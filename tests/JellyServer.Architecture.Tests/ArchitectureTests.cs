using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace JellyServer.Architecture.Tests;

/// <summary>
/// The modular monolith's rules, checked on the project files and sources (no assemblies are loaded):
/// <list type="bullet">
/// <item>a module's implementation sees other modules only through their <c>.Contracts</c> projects;</item>
/// <item>contracts are plain: they know the Kernel and other contracts, nothing else;</item>
/// <item>scanners read files and emit items: no module implementation, no database;</item>
/// <item>metadata providers talk to their service only: Kernel and contracts, no database, no other provider;</item>
/// <item>only the host sees everything, and nothing references the host;</item>
/// <item>namespaces equal the project they live in.</item>
/// </list>
/// </summary>
public class ArchitectureTests
{
    private const string Kernel = "JellyServer.Kernel";
    private const string Host = "JellyServer";
    private static readonly string[] ModuleImpls = ["JellyServer.Identity", "JellyServer.Settings", "JellyServer.UserData", "JellyServer.Catalog", "JellyServer.Metadata"];

    private sealed record Proj(string Name, string Dir, string[] References, string[] Packages)
    {
        public bool IsContracts => Name.EndsWith(".Contracts", StringComparison.Ordinal);
        public bool IsModule => ModuleImpls.Contains(Name);
        public bool IsScanning => Name == "JellyServer.Scanning";
        public bool IsScanner => Name.StartsWith("JellyServer.Scanners.", StringComparison.Ordinal);
        public bool IsProvider => Name.StartsWith("JellyServer.Providers.", StringComparison.Ordinal);
        public bool IsTranscoding => Name.StartsWith("JellyServer.Transcoding", StringComparison.Ordinal);
    }

    private static readonly Lazy<(string Root, List<Proj> Projects)> Solution = new(Load);

    private static (string, List<Proj>) Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "JellyServer.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("JellyServer.slnx not found above " + AppContext.BaseDirectory);
        var projects = new List<Proj>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(file);
            var refs = xml.Descendants("ProjectReference").Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/'))).ToArray();
            var pkgs = xml.Descendants("PackageReference").Select(r => (string)r.Attribute("Include")!).ToArray();
            projects.Add(new Proj(Path.GetFileNameWithoutExtension(file), Path.GetDirectoryName(file)!, refs, pkgs));
        }
        return (root, projects);
    }

    private static List<Proj> All => Solution.Value.Projects;

    private static IEnumerable<string> Sources(Proj p) =>
        Directory.EnumerateFiles(p.Dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static void AssertNoViolations(List<string> violations) =>
        Assert.True(violations.Count == 0, "Architecture violations:\n  " + string.Join("\n  ", violations));

    [Fact]
    public void ProjectsAreDiscovered() => Assert.Contains(All, p => p.Name == "JellyServer.Catalog.Contracts");

    [Fact]
    public void Kernel_DependsOnNothing() =>
        AssertNoViolations(All.Where(p => p.Name == Kernel).SelectMany(p => p.References.Select(r => $"{p.Name} -> {r}")).ToList());

    [Fact]
    public void Contracts_KnowOnlyKernelAndOtherContracts()
    {
        var violations = All.Where(p => p.IsContracts)
            .SelectMany(p => p.References.Where(r => r != Kernel && !r.EndsWith(".Contracts", StringComparison.Ordinal)).Select(r => $"{p.Name} -> {r}"))
            .ToList();
        AssertNoViolations(violations);
    }

    [Fact]
    public void ModuleImplementations_ReachOtherModulesOnlyThroughContracts()
    {
        var violations = new List<string>();
        foreach (var p in All.Where(p => p.IsModule))
            foreach (var r in p.References)
            {
                var ok = r == Kernel || r.EndsWith(".Contracts", StringComparison.Ordinal);
                if (!ok) violations.Add($"{p.Name} -> {r} (a module may only reference Kernel and *.Contracts)");
            }
        AssertNoViolations(violations);
    }

    [Fact]
    public void Scanning_ReferencesOnlyContracts()
    {
        var violations = All.Where(p => p.IsScanning)
            .SelectMany(p => p.References.Where(r => r != Kernel && !r.EndsWith(".Contracts", StringComparison.Ordinal)).Select(r => $"{p.Name} -> {r}"))
            .ToList();
        AssertNoViolations(violations);
    }

    [Fact]
    public void Scanners_AreIndependentOfModulesAndEachOther()
    {
        var violations = new List<string>();
        foreach (var p in All.Where(p => p.IsScanner))
            foreach (var r in p.References)
            {
                var allowed = r == Kernel || r == "JellyServer.Scanning" || r.EndsWith(".Contracts", StringComparison.Ordinal)
                    // Mixed libraries are composed of the movie and TV scanners.
                    || (p.Name == "JellyServer.Scanners.Mixed" && r is "JellyServer.Scanners.Movies" or "JellyServer.Scanners.TvShows");
                if (!allowed) violations.Add($"{p.Name} -> {r}");
            }
        AssertNoViolations(violations);
    }

    [Fact]
    public void Providers_KnowOnlyKernelAndContracts()
    {
        var violations = All.Where(p => p.IsProvider)
            .SelectMany(p => p.References.Where(r => r != Kernel && !r.EndsWith(".Contracts", StringComparison.Ordinal)).Select(r => $"{p.Name} -> {r}"))
            .ToList();
        AssertNoViolations(violations);
    }

    [Fact]
    public void NothingReferencesTheHost()
    {
        var violations = All.Where(p => p.Name != Host).SelectMany(p => p.References.Where(r => r == Host).Select(r => $"{p.Name} -> {r}")).ToList();
        AssertNoViolations(violations);
    }

    [Fact]
    public void ScannersProvidersAndContracts_HaveNoDatabaseCode()
    {
        var banned = new Regex(@"^\s*using\s+(Npgsql|Dapper|StackExchange\.Redis)\b", RegexOptions.Multiline);
        var violations = new List<string>();
        foreach (var p in All.Where(p => p.IsScanner || p.IsScanning || p.IsContracts || p.IsProvider))
        {
            foreach (var pkg in p.Packages.Where(k => k is "Npgsql" or "Dapper" or "StackExchange.Redis")) violations.Add($"{p.Name} references package {pkg}");
            foreach (var f in Sources(p).Where(f => banned.IsMatch(File.ReadAllText(f)))) violations.Add($"{p.Name}: {Path.GetFileName(f)} uses a database/cache namespace");
        }
        AssertNoViolations(violations);
    }

    [Fact]
    public void Namespaces_MatchTheirProject()
    {
        var violations = new List<string>();
        var decl = new Regex(@"^namespace\s+([\w.]+)\s*[;{]", RegexOptions.Multiline);
        foreach (var p in All.Where(p => !p.IsTranscoding))
            foreach (var f in Sources(p))
                foreach (Match m in decl.Matches(File.ReadAllText(f)))
                {
                    var ns = m.Groups[1].Value;
                    if (ns != p.Name && !ns.StartsWith(p.Name + ".", StringComparison.Ordinal))
                        violations.Add($"{p.Name}: {Path.GetFileName(f)} declares namespace {ns}");
                }
        AssertNoViolations(violations);
    }

    [Fact]
    public void EveryModuleProjectHasContractsOrIsContractless()
    {
        // Data modules that other modules or the host build upon expose a *.Contracts project next to them.
        foreach (var module in ModuleImpls)
            Assert.Contains(All, p => p.Name == module + ".Contracts");
    }
}