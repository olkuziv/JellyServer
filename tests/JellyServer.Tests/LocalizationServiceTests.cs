using System.Text.Json;
using JellyServer.Localization;
using Xunit;

namespace JellyServer.Tests;

public class LocalizationServiceTests
{
    private static readonly LocalizationService Svc = new();

    private static JsonElement Parse(byte[] json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Cultures_AreDistinctSortedAndHaveIsoCodes()
    {
        var all = Parse(Svc.CulturesJson).EnumerateArray().ToList();
        Assert.True(all.Count > 300);
        var names = all.Select(c => c.GetProperty("DisplayName").GetString()!).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(names.OrderBy(n => n, StringComparer.InvariantCulture), names);

        var ukr = all.Single(c => c.GetProperty("DisplayName").GetString() == "Ukrainian");
        Assert.Equal("uk", ukr.GetProperty("TwoLetterISOLanguageName").GetString());
        Assert.Equal("ukr", ukr.GetProperty("ThreeLetterISOLanguageName").GetString());

        // Two 639-2 codes: T first, then B (German: deu/ger).
        var ger = all.Single(c => c.GetProperty("DisplayName").GetString() == "German");
        Assert.Equal(["deu", "ger"], ger.GetProperty("ThreeLetterISOLanguageNames").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Countries_ContainUkraine()
    {
        var all = Parse(Svc.CountriesJson).EnumerateArray().ToList();
        Assert.True(all.Count == 140);
        Assert.Contains(all, c => c.GetProperty("TwoLetterISORegionName").GetString() == "UA" && c.GetProperty("ThreeLetterISORegionName").GetString() == "UKR");
    }

    [Fact]
    public void Options_ContainEnglishAndUkrainian()
    {
        var all = Parse(Svc.OptionsJson).EnumerateArray().Select(o => o.GetProperty("Value").GetString()).ToList();
        Assert.Contains("en-US", all);
        Assert.Contains("uk", all);
    }

    [Theory]
    [InlineData("US")]
    [InlineData(null)]
    [InlineData("zz")] // unknown country still gets the standard extras
    public void ParentalRatings_AreOrderedAndIncludeStandardExtras(string? country)
    {
        var r = Parse(Svc.ParentalRatingsJson(country)).EnumerateArray().ToList();
        var names = r.Select(x => x.GetProperty("Name").GetString()).ToList();
        Assert.Equal("Unrated", names[0]);
        Assert.Equal("Banned", names[^1]);
        Assert.Contains("XXX", names);
        var scores = r.Skip(1).Select(x => x.GetProperty("Value").GetInt32()).ToList();
        Assert.Equal(scores.OrderBy(s => s), scores);
    }

    [Fact]
    public void ParentalRatings_UsesCountrySystem()
    {
        var us = Parse(Svc.ParentalRatingsJson("us")).EnumerateArray().Select(x => x.GetProperty("Name").GetString()).ToList();
        Assert.Contains("PG-13", us);
        Assert.Contains("TV-MA", us);
    }
}
