using System.Text.Json;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.UserData.Contracts;

namespace JellyServer.Api.Endpoints;

/// <summary>Authentication, users, display preferences and per-user item state.</summary>
public static class UserEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/Users/Public", () => SystemEndpoints.EmptyList());
        app.MapPost("/Users/AuthenticateByName", Authenticate);

        var authed = app.MapGroup("").RequireToken();
        authed.MapGet("/Users", AllUsers);
        authed.MapGet("/Users/Me", (HttpContext ctx, ServerIdentity id) => Results.Json(ToDto(ctx.Auth().User, id.ServerId), AppJson.Default.UserDto));
        authed.MapGet("/Users/{userId}", GetUser);
        authed.MapPost("/Users/{userId}/Configuration", SaveConfiguration);
        authed.MapPost("/Users/Configuration", SaveConfiguration);
        authed.MapPost("/Users/{userId}/Password", ChangePassword);
        authed.MapPost("/Users/Password", ChangePassword);
        authed.MapPost("/Sessions/Logout", Logout);
        authed.MapGet("/DisplayPreferences/{id}", GetDisplayPreferences);
        authed.MapPost("/DisplayPreferences/{id}", SaveDisplayPreferences);
        authed.MapGet("/Devices", async (UserRepository users) =>
            Results.Json((await users.DevicesAsync()).Select(d => new Dictionary<string, object>
            {
                ["Id"] = d.DeviceId, ["Name"] = d.DeviceName, ["AppName"] = d.Client, ["AppVersion"] = d.AppVersion, ["LastUserId"] = d.UserId.N(),
            }).Cast<object>().ToList(), AppJson.Default.ListObject)).RequireAdmin();
        app.MapPost("/Users/New", CreateUser).RequireAdmin();
        app.MapDelete("/Users/{userId}", DeleteUser).RequireAdmin();

        foreach (var prefix in new[] { "/Users/{userId}/FavoriteItems/{id}", "/UserFavoriteItems/{id}" })
        {
            authed.MapPost(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i) => SetFavorite(ctx, u, i, true));
            authed.MapDelete(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i) => SetFavorite(ctx, u, i, false));
        }
        foreach (var prefix in new[] { "/Users/{userId}/PlayedItems/{id}", "/UserPlayedItems/{id}" })
        {
            authed.MapPost(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i) => SetPlayed(ctx, u, i, true));
            authed.MapDelete(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i) => SetPlayed(ctx, u, i, false));
        }
        foreach (var path in new[] { "/UserItems/{id}/UserData", "/Users/{userId}/Items/{id}/UserData" })
        {
            authed.MapGet(path, GetUserData);
            authed.MapPost(path, UpdateUserData);
        }
    }

    public static UserDto ToDto(User u, string serverId)
    {
        JsonElement cfg;
        try { cfg = JsonDocument.Parse(string.IsNullOrWhiteSpace(u.Configuration) || u.Configuration == "{}" ? DefaultConfiguration : u.Configuration).RootElement.Clone(); }
        catch (JsonException) { cfg = JsonDocument.Parse(DefaultConfiguration).RootElement.Clone(); }
        return new UserDto
        {
            Name = u.Name, ServerId = serverId, Id = u.Id.N(), HasPassword = true, HasConfiguredPassword = true,
            LastLoginDate = u.LastLoginAt, LastActivityDate = u.LastActiveAt, Configuration = cfg,
            Policy = new UserPolicy { IsAdministrator = u.IsAdmin, IsHidden = u.IsHidden },
        };
    }

    private const string DefaultConfiguration = """
        {"PlayDefaultAudioTrack":true,"SubtitleLanguagePreference":"","DisplayMissingEpisodes":false,"GroupedFolders":[],
         "SubtitleMode":"Default","DisplayCollectionsView":false,"EnableLocalPassword":false,"OrderedViews":[],
         "LatestItemsExcludes":[],"MyMediaExcludes":[],"HidePlayedInLatest":true,"RememberAudioSelections":true,
         "RememberSubtitleSelections":true,"EnableNextEpisodeAutoPlay":true,"AudioLanguagePreference":"","CastReceiverId":"F007D354"}
        """;

    private static async Task<IResult> Authenticate(HttpContext ctx, UserRepository users, ServerIdentity id, SessionRegistry sessions, ILogger<UserRepository> log)
    {
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.AuthenticateByName);
        if (string.IsNullOrWhiteSpace(body?.Username)) return Results.BadRequest("Username required");
        var user = await users.ByNameAsync(body.Username.Trim());
        if (user is null || !Passwords.Verify(user, body.Pw))
        {
            log.LogWarning("Failed login for {User} from {Ip}", body.Username, ctx.Connection.RemoteIpAddress);
            return Results.Unauthorized();
        }
        var header = AuthHeader.Parse(ctx.Request);
        var session = new AuthSession
        {
            UserId = user.Id, DeviceId = header.DeviceId, DeviceName = header.Device, Client = header.Client, AppVersion = header.Version,
        };
        var token = await users.CreateTokenAsync(session);
        var state = sessions.Touch(new AuthInfo(user, session, header with { Token = token }));
        log.LogInformation("User {User} logged in from {Client} on {Device}", user.Name, header.Client, header.Device);
        return Results.Json(new AuthenticationResult
        {
            User = ToDto(user, id.ServerId), AccessToken = token, ServerId = id.ServerId,
            SessionInfo = new SessionInfoDto
            {
                Id = state.Id, UserId = user.Id.N(), UserName = user.Name, Client = header.Client, DeviceName = header.Device,
                DeviceId = header.DeviceId, ApplicationVersion = header.Version, LastActivityDate = DateTime.UtcNow, ServerId = id.ServerId,
            },
        }, AppJson.Default.AuthenticationResult);
    }

    private static async Task<IResult> AllUsers(HttpContext ctx, UserRepository users, ServerIdentity id)
    {
        var me = ctx.Auth().User;
        var list = me.IsAdmin ? await users.AllAsync() : [me];
        return Results.Json(list.Select(u => ToDto(u, id.ServerId)).ToList(), AppJson.Default.ListUserDto);
    }

    private static async Task<IResult> GetUser(HttpContext ctx, UserRepository users, ServerIdentity id)
    {
        var me = ctx.Auth().User;
        var requested = ctx.RouteGuid("userId");
        if (requested is null || requested == me.Id) return Results.Json(ToDto(me, id.ServerId), AppJson.Default.UserDto);
        if (!me.IsAdmin) return Results.Forbid();
        return await users.ByIdAsync(requested.Value) is { } u ? Results.Json(ToDto(u, id.ServerId), AppJson.Default.UserDto) : Results.NotFound();
    }

    private static async Task<IResult> SaveConfiguration(HttpContext ctx, UserRepository users)
    {
        var json = await ctx.Request.ReadRawBodyAsync();
        if (json is null) return Results.BadRequest();
        try { JsonDocument.Parse(json).Dispose(); }
        catch (JsonException) { return Results.BadRequest("invalid json"); }
        await users.SetConfigurationAsync(ctx.Auth().User.Id, json);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePassword(HttpContext ctx, UserRepository users, AuthService auth)
    {
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.PasswordChange);
        if (body is null) return Results.BadRequest();
        var me = ctx.Auth().User;
        var target = ctx.RouteGuid("userId") ?? me.Id;
        if (target != me.Id && !me.IsAdmin) return Results.Forbid();
        if (target == me.Id && !body.ResetPassword)
        {
            var withHash = await users.ByNameAsync(me.Name);
            if (withHash is null || !Passwords.Verify(withHash, body.CurrentPw)) return Results.Forbid();
        }
        await users.SetPasswordAsync(target, body.ResetPassword ? null : body.NewPw);
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(HttpContext ctx, UserRepository users, AuthService auth)
    {
        var token = ctx.Auth().Session.Token;
        await users.DeleteTokenAsync(token);
        auth.Forget(token);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteUser(Guid userId, UserRepository users, CancellationToken ct) =>
        await users.DeleteAsync(userId, ct) switch
        {
            DeleteUserResult.NotFound => Results.NotFound(),
            DeleteUserResult.LastAdministrator => Results.BadRequest("The last administrator cannot be deleted"),
            _ => Results.NoContent(),
        };

    private static async Task<IResult> CreateUser(HttpContext ctx, UserRepository users, ServerIdentity id)
    {
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.CreateUserRequest);
        if (string.IsNullOrWhiteSpace(body?.Name)) return Results.BadRequest("Name required");
        var user = await users.CreateAsync(body.Name.Trim(), body.Password, admin: false);
        return user is null ? Results.Problem() : Results.Json(ToDto(user, id.ServerId), AppJson.Default.UserDto);
    }

    private static async Task<IResult> GetDisplayPreferences(HttpContext ctx, IUserDataStore userData)
    {
        var prefId = ctx.Request.RouteValues["id"]?.ToString() ?? "";
        var client = ctx.Request.Query.Str("client") ?? "emby";
        var stored = await userData.GetDisplayPreferencesAsync(ctx.Auth().User.Id, prefId, client);
        if (stored is not null) return Results.Text(stored, "application/json");
        return Results.Json(new DisplayPreferencesDto { Id = prefId, Client = client }, AppJson.Default.DisplayPreferencesDto);
    }

    private static async Task<IResult> SaveDisplayPreferences(HttpContext ctx, IUserDataStore userData)
    {
        var json = await ctx.Request.ReadRawBodyAsync();
        if (json is null) return Results.BadRequest();
        var prefId = ctx.Request.RouteValues["id"]?.ToString() ?? "";
        await userData.SaveDisplayPreferencesAsync(ctx.Auth().User.Id, prefId, ctx.Request.Query.Str("client") ?? "emby", json);
        return Results.NoContent();
    }

    private static async Task<IResult> UserDataResult(HttpContext ctx, IUserDataStore userData, ItemRepository items, Item item)
    {
        var ud = await userData.GetManyAsync(ctx.Auth().User.Id, [item.Id]);
        var counts = item.IsFolder ? await items.GetPlayedCountsAsync(ctx.Auth().User.Id, [item.Id]) : null;
        return Results.Json(DtoBuilder.UserDataDto(item, ud.GetValueOrDefault(item.Id), counts), AppJson.Default.UserItemDataDto);
    }

    private static async Task<IResult> SetFavorite(HttpContext ctx, IUserDataStore userData, ItemRepository items, bool favorite)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item) return Results.NotFound();
        await userData.SetFavoriteAsync(ctx.Auth().User.Id, id, favorite);
        return await UserDataResult(ctx, userData, items, item);
    }

    private static async Task<IResult> SetPlayed(HttpContext ctx, IUserDataStore userData, ItemRepository items, bool played)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item) return Results.NotFound();
        if (item.IsFolder) await userData.MarkManyPlayedAsync(ctx.Auth().User.Id, await items.EpisodeIdsAsync(id, ctx.RequestAborted), played);
        else await userData.MarkPlayedAsync(ctx.Auth().User.Id, id, played);
        return await UserDataResult(ctx, userData, items, item);
    }

    private static async Task<IResult> GetUserData(HttpContext ctx, IUserDataStore userData, ItemRepository items)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item) return Results.NotFound();
        return await UserDataResult(ctx, userData, items, item);
    }

    private static async Task<IResult> UpdateUserData(HttpContext ctx, IUserDataStore userData, ItemRepository items)
    {
        if (ctx.RouteGuid("id") is not { } id || await items.GetAsync(id) is not { } item) return Results.NotFound();
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.UserDataUpdate);
        if (body is null) return Results.BadRequest();
        await userData.UpdateAsync(ctx.Auth().User.Id, id, body.PlaybackPositionTicks, body.Played, body.IsFavorite, body.PlayCount);
        return await UserDataResult(ctx, userData, items, item);
    }
}
