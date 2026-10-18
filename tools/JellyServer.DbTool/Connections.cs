using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Npgsql;

namespace JellyServer.DbTool;

/// <summary>SSH jump settings for a database that is only reachable from inside a private network.</summary>
public sealed record SshOptions(string Host, string? User, int Port, string? Key);

/// <summary>A resolved connection: the Npgsql string to use plus, when it goes through a tunnel, the original target.</summary>
public sealed record DbConnection(string Label, NpgsqlConnectionStringBuilder Builder, SshOptions? Ssh)
{
    /// <summary>Connection string with the secret hidden, safe to print.</summary>
    public string Masked()
    {
        var b = new NpgsqlConnectionStringBuilder(Builder.ConnectionString);
        if (!string.IsNullOrEmpty(b.Password)) b.Password = "***";
        return b.ConnectionString;
    }

    public string Where => $"{Builder.Host}:{Builder.Port}/{Builder.Database}";
}

/// <summary>
/// Turns what the user typed into a connection. A reference is one of: a name from the config file, <c>env:VAR</c>
/// (the variable holds the connection string, so the password stays out of shell history), <c>file:path</c>,
/// a <c>postgresql://</c> URI, or a plain Npgsql connection string.
/// </summary>
public sealed class ConnectionResolver
{
    private readonly Dictionary<string, (string Reference, SshOptions? Ssh)> _named = new(StringComparer.OrdinalIgnoreCase);
    private readonly SshOptions? _defaultSsh;

    public ConnectionResolver(string? configPath, SshOptions? defaultSsh)
    {
        _defaultSsh = defaultSsh;
        if (configPath is null) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("connections", out var list)) return;
        foreach (var c in list.EnumerateObject())
        {
            var connection = c.Value.TryGetProperty("connection", out var cs) ? cs.GetString() : null;
            if (string.IsNullOrWhiteSpace(connection)) throw new ToolException($"Config connection '{c.Name}' has no \"connection\".");
            SshOptions? ssh = null;
            if (c.Value.TryGetProperty("ssh", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                var host = s.TryGetProperty("host", out var h) ? h.GetString() : null;
                if (string.IsNullOrWhiteSpace(host)) throw new ToolException($"Config connection '{c.Name}': ssh.host is missing.");
                ssh = new SshOptions(host, s.TryGetProperty("user", out var u) ? u.GetString() : null,
                    s.TryGetProperty("port", out var p) ? p.GetInt32() : 22, s.TryGetProperty("key", out var k) ? ExpandHome(k.GetString()) : null);
            }
            _named[c.Name] = (connection, ssh);
        }
    }

    public static bool LooksLikeConnection(string value) =>
        value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) || value.Contains('=');

    public DbConnection Resolve(string reference, string label)
    {
        var ssh = _defaultSsh;
        var text = reference.Trim();
        if (!LooksLikeConnection(text) && !text.StartsWith("env:", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!_named.TryGetValue(text, out var named))
                throw new ToolException($"'{text}' is not a connection string, env:VAR, file:PATH or a name from the config file.");
            text = named.Reference;
            ssh = named.Ssh ?? ssh;
        }

        if (text.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
        {
            var name = text[4..];
            text = Environment.GetEnvironmentVariable(name) ?? throw new ToolException($"Environment variable {name} is not set.");
        }
        else if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = ExpandHome(text[5..])!;
            text = File.Exists(path) ? File.ReadAllText(path).Trim() : throw new ToolException($"File {path} does not exist.");
        }

        return new DbConnection(label, Parse(text), ssh);
    }

    /// <summary>Effective connection of a module in a running server's configuration: <c>ConnectionStrings__module</c>, else <c>ConnectionStrings__Postgres</c>.</summary>
    public DbConnection? FromServerEnvironment(string module)
    {
        foreach (var name in new[] { module, "Postgres" })
        {
            var value = Environment.GetEnvironmentVariable("ConnectionStrings__" + name);
            if (value is null)
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                    if (string.Equals((string)e.Key, "ConnectionStrings__" + name, StringComparison.OrdinalIgnoreCase)) { value = (string?)e.Value; break; }
            if (!string.IsNullOrWhiteSpace(value)) return new DbConnection(module, Parse(value), _defaultSsh);
        }
        return null;
    }

    public static NpgsqlConnectionStringBuilder Parse(string text)
    {
        text = text.Trim();
        if (!(text.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)))
            return new NpgsqlConnectionStringBuilder(text);

        var uri = new Uri(text);
        var b = new NpgsqlConnectionStringBuilder { Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432, Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')) };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            b.Username = Uri.UnescapeDataString(parts[0]);
            if (parts.Length > 1) b.Password = Uri.UnescapeDataString(parts[1]);
        }
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            b[Uri.UnescapeDataString(kv[0]).Replace("sslmode", "SSL Mode")] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }
        return b;
    }

    private static string? ExpandHome(string? path) =>
        path is not null && path.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\')) : path;
}

/// <summary>Opens each distinct database once (same host, port, database, user and tunnel share a data source) and closes any SSH tunnels at the end.</summary>
public sealed class ConnectionPool : IAsyncDisposable
{
    private readonly Dictionary<string, NpgsqlDataSource> _sources = new();
    private readonly Dictionary<string, SshTunnel> _tunnels = new();

    /// <summary>A data source whose <c>search_path</c> is <paramref name="schema"/> (so unqualified table names are the module's).</summary>
    public async Task<NpgsqlDataSource> OpenAsync(DbConnection connection, string? schema, CancellationToken ct)
    {
        var b = new NpgsqlConnectionStringBuilder(connection.Builder.ConnectionString)
        {
            ApplicationName = "jellyserver-dbtool",
            MaxPoolSize = 4,
            Pooling = true,
            Timeout = 30,
            CommandTimeout = 0,
        };
        if (schema is not null) b.SearchPath = schema;

        if (connection.Ssh is { } ssh)
        {
            var key = $"{ssh.User}@{ssh.Host}:{ssh.Port}>{connection.Builder.Host}:{connection.Builder.Port}";
            if (!_tunnels.TryGetValue(key, out var tunnel))
                _tunnels[key] = tunnel = await SshTunnel.StartAsync(ssh, connection.Builder.Host!, connection.Builder.Port, ct);
            b.Host = "127.0.0.1";
            b.Port = tunnel.LocalPort;
            // The certificate is issued for the real host name, which is not what we connect to through the tunnel.
            if (b.SslMode == SslMode.VerifyFull) b.SslMode = SslMode.VerifyCA;
        }

        var id = b.ConnectionString;
        if (!_sources.TryGetValue(id, out var source)) _sources[id] = source = NpgsqlDataSource.Create(id);
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sources.Values) await s.DisposeAsync();
        foreach (var t in _tunnels.Values) t.Dispose();
    }
}

/// <summary>An <c>ssh -N -L</c> process to a bastion that can reach the database host. Uses the system ssh client and its keys/agent.</summary>
public sealed class SshTunnel : IDisposable
{
    private readonly Process _process;
    public int LocalPort { get; }

    private SshTunnel(Process process, int localPort)
    {
        _process = process;
        LocalPort = localPort;
    }

    public static async Task<SshTunnel> StartAsync(SshOptions ssh, string remoteHost, int remotePort, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var psi = new ProcessStartInfo("ssh") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-N", "-o", "ExitOnForwardFailure=yes", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new",
                     "-o", "ServerAliveInterval=30", "-L", $"127.0.0.1:{port}:{remoteHost}:{remotePort}", "-p", ssh.Port.ToString() })
            psi.ArgumentList.Add(a);
        if (ssh.Key is not null) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(ssh.Key); psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("IdentitiesOnly=yes"); }
        psi.ArgumentList.Add(ssh.User is null ? ssh.Host : $"{ssh.User}@{ssh.Host}");

        Process process;
        try { process = Process.Start(psi)!; }
        catch (Exception ex) { throw new ToolException($"Cannot start the 'ssh' client ({ex.Message}). Install OpenSSH or connect to the database directly."); }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw new ToolException($"SSH tunnel to {ssh.Host} failed: {(await process.StandardError.ReadToEndAsync(ct)).Trim()}");
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, ct);
                return new SshTunnel(process, port);
            }
            catch (SocketException) { await Task.Delay(250, ct); }
        }
        try { process.Kill(); } catch { /* already gone */ }
        throw new ToolException($"SSH tunnel to {ssh.Host} did not come up within 20 s.");
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(true); } catch { /* already gone */ }
        _process.Dispose();
    }
}

/// <summary>An error with a message meant for the person running the tool (no stack trace).</summary>
public sealed class ToolException(string message) : Exception(message);
