namespace JellyServer.DbTool;

/// <summary>Builds the list of moves for <c>merge</c> and <c>split</c> and runs them one module at a time.</summary>
public static class MoveCommand
{
    public static List<MoveRequest> PlanMerge(Cli cli, ConnectionResolver resolver)
    {
        var to = cli.Value("to") ?? throw new ToolException("merge needs --to REF (the database that receives the schemas).");
        var target = resolver.Resolve(to, "target");
        var froms = cli.Values("from");
        if (froms.Count == 0) throw new ToolException("merge needs at least one --from MODULE[=REF].");

        var moves = new List<MoveRequest>();
        foreach (var from in froms)
        {
            var (module, reference) = SplitModule(from);
            var spec = ModuleCatalog.Find(module) ?? throw UnknownModule(module);
            var source = reference is null ? FromServerConfig(resolver, spec.Module) : resolver.Resolve(reference, spec.Module);
            moves.Add(new MoveRequest(spec, source, target));
        }
        return Checked(moves);
    }

    public static List<MoveRequest> PlanSplit(Cli cli, ConnectionResolver resolver)
    {
        var tos = cli.Values("to");
        if (tos.Count == 0) throw new ToolException("split needs at least one --to MODULE=REF.");
        var shared = cli.Value("from") is { } f ? resolver.Resolve(f, "source") : null;

        var moves = new List<MoveRequest>();
        foreach (var to in tos)
        {
            var (module, reference) = SplitModule(to);
            var spec = ModuleCatalog.Find(module) ?? throw UnknownModule(module);
            if (reference is null) throw new ToolException($"--to {module} needs a database: --to {module}=REF.");
            var source = shared ?? FromServerConfig(resolver, spec.Module);
            moves.Add(new MoveRequest(spec, source, resolver.Resolve(reference, spec.Module + " target")));
        }
        return Checked(moves);
    }

    public static async Task<int> RunAsync(List<MoveRequest> moves, Cli cli, ConnectionPool pool, TextWriter output, CancellationToken ct)
    {
        var dryRun = cli.Flag("dry-run");
        var options = new MoveOptions(dryRun, cli.Flag("replace"), cli.Flag("drop-source"), cli.Flag("ignore-running"));

        // Everything is checked first (nothing is written), so a problem with the last module does not surface after the first moved.
        output.WriteLine($"Plan: {moves.Count} module(s)");
        foreach (var move in moves)
            await new Mover(pool, options with { DryRun = true }, output).MoveAsync(move, ct);
        if (dryRun) return 0;

        if (!cli.Flag("yes"))
        {
            if (Console.IsInputRedirected) throw new ToolException("Not confirmed: pass --yes to run without a prompt.");
            output.Write($"\nCopy {moves.Count} module(s)" + (options.DropSource ? " and DROP the schemas on the source" : "") + "? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) { output.WriteLine("Aborted."); return 1; }
        }

        output.WriteLine();
        foreach (var move in moves)
            await new Mover(pool, options, output).MoveAsync(move, ct);

        output.WriteLine("\nDone. Point the server at the new places (then start it):");
        foreach (var move in moves)
            output.WriteLine($"  ConnectionStrings__{move.Spec.Module}={move.To.Masked()}");
        output.WriteLine("(Use the real password, or remove a module's own setting when the target is your ConnectionStrings__Postgres.)");
        return 0;
    }

    private static DbConnection FromServerConfig(ConnectionResolver resolver, string module) =>
        resolver.FromServerEnvironment(module) ?? throw new ToolException(
            $"No source for '{module}': give --from {module}=REF, or set ConnectionStrings__{module} / ConnectionStrings__Postgres in the environment.");

    private static (string Module, string? Reference) SplitModule(string text)
    {
        var eq = text.IndexOf('=');
        return eq > 0 ? (text[..eq], text[(eq + 1)..]) : (text, null);
    }

    private static ToolException UnknownModule(string module) =>
        new($"Unknown module '{module}'. Known: {string.Join(", ", ModuleCatalog.Specs.Select(s => s.Module))}.");

    private static List<MoveRequest> Checked(List<MoveRequest> moves)
    {
        var dup = moves.GroupBy(m => m.Spec.Module).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new ToolException($"Module '{dup.Key}' is listed twice.");
        return moves;
    }
}
