using JellyServer.Scanning;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using JellyServer.Catalog.Contracts;

namespace JellyServer.Tests;

public class NfoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nfo").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private Item Apply(string xml)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".nfo");
        File.WriteAllText(path, xml);
        var it = new Item { Name = "fallback" };
        Nfo.Read(path, NullLogger.Instance)!.ApplyTo(it);
        return it;
    }

    [Fact]
    public void ReadsStrmMovieNfo()
    {
        var it = Apply("""
            <?xml version="1.0" encoding="utf-8" standalone="yes"?>
            <movie>
              <title>Ґанц</title>
              <originaltitle>Gantz</originaltitle>
              <sorttitle>анц</sorttitle>
              <year>2010</year>
              <tmdbid>56832</tmdbid>
              <id provider="Tmdb">56832</id>
            </movie>
            """);
        Assert.Equal("Ґанц", it.Name);
        Assert.Equal("Gantz", it.OriginalTitle);
        Assert.Equal("ґанц", it.SortName); // broken sorttitle ignored
        Assert.Equal(2010, it.ProductionYear);
        Assert.Equal("56832", it.ProviderIds["Tmdb"]);
    }

    [Fact]
    public void ReadsFullJellyfinNfo()
    {
        var it = Apply("""
            <movie>
              <plot>Plot text</plot>
              <title>Тихоокеанський рубіж</title>
              <rating>6.918</rating>
              <year>2013</year>
              <mpaa>UA-12+</mpaa>
              <imdbid>tt1663662</imdbid>
              <premiered>2013-07-11</premiered>
              <dateadded>2026-04-22 18:02:04</dateadded>
              <runtime>131</runtime>
              <genre>Бойовик</genre><genre>Пригоди</genre>
              <studio /><studio>Legendary Pictures</studio>
              <set><name>Тихоокеанський рубіж | Колекція</name></set>
              <actor><name>Charlie Hunnam</name><role>Raleigh</role></actor>
              <fileinfo><streamdetails>
                <video><codec>hevc</codec><width>1920</width><height>800</height><durationinseconds>7856</durationinseconds></video>
                <audio><codec>ac3</codec><language>ukr</language><channels>6</channels></audio>
                <subtitle><language>eng</language></subtitle>
              </streamdetails></fileinfo>
            </movie>
            """);
        Assert.Equal("Plot text", it.Overview);
        Assert.Equal(6.918f, it.CommunityRating!.Value, 3);
        Assert.Equal(131L * 60 * TimeSpan.TicksPerSecond, it.RuntimeTicks);
        Assert.Equal(new[] { "Бойовик", "Пригоди" }, it.Genres);
        Assert.Equal(new[] { "Legendary Pictures" }, it.Studios);
        Assert.Equal("tt1663662", it.ProviderIds["Imdb"]);
        Assert.Equal(new DateTime(2026, 4, 22, 18, 2, 4, DateTimeKind.Utc), it.DateCreated);
        Assert.Equal("Тихоокеанський рубіж | Колекція", it.CollectionName);
        Assert.Equal("hevc", it.MediaInfo!.Video[0].Codec);
        Assert.Equal(6, it.MediaInfo.Audio[0].Channels);
        Assert.Single(it.People!);
    }

    [Fact]
    public void ToleratesBareAmpersands()
    {
        var it = Apply("<movie><title>Tom & Jerry</title></movie>");
        Assert.Equal("Tom & Jerry", it.Name);
    }

    [Fact]
    public void UrlOnlyNfoIsIgnored()
    {
        var path = Path.Combine(_dir, "url.nfo");
        File.WriteAllText(path, "https://www.imdb.com/title/tt0133093/");
        Assert.Null(Nfo.Read(path, NullLogger.Instance));
    }
}
