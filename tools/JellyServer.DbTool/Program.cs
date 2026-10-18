using Npgsql;

namespace JellyServer.DbTool;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            return await RunAsync(args, Console.Out, cts.Token);
        }
        catch (ToolException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled.");
            return 130;
        }
        catch (NpgsqlException ex)
        {
            Console.Error.WriteLine("database error: " + ex.Message);
            return 1;
        }
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var cli = Cli.Parse(args);
        if (cli.Command is null or "help" || cli.Flag("help"))
        {
            output.WriteLine(Cli.Usage);
            return cli.Command is null or "help" ? 0 : 0;
        }

        var ssh = cli.Value("ssh") is { } target ? ParseSsh(target, cli.Value("ssh-key")) : null;
        var resolver = new ConnectionResolver(cli.Value("config"), ssh);
        await using var pool = new ConnectionPool();

        switch (cli.Command)
        {
            case "modules":
                foreach (var s in ModuleCatalog.Specs)
                    output.WriteLine($"{s.Module,-14} schema \"{s.Schema}\"  {Migrations(s)} migrations");
                return 0;

            case "status":
                await StatusCommand.RunAsync(cli, resolver, pool, output, ct);
                return 0;

            case "merge":
                return await MoveCommand.RunAsync(MoveCommand.PlanMerge(cli, resolver), cli, pool, output, ct);

            case "split":
                return await MoveCommand.RunAsync(MoveCommand.PlanSplit(cli, resolver), cli, pool, output, ct);

            default:
                throw new ToolException($"Unknown command '{cli.Command}'. Run 'dbtool help'.");
        }
    }

    private static int Migrations(Kernel.Data.ModuleDatabaseSpec s) => Kernel.Data.Migrator.ScriptNames(s).Count;

    private static SshOptions ParseSsh(string target, string? key)
    {
        var user = (string?)null;
        var host = target;
        if (host.Contains('@')) { user = host[..host.IndexOf('@')]; host = host[(host.IndexOf('@') + 1)..]; }
        var port = 22;
        if (host.Contains(':') && int.TryParse(host[(host.LastIndexOf(':') + 1)..], out var p)) { port = p; host = host[..host.LastIndexOf(':')]; }
        return new SshOptions(host, user, port, key);
    }
}

/// <summary>Arguments: a command, then <c>--name value</c> options (repeatable) and <c>--flag</c> switches.</summary>
public sealed class Cli
{
    private static readonly HashSet<string> Flags = ["dry-run", "yes", "replace", "drop-source", "ignore-running", "env", "help"];

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public string? Command { get; private init; }

    public static Cli Parse(string[] args)
    {
        var cli = new Cli { Command = args.FirstOrDefault(a => !a.StartsWith('-')) };
        var skipped = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!skipped && !a.StartsWith('-')) { skipped = true; continue; }
            if (a == "-y") a = "--yes";
            if (a == "-h") a = "--help";
            if (!a.StartsWith("--")) throw new ToolException($"Unexpected argument '{a}'.");
            var name = a[2..];
            var eq = name.IndexOf('=');
            if (eq > 0 && !Flags.Contains(name[..eq])) { cli.Add(name[..eq], name[(eq + 1)..]); continue; }
            if (Flags.Contains(name)) { cli._flags.Add(name); continue; }
            if (i + 1 >= args.Length) throw new ToolException($"Option --{name} needs a value.");
            cli.Add(name, args[++i]);
        }
        return cli;
    }

    private void Add(string name, string value)
    {
        if (!_values.TryGetValue(name, out var list)) _values[name] = list = [];
        list.Add(value);
    }

    public bool Flag(string name) => _flags.Contains(name);
    public string? Value(string name) => _values.TryGetValue(name, out var l) ? l[^1] : null;
    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var l) ? l : [];

    public const string Usage = """
        dbtool - move JellyServer module schemas between PostgreSQL databases

        Usage
          dbtool modules                         list the modules that own a schema
          dbtool status [--env] [--db REF ...]   which module schemas live in which database (sizes, migrations, running server)
          dbtool merge  --from MODULE[=REF] ... --to REF        gather module schemas into one database
          dbtool split  [--from REF] --to MODULE=REF ...        move module schemas out to their own databases

        REF is a connection: 'Host=..;Database=..;Username=..;Password=..', a postgresql:// URI, env:VAR (the variable holds
        the connection), file:PATH, or a name from --config. A MODULE without =REF is read from the server's own
        configuration (ConnectionStrings__<module>, else ConnectionStrings__Postgres) in the environment, so you can reuse
        the settings your server already has. 'split' without --from does the same.

        Options
          --config FILE       JSON with named connections: { "connections": { "name": { "connection": "env:VAR",
                              "ssh": { "host": "bastion", "user": "me", "port": 22, "key": "~/.ssh/id" } } } }
          --ssh [USER@]HOST[:PORT] [--ssh-key FILE]
                              reach every database through an SSH jump host (the system 'ssh' client is used)
          --dry-run           show what would be moved and stop
          -y, --yes           do not ask for confirmation
          --replace           drop an existing, non-empty schema on the target first
          --drop-source       after a verified copy, drop the schema on the source
          --ignore-running    copy even though a JellyServer is connected to the source (a consistent snapshot, but later writes are lost)

        Stop the server before moving data. The source is only read; nothing is committed on the target unless every row
        count matches. Afterwards the tool prints the ConnectionStrings__ settings the server needs.
        """;
}
