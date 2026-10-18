using JellyServer.DbTool;
using Npgsql;
using Xunit;

namespace JellyServer.DbTool.Tests;

public class MoveTests
{
    // access_tokens sorts before users alphabetically but references it: the copy has to put users first.
    private const string User1 = "insert into users(id, name, is_admin) values ('11111111-1111-1111-1111-111111111111', 'ann', true)";
    private const string User2 = "insert into users(id, name) values ('22222222-2222-2222-2222-222222222222', 'bob')";
    private const string Tokens = """
        insert into access_tokens(token, user_id) values
          ('t1', '11111111-1111-1111-1111-111111111111'), ('t2', '11111111-1111-1111-1111-111111111111'), ('t3', '22222222-2222-2222-2222-222222222222')
        """;

    private static async Task<(int Exit, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        try
        {
            return (await Program.RunAsync(args, output, CancellationToken.None), output.ToString());
        }
        catch (ToolException ex)
        {
            return (1, output + "error: " + ex.Message);
        }
    }

    private static async Task<TwoDatabases> SeededAsync()
    {
        var dbs = new TwoDatabases();
        await TwoDatabases.SeedAsync(dbs.Source, "identity", User1, User2, Tokens);
        await TwoDatabases.SeedAsync(dbs.Source, "settings", "insert into server_configuration(key, data) values ('encoding', '{\"a\":1}'), ('network', '{}')");
        return dbs;
    }

    [PgFact]
    public async Task Merge_copies_every_row_in_foreign_key_order_and_keeps_migration_history()
    {
        await using var dbs = await SeededAsync();
        var (exit, output) = await RunAsync("merge", "--from", $"identity={dbs.Source}", "--from", $"settings={dbs.Source}", "--to", dbs.Target, "--yes");

        Assert.True(exit == 0, output);
        Assert.Equal(2, await TwoDatabases.CountAsync(dbs.Target, "identity", "users"));
        Assert.Equal(3, await TwoDatabases.CountAsync(dbs.Target, "identity", "access_tokens"));
        Assert.Equal(2, await TwoDatabases.CountAsync(dbs.Target, "settings", "server_configuration"));
        Assert.Equal(1, await TwoDatabases.CountAsync(dbs.Target, "identity", "schema_migrations"));
        // The source is left alone.
        Assert.Equal(3, await TwoDatabases.CountAsync(dbs.Source, "identity", "access_tokens"));
        Assert.DoesNotContain(dbs.Source, output); // no connection string (with its password) is echoed in full
    }

    [PgFact]
    public async Task Dry_run_writes_nothing()
    {
        await using var dbs = await SeededAsync();
        var (exit, output) = await RunAsync("merge", "--from", $"identity={dbs.Source}", "--to", dbs.Target, "--dry-run");

        Assert.True(exit == 0, output);
        Assert.Contains("users", output);
        Assert.False(await TwoDatabases.SchemaExistsAsync(dbs.Target, "identity"));
    }

    [PgFact]
    public async Task Refuses_a_target_that_already_has_data_unless_replace_is_given()
    {
        await using var dbs = await SeededAsync();
        await TwoDatabases.SeedAsync(dbs.Target, "settings", "insert into server_configuration(key, data) values ('old', '{}')");

        var (exit, output) = await RunAsync("merge", "--from", $"settings={dbs.Source}", "--to", dbs.Target, "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("--replace", output);
        Assert.Equal(1, await TwoDatabases.CountAsync(dbs.Target, "settings", "server_configuration"));

        (exit, output) = await RunAsync("merge", "--from", $"settings={dbs.Source}", "--to", dbs.Target, "--yes", "--replace");
        Assert.True(exit == 0, output);
        Assert.Equal(2, await TwoDatabases.CountAsync(dbs.Target, "settings", "server_configuration"));
    }

    [PgFact]
    public async Task Refuses_while_a_server_is_connected_to_the_source()
    {
        await using var dbs = await SeededAsync();
        await using var server = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(dbs.Source) { ApplicationName = "jellyserver-identity" }.ConnectionString);
        await server.OpenAsync();

        var (exit, output) = await RunAsync("merge", "--from", $"identity={dbs.Source}", "--to", dbs.Target, "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("Stop the server", output);
        Assert.False(await TwoDatabases.SchemaExistsAsync(dbs.Target, "identity"));

        (exit, output) = await RunAsync("merge", "--from", $"identity={dbs.Source}", "--to", dbs.Target, "--yes", "--ignore-running");
        Assert.True(exit == 0, output);
        Assert.Equal(2, await TwoDatabases.CountAsync(dbs.Target, "identity", "users"));
    }

    [PgFact]
    public async Task A_source_with_a_different_migration_history_is_rejected_and_nothing_is_created()
    {
        await using var dbs = await SeededAsync();
        await using (var ds = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(dbs.Source) { SearchPath = "settings" }.ConnectionString))
            await TwoDatabases.ExecAsync(ds, "insert into schema_migrations(name) values ('999_from_the_future.sql')");

        var (exit, output) = await RunAsync("merge", "--from", $"settings={dbs.Source}", "--to", dbs.Target, "--yes");
        Assert.NotEqual(0, exit);
        Assert.Contains("999_from_the_future.sql", output);
        Assert.False(await TwoDatabases.SchemaExistsAsync(dbs.Target, "settings"));
    }

    [PgFact]
    public async Task Split_moves_a_schema_out_and_drop_source_removes_it_after_verification()
    {
        await using var dbs = await SeededAsync();
        var (exit, output) = await RunAsync("split", "--from", dbs.Source, "--to", $"identity={dbs.Target}", "--yes", "--drop-source");

        Assert.True(exit == 0, output);
        Assert.Equal(3, await TwoDatabases.CountAsync(dbs.Target, "identity", "access_tokens"));
        Assert.False(await TwoDatabases.SchemaExistsAsync(dbs.Source, "identity"));
        Assert.True(await TwoDatabases.SchemaExistsAsync(dbs.Source, "settings")); // other modules stay
    }

    [PgFact]
    public async Task Connection_can_come_from_an_environment_variable_and_the_servers_own_settings()
    {
        await using var dbs = await SeededAsync();
        var variable = "DBTOOL_TEST_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, dbs.Target);
        Environment.SetEnvironmentVariable("ConnectionStrings__settings", dbs.Source);
        try
        {
            var (exit, output) = await RunAsync("merge", "--from", "settings", "--to", "env:" + variable, "--yes");
            Assert.True(exit == 0, output);
            Assert.Equal(2, await TwoDatabases.CountAsync(dbs.Target, "settings", "server_configuration"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Environment.SetEnvironmentVariable("ConnectionStrings__settings", null);
        }
    }

    [PgFact]
    public async Task Every_module_can_be_moved_there_and_back()
    {
        await using var dbs = new TwoDatabases();
        foreach (var spec in ModuleCatalog.Specs) await TwoDatabases.SeedAsync(dbs.Source, spec.Module);
        var from = ModuleCatalog.Specs.SelectMany(s => new[] { "--from", $"{s.Module}={dbs.Source}" }).ToArray();

        var (exit, output) = await RunAsync(["merge", .. from, "--to", dbs.Target, "--yes"]);
        Assert.True(exit == 0, output);
        foreach (var spec in ModuleCatalog.Specs) Assert.True(await TwoDatabases.SchemaExistsAsync(dbs.Target, spec.Schema), spec.Module);

        var back = ModuleCatalog.Specs.SelectMany(s => new[] { "--to", $"{s.Module}={dbs.Source}" }).ToArray();
        (exit, output) = await RunAsync(["split", "--from", dbs.Target, .. back, "--yes", "--replace"]);
        Assert.True(exit == 0, output);
    }

    [PgFact]
    public async Task Status_lists_the_module_schemas_of_a_database()
    {
        await using var dbs = await SeededAsync();
        var (exit, output) = await RunAsync("status", "--db", dbs.Source);

        Assert.True(exit == 0, output);
        Assert.Contains("identity", output);
        Assert.Contains("settings", output);
        Assert.Contains("1/1 migrations", output);
    }
}
