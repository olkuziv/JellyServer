using System.Security.Cryptography;
using Dapper;
using Npgsql;
using JellyServer.Kernel.Data;
using JellyServer.Kernel.Events;
using JellyServer.Kernel.Caching;
using JellyServer.Kernel;
using JellyServer.Identity.Contracts;

namespace JellyServer.Identity;

/// <summary>Users and access tokens (the Identity module's data).</summary>


public enum DeleteUserResult { Deleted, NotFound, LastAdministrator }

public sealed class UserRepository([FromKeyedServices(IdentityModule.ModuleName)] Database db, RedisCache cache, IEventBus events)
{
    private const string UserColumns =
        "id, name, password_hash AS PasswordHash, is_admin AS IsAdmin, is_hidden AS IsHidden, configuration::text AS Configuration, policy::text AS Policy, is_disabled AS IsDisabled, last_login_at AS LastLoginAt, last_active_at AS LastActiveAt";

    public async Task<List<User>> AllAsync()
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        return (await conn.QueryAsync<User>($"SELECT {UserColumns} FROM users ORDER BY name")).ToList();
    }

    public async Task<User?> ByIdAsync(Guid id)
    {
        var key = "user:" + id.N();
        if (await cache.GetAsync(key, IdentityJson.Default.User) is { } hit) return hit;
        await using var conn = await db.DataSource.OpenConnectionAsync();
        var user = await conn.QueryFirstOrDefaultAsync<User>($"SELECT {UserColumns} FROM users WHERE id = @id", new { id });
        if (user is not null) await cache.SetAsync(key, user, IdentityJson.Default.User, TimeSpan.FromMinutes(10));
        return user;
    }

    /// <summary>Uncached on purpose: authentication needs the password hash, which the cache never stores.</summary>
    public async Task<User?> ByNameAsync(string name)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<User>($"SELECT {UserColumns} FROM users WHERE lower(name) = lower(@name)", new { name });
    }

    public async Task<bool> AnyAsync()
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM users)");
    }

    /// <summary>The oldest user: during the startup wizard this is the account being set up.</summary>
    public async Task<User?> FirstAsync()
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        return await conn.QueryFirstOrDefaultAsync<User>($"SELECT {UserColumns} FROM users ORDER BY created_at, id LIMIT 1");
    }

    /// <summary>Creates the initial administrator (empty password) when no user exists; safe against concurrent callers.</summary>
    public async Task<User> EnsureFirstUserAsync(string defaultName)
    {
        await using (var conn = await db.DataSource.OpenConnectionAsync())
            await conn.ExecuteAsync("""
                INSERT INTO users (id, name, password_hash, is_admin) SELECT @id, @name, '', true
                 WHERE NOT EXISTS (SELECT 1 FROM users)
                """, new { id = Guid.NewGuid(), name = defaultName });
        return (await FirstAsync())!;
    }

    /// <summary>False when the name is taken by another user.</summary>
    public async Task<bool> RenameAsync(Guid id, string name)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        try
        {
            await conn.ExecuteAsync("UPDATE users SET name = @name WHERE id = @id", new { id, name });
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
        await cache.DeleteAsync("user:" + id.N());
        return true;
    }
    /// <summary>Creates the user; null when the name is already taken.</summary>
    public async Task<User?> CreateAsync(string name, string? password, bool admin)
    {
        var id = Guid.NewGuid();
        await using var conn = await db.DataSource.OpenConnectionAsync();
        try
        {
            await conn.ExecuteAsync("INSERT INTO users (id, name, password_hash, is_admin) VALUES (@id, @name, @hash, @admin)",
                new { id, name, hash = string.IsNullOrEmpty(password) ? "" : Passwords.Hash(password), admin });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }
        return await ByIdAsync(id);
    }

    /// <summary>Removes the user and their tokens (cascade); the last administrator can never be deleted. Other modules react to <see cref="UserDeleted"/>.</summary>
    public async Task<DeleteUserResult> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // Serialise concurrent deletes so two admins cannot remove each other.
        await conn.ExecuteAsync("LOCK TABLE users IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
        var isAdmin = await conn.QueryFirstOrDefaultAsync<bool?>("SELECT is_admin FROM users WHERE id = @id", new { id }, tx);
        if (isAdmin is null) return DeleteUserResult.NotFound;
        if (isAdmin.Value && await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM users WHERE is_admin AND id <> @id", new { id }, tx) == 0)
            return DeleteUserResult.LastAdministrator;
        var tokens = (await conn.QueryAsync<string>("DELETE FROM access_tokens WHERE user_id = @id RETURNING token", new { id }, tx)).ToList();
        await conn.ExecuteAsync("DELETE FROM users WHERE id = @id", new { id }, tx);
        await tx.CommitAsync(ct);
        await cache.DeleteAsync(tokens.Select(t => "tok:" + t).Append("user:" + id.N()));
        await events.PublishAsync(new UserDeleted(id), ct);
        return DeleteUserResult.Deleted;
    }

    public async Task SetPasswordAsync(Guid id, string? password)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE users SET password_hash = @hash WHERE id = @id",
            new { id, hash = string.IsNullOrEmpty(password) ? "" : Passwords.Hash(password) });
        await cache.DeleteAsync("user:" + id.N());
    }

    /// <summary>Stores the policy and its indexed flags; false when it would leave the server without an enabled administrator.</summary>
    public async Task<bool> SetPolicyAsync(Guid id, string json, bool isAdmin, bool isHidden, bool isDisabled)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync("LOCK TABLE users IN SHARE ROW EXCLUSIVE MODE", transaction: tx);
        if ((!isAdmin || isDisabled) && await conn.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM users WHERE is_admin AND NOT is_disabled AND id <> @id", new { id }, tx) == 0)
            return false;
        var tokens = isDisabled
            ? (await conn.QueryAsync<string>("DELETE FROM access_tokens WHERE user_id = @id RETURNING token", new { id }, tx)).ToList()
            : [];
        await conn.ExecuteAsync("UPDATE users SET policy = @json::jsonb, is_admin = @isAdmin, is_hidden = @isHidden, is_disabled = @isDisabled WHERE id = @id",
            new { id, json, isAdmin, isHidden, isDisabled }, tx);
        await tx.CommitAsync();
        await cache.DeleteAsync(tokens.Select(t => "tok:" + t).Append("user:" + id.N()));
        return true;
    }

    /// <summary>
    /// Counts a failed sign-in in the policy; at <paramref name="lockoutAfter"/> failures (0: never) the user is disabled, except
    /// the last enabled administrator, who could not be re-enabled. Returns the count and whether the user was locked out.
    /// </summary>
    public async Task<(int Count, bool LockedOut)> RegisterFailedLoginAsync(Guid id, int lockoutAfter)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        var count = await conn.ExecuteScalarAsync<int>("""
            UPDATE users SET policy = jsonb_set(policy, '{InvalidLoginAttemptCount}', to_jsonb(coalesce((policy->>'InvalidLoginAttemptCount')::int, 0) + 1))
             WHERE id = @id RETURNING coalesce((policy->>'InvalidLoginAttemptCount')::int, 0)
            """, new { id });
        var locked = lockoutAfter > 0 && count >= lockoutAfter && await conn.ExecuteAsync("""
            UPDATE users SET is_disabled = true, policy = jsonb_set(policy, '{IsDisabled}', 'true')
             WHERE id = @id AND NOT is_disabled
               AND (NOT is_admin OR EXISTS (SELECT 1 FROM users o WHERE o.is_admin AND NOT o.is_disabled AND o.id <> @id))
            """, new { id }) > 0;
        await cache.DeleteAsync("user:" + id.N());
        return (count, locked);
    }

    public async Task ResetFailedLoginsAsync(Guid id)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        if (await conn.ExecuteAsync("""
                UPDATE users SET policy = jsonb_set(policy, '{InvalidLoginAttemptCount}', '0')
                 WHERE id = @id AND coalesce((policy->>'InvalidLoginAttemptCount')::int, 0) > 0
                """, new { id }) > 0)
            await cache.DeleteAsync("user:" + id.N());
    }

    public async Task SetConfigurationAsync(Guid id, string json)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("UPDATE users SET configuration = @json::jsonb WHERE id = @id", new { id, json });
        await cache.DeleteAsync("user:" + id.N());
    }

    // ---- access tokens ----

    /// <summary>Issues a token, replacing older tokens of the same device.</summary>
    public async Task<string> CreateTokenAsync(AuthSession session)
    {
        session.Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        if (!string.IsNullOrEmpty(session.DeviceId))
        {
            var old = await conn.QueryAsync<string>("DELETE FROM access_tokens WHERE user_id = @UserId AND device_id = @DeviceId RETURNING token", session, tx);
            await cache.DeleteAsync(old.Select(t => "tok:" + t));
        }
        await conn.ExecuteAsync("""
            INSERT INTO access_tokens (token, user_id, device_id, device_name, client, app_version)
            VALUES (@Token, @UserId, @DeviceId, @DeviceName, @Client, @AppVersion)
            """, session, tx);
        await conn.ExecuteAsync("UPDATE users SET last_login_at = now(), last_active_at = now() WHERE id = @UserId", session, tx);
        await tx.CommitAsync();
        await cache.DeleteAsync("user:" + session.UserId.N());
        return session.Token;
    }

    /// <summary>Resolves a token through Redis, falling back to PostgreSQL.</summary>
    public async Task<AuthSession?> SessionByTokenAsync(string token)
    {
        var key = "tok:" + token;
        if (await cache.GetAsync(key, IdentityJson.Default.AuthSession) is { } hit) return hit;
        await using var conn = await db.DataSource.OpenConnectionAsync();
        var session = await conn.QueryFirstOrDefaultAsync<AuthSession>("""
            UPDATE access_tokens SET last_used_at = now() WHERE token = @token
            RETURNING token, user_id AS UserId, device_id AS DeviceId, device_name AS DeviceName, client, app_version AS AppVersion
            """, new { token });
        if (session is not null) await cache.SetAsync(key, session, IdentityJson.Default.AuthSession, TimeSpan.FromHours(1));
        return session;
    }

    public async Task DeleteTokenAsync(string token)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        await conn.ExecuteAsync("DELETE FROM access_tokens WHERE token = @token", new { token });
        await cache.DeleteAsync("tok:" + token);
    }

    public async Task<List<AuthSession>> DevicesAsync()
    {
        await using var conn = await db.DataSource.OpenConnectionAsync();
        return (await conn.QueryAsync<AuthSession>("""
            SELECT token, user_id AS UserId, device_id AS DeviceId, device_name AS DeviceName, client, app_version AS AppVersion
              FROM access_tokens ORDER BY last_used_at DESC
            """)).ToList();
    }
}

/// <summary>PBKDF2-SHA256 password hashing (no external dependency).</summary>
public static class Passwords
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(User user, string? password)
    {
        if (string.IsNullOrEmpty(user.PasswordHash)) return string.IsNullOrEmpty(password);
        var parts = user.PasswordHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
