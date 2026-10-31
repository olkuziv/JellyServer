using JellyServer.Plugins;
using JellyServer.Plugins.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JellyServer.Tests;

public sealed class PluginSettingsTests
{
    private sealed class FakePlugin : IServerPlugin
    {
        public string Id => "fake";
        public string Name => "Fake <Plugin>";
        public string Version => "1.0.0";
        public void Register(IPluginContext context) { }
    }

    private static IConfiguration HostSection(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    private static PluginContext Context(params (string Key, string Value)[] host) => new("fake", Path.GetTempPath(), HostSection(host));

    private static PluginSettingsSection Section(string? prefix = null) => new("General", [
        new("Delay", "Delay", PluginSettingType.Number, "330", Min: 0, Max: 1000),
        new("Key", "Key", PluginSettingType.Secret),
        new("Mode", "Mode", PluginSettingType.Select, "fast", Options: ["fast", "slow"]),
        new("Verbose", "Verbose", PluginSettingType.Toggle),
    ], prefix);

    [Fact]
    public void Configuration_LayersDefaults_ThenHost_ThenSavedValues()
    {
        var context = Context(("Delay", "500"));
        context.AddSettingsSection(Section());

        Assert.Equal("500", context.Configuration["Delay"]);       // the environment beats the default
        Assert.Equal("fast", context.Configuration["Mode"]);       // nothing else sets it: the default

        context.ApplyOverrides([KeyValuePair.Create("Delay", "100"), KeyValuePair.Create("Mode", "slow")]);
        Assert.Equal("100", context.Configuration["Delay"]);       // what the administrator saved beats both
        Assert.Equal("slow", context.Configuration["Mode"]);

        context.ApplyOverrides([]);
        Assert.Equal("500", context.Configuration["Delay"]);       // dropping the override falls back
    }

    [Fact]
    public void Configuration_KeepsPrefixedKeysReadableAsASection()
    {
        var context = Context();
        context.AddSettingsSection(Section("Profiles:anime"));
        context.ApplyOverrides([KeyValuePair.Create("Profiles:anime:Delay", "7")]);

        var section = context.Configuration.GetSection("Profiles:anime");
        Assert.Equal("7", section["Delay"]);
        Assert.Equal("fast", section["Mode"]);
    }

    [Fact]
    public void Sections_RejectBadKeysAndDuplicates()
    {
        var context = Context();
        Assert.Throws<ArgumentException>(() => context.AddSettingsSection(new PluginSettingsSection("x", [new("bad key", "x")])));
        Assert.Throws<ArgumentException>(() => context.AddSettingsSection(new PluginSettingsSection("x", [new("A", "x")], "bad prefix!")));
        Assert.Throws<ArgumentException>(() => context.AddSettingsSection(new PluginSettingsSection("x", [new("S", "x", PluginSettingType.Select)])));

        context.AddSettingsSection(new PluginSettingsSection("x", [new("A", "x")]));
        Assert.Throws<ArgumentException>(() => context.AddSettingsSection(new PluginSettingsSection("y", [new("A", "y")])));
    }

    [Theory]
    [InlineData(PluginSettingType.Number, "42", null, "42")]
    [InlineData(PluginSettingType.Number, "042", null, "42")]
    [InlineData(PluginSettingType.Number, "abc", "not a whole number", "abc")]
    [InlineData(PluginSettingType.Number, "5000", "at most 1000", "5000")]
    [InlineData(PluginSettingType.Number, "", "a value is required", "")]
    [InlineData(PluginSettingType.Toggle, "TRUE", null, "true")]
    [InlineData(PluginSettingType.Toggle, "maybe", "must be true or false", "maybe")]
    [InlineData(PluginSettingType.Select, "SLOW", null, "slow")]
    [InlineData(PluginSettingType.Select, "medium", "not one of the allowed values", "medium")]
    [InlineData(PluginSettingType.Text, "anything", null, "anything")]
    public void Validate_ChecksTypeAndBounds(PluginSettingType type, string value, string? problem, string stored)
    {
        var setting = type switch
        {
            PluginSettingType.Number => new PluginSetting("K", "K", type, "330", Min: 0, Max: 1000),
            PluginSettingType.Select => new PluginSetting("K", "K", type, "fast", Options: ["fast", "slow"]),
            PluginSettingType.Toggle => new PluginSetting("K", "K", type, "false"),
            _ => new PluginSetting("K", "K", type),
        };

        Assert.Equal(problem, PluginSettingsService.Validate(setting, value, out var normalized));
        Assert.Equal(stored, normalized);
    }

    [Fact]
    public void Get_ShowsDefaultsAndEffectiveValues_AndMasksSecrets()
    {
        var context = Context(("Key", "from-env"));
        context.AddSettingsSection(Section());
        var plugin = new LoadedPlugin(new FakePlugin(), context, "Fake.dll", null, new PluginMeta { Guid = Guid.NewGuid(), Name = "Fake" });
        var service = new PluginSettingsService(new PluginLoadReport([plugin], [], "x"), null!, NullLogger<PluginSettingsService>.Instance);

        var dto = service.Get(plugin.Meta.Guid)!;
        var fields = dto.Sections.Single().Fields.ToDictionary(f => f.Key);

        Assert.Equal("330", fields["Delay"].Value);
        Assert.Equal("330", fields["Delay"].Default);
        Assert.False(fields["Delay"].Overridden);
        Assert.Equal(PluginSettingsService.Mask, fields["Key"].Value); // never sent in clear
        Assert.Equal("", fields["Key"].Default);
        Assert.Equal("false", fields["Verbose"].Value);
        Assert.Equal(["fast", "slow"], fields["Mode"].Options);

        Assert.Null(service.Get(Guid.NewGuid()));
        Assert.Equal(plugin.Meta.Guid.ToString("N"), Assert.Single(service.Pages(null)).PluginId);
        Assert.Empty(service.Pages(false));
    }

    [Fact]
    public void Pages_OnlyListPluginsThatDeclareSettings()
    {
        var withSettings = Context();
        withSettings.AddSettingsSection(Section());
        var a = new LoadedPlugin(new FakePlugin(), withSettings, "a.dll", null, new PluginMeta { Guid = Guid.NewGuid(), Name = "A" });
        var b = new LoadedPlugin(new FakePlugin(), Context(), "b.dll", null, new PluginMeta { Guid = Guid.NewGuid(), Name = "B" });
        var service = new PluginSettingsService(new PluginLoadReport([a, b], [], "x"), null!, NullLogger<PluginSettingsService>.Instance);

        Assert.Equal(a.Meta.Guid.ToString("N"), Assert.Single(service.Pages(null)).PluginId);
        Assert.NotNull(service.FindPageByName("fake <plugin>"));
    }

    [Fact]
    public void Page_EscapesThePluginName()
    {
        var html = PluginSettingsPage.Render("Evil \"><script>alert(1)</script>", Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("11111111111111111111111111111111", html);
        Assert.Contains("data-role=\"page\"", html);
    }
}
