using System.Text.Json;
using JellyServer.Serialization;
using JellyServer.Services;
using JellyServer.Catalog;
using JellyServer.Identity;
using JellyServer.UserData;
using JellyServer.Kernel;
using JellyServer.Catalog.Contracts;
using JellyServer.Collections.Contracts;
using JellyServer.Identity.Contracts;
using JellyServer.People.Contracts;
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
        authed.MapPost("/Users/{userId}", UpdateUser);
        authed.MapPost("/Users", UpdateUser);
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
        app.MapPost("/Users/{userId}/Policy", SavePolicy).RequireAdmin();
        app.MapDelete("/Users/{userId}", DeleteUser).RequireAdmin();

        foreach (var prefix in new[] { "/Users/{userId}/FavoriteItems/{id}", "/UserFavoriteItems/{id}" })
        {
            authed.MapPost(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i, ICollectionManager c, IPeopleStore p) => SetFavorite(ctx, u, i, c, p, true));
            authed.MapDelete(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i, ICollectionManager c, IPeopleStore p) => SetFavorite(ctx, u, i, c, p, false));
        }
        foreach (var prefix in new[] { "/Users/{userId}/PlayedItems/{id}", "/UserPlayedItems/{id}" })
        {
            authed.MapPost(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i, ICollectionManager c) => SetPlayed(ctx, u, i, c, true));
            authed.MapDelete(prefix, (HttpContext ctx, IUserDataStore u, ItemRepository i, ICollectionManager c) => SetPlayed(ctx, u, i, c, false));
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
            Policy = ParsePolicy(u),
        };
    }

    /// <summary>The stored policy with the indexed flags (columns) applied on top; defaults when nothing was saved.</summary>
    public static UserPolicy ParsePolicy(User u)
    {
        UserPolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(u.Policy) && u.Policy != "{}")
        {
            try { policy = JsonSerializer.Deserialize(u.Policy, AppJson.Default.UserPolicy); }
            catch (JsonException) { }
        }
        policy ??= new UserPolicy();
        policy.IsAdministrator = u.IsAdmin;
        policy.IsHidden = u.IsHidden;
        policy.IsDisabled = u.IsDisabled;
        // Administrators can always manage collections; jellyfin-web only offers "Add to collection" with this right.
        policy.EnableCollectionManagement |= u.IsAdmin;
        return policy;
    }

    private static async Task<IResult> SavePolicy(HttpContext ctx, UserRepository users, UserItemRestrictions restrictions, AuthService auth)
    {
        var policy = await ctx.Request.ReadBodyAsync(AppJson.Default.UserPolicy);
        if (policy is null) return Results.BadRequest();
        var target = ctx.RouteGuid("userId");
        if (target is null || await users.ByIdAsync(target.Value) is not { } user) return Results.NotFound();
        // The server owns the failed-login counter: enabling the user again clears it, otherwise the client's value is ignored.
        policy.InvalidLoginAttemptCount = policy.IsDisabled ? ParsePolicy(user).InvalidLoginAttemptCount : 0;
        var json = JsonSerializer.Serialize(policy, AppJson.Default.UserPolicy);
        if (!await users.SetPolicyAsync(target.Value, json, policy.IsAdministrator, policy.IsHidden, policy.IsDisabled))
            return Results.BadRequest("The last enabled administrator cannot be demoted or disabled");
        restrictions.Evict(target.Value);
        auth.ForgetUser(target.Value);
        return Results.NoContent();
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
        if (user is null || user.IsDisabled || !Passwords.Verify(user, body.Pw))
        {
            log.LogWarning("Failed login for {User} from {Ip}", body.Username, ctx.Connection.RemoteIpAddress);
            if (user is { IsDisabled: false })
            {
                // Jellyfin's lockout: -1 means 3 failures (5 for administrators), 0 never locks.
                var failed = ParsePolicy(user);
                var limit = failed.LoginAttemptsBeforeLockout < 0 ? (user.IsAdmin ? 5 : 3) : failed.LoginAttemptsBeforeLockout;
                if ((await users.RegisterFailedLoginAsync(user.Id, limit)).LockedOut)
                    log.LogWarning("User {User} was locked out after too many failed logins", user.Name);
            }
            return Results.Unauthorized();
        }
        var header = AuthHeader.Parse(ctx.Request);
        var policy = ParsePolicy(user);
        if (AuthService.Rejection(ctx, policy, header.DeviceId) is { } rejected) return Results.Text(rejected, statusCode: StatusCodes.Status401Unauthorized);
        if (policy.MaxActiveSessions > 0 && sessions.Active(TimeSpan.FromMinutes(10))
                .Where(s => s.UserId == user.Id && !string.Equals(s.DeviceId, header.DeviceId, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.DeviceId).Distinct().Count() >= policy.MaxActiveSessions)
            return Results.Text("The user has reached the maximum number of active sessions.", statusCode: StatusCodes.Status401Unauthorized);
        if (policy.InvalidLoginAttemptCount > 0) await users.ResetFailedLoginsAsync(user.Id);
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

    /// <summary>Jellyfin's UpdateUser: the body is a UserDto and only the name is applied (configuration and policy have their own endpoints).</summary>
    private static async Task<IResult> UpdateUser(HttpContext ctx, UserRepository users)
    {
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.UserDto);
        if (body is null) return Results.BadRequest();
        var me = ctx.Auth().User;
        var target = ctx.RouteGuid("userId") ?? (Guid.TryParse(body.Id, out var bodyId) ? bodyId : me.Id);
        if (target != me.Id && !me.IsAdmin) return Results.Forbid();
        var user = target == me.Id ? me : await users.ByIdAsync(target);
        if (user is null) return Results.NotFound();
        var name = body.Name?.Trim();
        if (!string.IsNullOrEmpty(name) && !string.Equals(name, user.Name, StringComparison.Ordinal)
            && !await users.RenameAsync(target, name))
            return Results.Conflict("A user with that name already exists");
        return Results.NoContent();
    }

    private static async Task<IResult> SaveConfiguration(HttpContext ctx, UserRepository users)
    {
        if (!ctx.Policy().EnableUserPreferenceAccess) return Results.StatusCode(StatusCodes.Status403Forbidden);
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
        return user is null ? Results.BadRequest("A user with this name already exists.") : Results.Json(ToDto(user, id.ServerId), AppJson.Default.UserDto);
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
        if (!ctx.Policy().EnableUserPreferenceAccess) return Results.StatusCode(StatusCodes.Status403Forbidden);
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

    /// <summary>User data of something that is no catalog item (a playlist, box set or person): kept by id like any other.</summary>
    private static async Task<IResult> PlainUserDataResult(HttpContext ctx, IUserDataStore userData, Guid id, bool? played = null)
    {
        var ud = (await userData.GetManyAsync(ctx.Auth().User.Id, [id])).GetValueOrDefault(id);
        var dto = new UserItemDataDto
        {
            Key = id.N(), ItemId = id.N(), IsFavorite = ud?.IsFavorite ?? false, PlayCount = ud?.PlayCount ?? 0, LastPlayedDate = ud?.LastPlayedAt,
            Played = played ?? ud?.Played ?? false,
        };
        return Results.Json(dto, AppJson.Default.UserItemDataDto);
    }

    private static async Task<bool> IsListOrPersonAsync(HttpContext ctx, Guid id, ICollectionManager collections, IPeopleStore? people) =>
        await collections.GetAsync(id, ctx.RequestAborted) is { } c && c.IsVisibleTo(ctx.Auth().User.Id)
        || people is not null && (await people.GetPersonsAsync([id], ctx.RequestAborted)).Count > 0;

    private static async Task<IResult> SetFavorite(HttpContext ctx, IUserDataStore userData, ItemRepository items, ICollectionManager collections,
        IPeopleStore people, bool favorite)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        if (await items.GetAsync(id) is { } item)
        {
            if (!await ctx.CanAccessAsync(item)) return Results.NotFound();
            await userData.SetFavoriteAsync(ctx.Auth().User.Id, id, favorite);
            return await UserDataResult(ctx, userData, items, item);
        }
        if (!await IsListOrPersonAsync(ctx, id, collections, people)) return Results.NotFound();
        await userData.SetFavoriteAsync(ctx.Auth().User.Id, id, favorite);
        return await PlainUserDataResult(ctx, userData, id);
    }

    /// <summary>Marking a folder, playlist or box set marks what plays in it (Jellyfin's Folder/BoxSet.MarkPlayed).</summary>
    private static async Task<IResult> SetPlayed(HttpContext ctx, IUserDataStore userData, ItemRepository items, ICollectionManager collections, bool played)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        if (await items.GetAsync(id) is { } item)
        {
            if (!await ctx.CanAccessAsync(item)) return Results.NotFound();
            if (item.IsFolder) await userData.MarkManyPlayedAsync(ctx.Auth().User.Id, await items.EpisodeIdsAsync(id, ctx.RequestAborted), played);
            else await userData.MarkPlayedAsync(ctx.Auth().User.Id, id, played);
            return await UserDataResult(ctx, userData, items, item);
        }
        if (await collections.GetAsync(id, ctx.RequestAborted) is not { } list || !list.IsVisibleTo(ctx.Auth().User.Id)) return Results.NotFound();
        var leaves = await collections.ExpandAsync(await collections.EntriesAsync(id, ctx.RequestAborted), ctx.RequestAborted);
        await userData.MarkManyPlayedAsync(ctx.Auth().User.Id, leaves.Distinct().ToList(), played);
        return await PlainUserDataResult(ctx, userData, id, played);
    }

    private static async Task<IResult> GetUserData(HttpContext ctx, IUserDataStore userData, ItemRepository items, ICollectionManager collections, IPeopleStore people)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        if (await items.GetAsync(id) is { } item) return await ctx.CanAccessAsync(item) ? await UserDataResult(ctx, userData, items, item) : Results.NotFound();
        return await IsListOrPersonAsync(ctx, id, collections, people) ? await PlainUserDataResult(ctx, userData, id) : Results.NotFound();
    }

    private static async Task<IResult> UpdateUserData(HttpContext ctx, IUserDataStore userData, ItemRepository items, ICollectionManager collections, IPeopleStore people)
    {
        if (ctx.RouteGuid("id") is not { } id) return Results.NotFound();
        var item = await items.GetAsync(id);
        if (item is not null && !await ctx.CanAccessAsync(item)) return Results.NotFound();
        if (item is null && !await IsListOrPersonAsync(ctx, id, collections, people)) return Results.NotFound();
        var body = await ctx.Request.ReadBodyAsync(AppJson.Default.UserDataUpdate);
        if (body is null) return Results.BadRequest();
        await userData.UpdateAsync(ctx.Auth().User.Id, id, body.PlaybackPositionTicks, body.Played, body.IsFavorite, body.PlayCount);
        return item is null ? await PlainUserDataResult(ctx, userData, id) : await UserDataResult(ctx, userData, items, item);
    }
}
