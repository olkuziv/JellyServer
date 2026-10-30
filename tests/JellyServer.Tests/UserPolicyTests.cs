using System.Net;
using JellyServer.Api;
using JellyServer.Api.Endpoints;
using JellyServer.Catalog;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.Localization;
using JellyServer.Media.Contracts;
using JellyServer.Transcoding;
using Xunit;

namespace JellyServer.Tests;

/// <summary>The user policy: what the stored JSON means, and the rules the server derives from it.</summary>
public class UserPolicyTests
{
    [Fact]
    public void NewUsersGetJellyfinsDefaults()
    {
        var policy = UserEndpoints.ParsePolicy(new User { Name = "kid" });

        Assert.True(policy.EnableAllFolders);
        Assert.True(policy.EnableMediaPlayback);
        Assert.True(policy.EnableRemoteAccess);
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableCollectionManagement);
    }

    [Fact]
    public void ColumnsWinOverTheStoredJson()
    {
        var user = new User { Name = "a", IsAdmin = true, IsHidden = true, IsDisabled = true, Policy = """{"IsAdministrator":false,"IsDisabled":false,"MaxParentalRating":7}""" };

        var policy = UserEndpoints.ParsePolicy(user);

        Assert.True(policy.IsAdministrator);
        Assert.True(policy.IsHidden);
        Assert.True(policy.IsDisabled);
        Assert.True(policy.EnableCollectionManagement); // administrators can always manage collections
        Assert.Equal(7, policy.MaxParentalRating);
    }

    [Fact]
    public void BrokenPolicyJsonFallsBackToDefaults() =>
        Assert.True(UserEndpoints.ParsePolicy(new User { Policy = "{not json" }).EnableMediaPlayback);

    [Fact]
    public void LibrariesAreRestrictedToTheGivenOnes()
    {
        var mine = Guid.NewGuid();
        var r = new ItemRestrictions(Libraries: [mine]);

        Assert.True(r.AllowsLibrary(mine));
        Assert.False(r.AllowsLibrary(Guid.NewGuid()));
        Assert.True(ItemRestrictions.None.AllowsLibrary(Guid.NewGuid()));
        Assert.False(r.IsNone);
        Assert.NotEqual(r.Key, new ItemRestrictions(Libraries: [Guid.NewGuid()]).Key);
    }

    [Fact]
    public void QueriesCarryEveryRestriction()
    {
        var lib = Guid.NewGuid();
        var q = new ItemQuery
        {
            Restrictions = new ItemRestrictions([lib], ["r", "nc-17"], ["g", "pg", "r", "nc-17"], ["Movie"]),
        };

        var (fromWhere, _, args, _) = q.Build();

        Assert.Contains("i.library_id = ANY($1)", fromWhere);
        Assert.Contains("<> ALL($2)", fromWhere);
        Assert.Contains("<> ALL($3)", fromWhere);
        Assert.Contains("= ANY($4)", fromWhere);
        Assert.Equal([lib], (Guid[])args[0]);
        Assert.Equal(["Movie"], (string[])args[2]);
    }

    [Fact]
    public void AnUnrestrictedQueryAddsNothing()
    {
        var (fromWhere, _, args, _) = new ItemQuery { Restrictions = ItemRestrictions.None }.Build();

        Assert.DoesNotContain("WHERE", fromWhere);
        Assert.Empty(args);
    }

    [Fact]
    public void ParentalScaleKnowsTheServersCountryFirst()
    {
        var scale = new LocalizationService().RatingScale("US");

        Assert.True(scale["PG-13"].Score > scale["PG"].Score);
        Assert.True(scale["R"].Score > scale["PG-13"].Score);
        Assert.True(scale.ContainsKey("pg-13")); // case-insensitive
    }

    // 2026-10-26 is a Monday.
    [Theory]
    [InlineData("Everyday", 10.0, true)]
    [InlineData("Monday", 10.0, true)]
    [InlineData("Tuesday", 10.0, false)]
    [InlineData("Weekday", 10.0, true)]
    [InlineData("Weekend", 10.0, false)]
    [InlineData("Everyday", 20.0, false)]
    public void ScheduleAllowsOnlyItsHours(string day, double hour, bool allowed)
    {
        var policy = new UserPolicy { AccessSchedules = [new AccessSchedule { DayOfWeek = day, StartHour = 8, EndHour = 18 }] };

        Assert.Equal(allowed, PolicyAccess.InSchedule(policy, new DateTime(2026, 10, 26).AddHours(hour)));
    }

    [Fact]
    public void NoScheduleMeansAlwaysAllowed() =>
        Assert.True(PolicyAccess.InSchedule(new UserPolicy(), new DateTime(2026, 10, 26, 3, 0, 0)));

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("::ffff:192.168.1.10", true)]
    [InlineData("fd12::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("2001:db8::1", false)]
    public void LocalNetworkIsRecognised(string ip, bool local) => Assert.Equal(local, PolicyAccess.IsLocal(IPAddress.Parse(ip)));

    private static (Item, MediaInfo) HevcMkv() =>
        (new Item { Name = "x", Container = "mkv", MediaInfo = new MediaInfo { Video = [new VideoStream("hevc", 3840, 2160, 0, null)], Audio = [new AudioStream("ac3", "ukr", 6)] } }, null!);

    private static PlaybackInfoRequest IosRequest() => new()
    {
        DeviceProfile = new DeviceProfileDto
        {
            DirectPlayProfiles = [new() { Container = "mp4", Type = "Video", VideoCodec = "h264", AudioCodec = "aac" }],
            TranscodingProfiles = [new() { Container = "mp4", Type = "Video", Protocol = "hls", VideoCodec = "hevc,h264", AudioCodec = "aac,ac3", MaxAudioChannels = "6" }],
        },
    };

    [Fact]
    public void WithoutRemuxingTheVideoIsReEncoded()
    {
        var (item, _) = HevcMkv();
        HashSet<string> encodable = ["h264"];

        var free = PlaybackDecider.Decide(item, item.MediaInfo, IosRequest(), true, encodable);
        var limited = PlaybackDecider.Decide(item, item.MediaInfo, IosRequest(), true, encodable, limits: new TranscodeLimits(Remux: false));

        Assert.True(free.Plan!.CopyVideo);
        Assert.False(limited.Plan!.CopyVideo);
        Assert.False(limited.Plan.CopyAudio);
    }

    [Fact]
    public void WithoutVideoTranscodingOnlyACopyIsOffered()
    {
        var (item, _) = HevcMkv();
        HashSet<string> encodable = ["h264"];

        var remuxOnly = PlaybackDecider.Decide(item, item.MediaInfo, IosRequest(), true, encodable, limits: new TranscodeLimits(EncodeVideo: false));
        var nothing = PlaybackDecider.Decide(item, item.MediaInfo, IosRequest(), true, encodable, limits: new TranscodeLimits(Remux: false, EncodeVideo: false));

        Assert.True(remuxOnly.Plan!.CopyVideo);
        Assert.True(nothing.DirectPlay);
        Assert.Null(nothing.Plan);
    }

    [Fact]
    public void WithoutAudioTranscodingTheAudioMustBeCopied()
    {
        var item = new Item { Name = "x", Container = "mkv", MediaInfo = new MediaInfo { Video = [new VideoStream("hevc", 1920, 1080, 0, null)], Audio = [new AudioStream("dts", "ukr", 6)] } };
        HashSet<string> encodable = ["h264"];

        var d = PlaybackDecider.Decide(item, item.MediaInfo, IosRequest(), true, encodable, limits: new TranscodeLimits(EncodeAudio: false));

        Assert.True(d.DirectPlay);
        Assert.Null(d.Plan);
    }
}
