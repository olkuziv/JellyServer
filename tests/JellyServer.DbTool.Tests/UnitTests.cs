using JellyServer.Composition;
using JellyServer.DbTool;
using JellyServer.Kernel;
using JellyServer.Kernel.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JellyServer.DbTool.Tests;

public class ModuleCatalogTests
{
    [Fact]
    public void The_tool_knows_exactly_the_schemas_the_server_creates()
    {
        var services = new ServiceCollection();
        foreach (var module in AllModules.Core) module.Register(services, new ConfigurationBuilder().Build());
        var server = services.Where(d => d.ServiceType == typeof(ModuleDatabaseSpec)).Select(d => ((ModuleDatabaseSpec)d.ImplementationInstance!).Module)
            .Order().ToList();

        Assert.Equal(server, ModuleCatalog.Specs.Select(s => s.Module).Order().ToList());
    }

    [Fact]
    public void Every_module_has_migrations()
    {
        foreach (var spec in ModuleCatalog.Specs) Assert.NotEmpty(Migrator.ScriptNames(spec));
    }
}

public class ConnectionResolverTests
{
    private static ConnectionResolver Resolver(string? config = null) => new(config, null);

    [Fact]
    public void A_uri_becomes_a_connection_string()
    {
        var c = Resolver().Resolve("postgresql://me:p%40ss@db.example.com:6543/jelly?sslmode=require", "t");

        Assert.Equal("db.example.com", c.Builder.Host);
        Assert.Equal(6543, c.Builder.Port);
        Assert.Equal("jelly", c.Builder.Database);
        Assert.Equal("me", c.Builder.Username);
        Assert.Equal("p@ss", c.Builder.Password);
        Assert.Equal(Npgsql.SslMode.Require, c.Builder.SslMode);
    }

    [Fact]
    public void The_password_is_hidden_when_printed()
    {
        var c = Resolver().Resolve("Host=h;Database=d;Username=u;Password=secret", "t");

        Assert.DoesNotContain("secret", c.Masked());
        Assert.Contains("***", c.Masked());
    }

    [Fact]
    public void Env_and_file_references_are_read_and_named_connections_come_from_the_config_file()
    {
        var variable = "DBTOOL_UNIT_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, "Host=fromenv;Database=d;Username=u;Password=p");
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "cs.txt"), "Host=fromfile;Database=d;Username=u;Password=p\n");
            File.WriteAllText(Path.Combine(dir.FullName, "cfg.json"), $$"""
                { "connections": { "core": { "connection": "env:{{variable}}", "ssh": { "host": "bastion", "user": "me", "port": 2222, "key": "k" } } } }
                """);
            var resolver = Resolver(Path.Combine(dir.FullName, "cfg.json"));

            Assert.Equal("fromenv", resolver.Resolve("env:" + variable, "t").Builder.Host);
            Assert.Equal("fromfile", resolver.Resolve("file:" + Path.Combine(dir.FullName, "cs.txt"), "t").Builder.Host);
            var named = resolver.Resolve("core", "t");
            Assert.Equal("fromenv", named.Builder.Host);
            Assert.Equal(new SshOptions("bastion", "me", 2222, "k"), named.Ssh);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            dir.Delete(true);
        }
    }

    [Fact]
    public void An_unknown_reference_is_an_error_not_a_guess()
    {
        Assert.Throws<ToolException>(() => Resolver().Resolve("nope", "t"));
        Assert.Throws<ToolException>(() => Resolver().Resolve("env:DBTOOL_DOES_NOT_EXIST_X", "t"));
    }

    [Fact]
    public void A_module_without_its_own_setting_falls_back_to_the_shared_postgres_setting()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", "Host=shared;Database=d;Username=u;Password=p");
        Environment.SetEnvironmentVariable("ConnectionStrings__media", "Host=own;Database=d;Username=u;Password=p");
        try
        {
            Assert.Equal("own", Resolver().FromServerEnvironment("media")!.Builder.Host);
            Assert.Equal("shared", Resolver().FromServerEnvironment("people")!.Builder.Host);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Postgres", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__media", null);
        }
    }
}

public class CliTests
{
    [Fact]
    public void Repeated_options_and_flags_are_parsed()
    {
        var cli = Cli.Parse(["merge", "--from", "a=1", "--from", "b", "--to", "Host=x;Database=y", "-y", "--drop-source"]);

        Assert.Equal("merge", cli.Command);
        Assert.Equal(["a=1", "b"], cli.Values("from"));
        Assert.Equal("Host=x;Database=y", cli.Value("to"));
        Assert.True(cli.Flag("yes"));
        Assert.True(cli.Flag("drop-source"));
        Assert.False(cli.Flag("replace"));
    }

    [Fact]
    public void An_option_without_a_value_is_an_error()
    {
        Assert.Throws<ToolException>(() => Cli.Parse(["merge", "--to"]));
    }
}
