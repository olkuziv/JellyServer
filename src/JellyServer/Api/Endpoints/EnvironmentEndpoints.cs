using System.Text.Json.Nodes;

namespace JellyServer.Api.Endpoints;

/// <summary>Environment/*: the file-system browser the dashboard uses to pick media folders.</summary>
public static class EnvironmentEndpoints
{
    private const char UncSeparator = '\\';
    private const string UncStartPrefix = @"\\";

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/Environment").RequireSetupOrAdmin();
        g.MapGet("/DirectoryContents", DirectoryContents);
        g.MapPost("/ValidatePath", ValidatePath);
        g.MapGet("/Drives", Drives);
        g.MapGet("/ParentPath", ParentPath);
        g.MapGet("/DefaultDirectoryBrowser", () => SetupAccess.Json(new JsonObject { ["Path"] = null }));
    }

    private static JsonObject Entry(string name, string fullName, bool directory) =>
        new() { ["Name"] = name, ["Path"] = fullName, ["Type"] = directory ? "Directory" : "File" };

    private static IResult DirectoryContents(HttpContext ctx)
    {
        var q = ctx.Request.Query;
        var path = q.Str("path");
        if (string.IsNullOrEmpty(path)) return Results.BadRequest("path is required");
        var files = q.Bool("includeFiles") ?? false;
        var dirs = q.Bool("includeDirectories") ?? false;

        // A bare UNC server name (\\host) has no listing.
        if (path.StartsWith(UncStartPrefix, StringComparison.OrdinalIgnoreCase) && path.LastIndexOf(UncSeparator) == 1)
            return SetupAccess.Json(new JsonArray());

        var result = new JsonArray();
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists) return Results.NotFound();
            var entries = new List<(string Name, string Full, bool Dir)>();
            foreach (var e in info.EnumerateFileSystemInfos())
            {
                var isDir = e is DirectoryInfo;
                if (isDir ? dirs : files) entries.Add((e.Name, e.FullName, isDir));
            }
            foreach (var e in entries.OrderBy(e => e.Full, StringComparer.OrdinalIgnoreCase)) result.Add(Entry(e.Name, e.Full, e.Dir));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        catch (IOException)
        {
            return Results.NotFound();
        }
        return SetupAccess.Json(result);
    }

    public static async Task<IResult> ValidatePath(HttpContext ctx, CancellationToken ct)
    {
        if (await SetupAccess.ReadObjectAsync(ctx.Request, ct) is not { } body) return Results.BadRequest("Body must be a JSON object");
        var path = body.String("Path");
        if (string.IsNullOrEmpty(path)) return Results.NotFound();

        if (body.Bool("IsFile") is { } isFile)
            return (isFile ? File.Exists(path) : Directory.Exists(path)) ? Results.NoContent() : Results.NotFound();
        if (!File.Exists(path) && !Directory.Exists(path)) return Results.NotFound();

        if (body.Bool("ValidateWritable") ?? body.Bool("ValidateWriteable") ?? false)
        {
            var probe = Path.Combine(path, Guid.NewGuid().ToString());
            try
            {
                File.WriteAllText(probe, string.Empty);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            finally
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
        }
        return Results.NoContent();
    }

    private static IResult Drives()
    {
        var result = new JsonArray();
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Network or DriveType.Removable && d.IsReady))
            result.Add(Entry(d.Name, d.RootDirectory.FullName, true));
        return SetupAccess.Json(result);
    }

    private static IResult ParentPath(HttpContext ctx)
    {
        var path = ctx.Request.Query.Str("path");
        if (string.IsNullOrEmpty(path)) return Results.BadRequest("path is required");
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            var index = path.LastIndexOf(UncSeparator);
            if (index != -1 && path[0] == UncSeparator)
            {
                parent = path[..index];
                if (string.IsNullOrWhiteSpace(parent.TrimStart(UncSeparator))) parent = null;
            }
        }
        return Results.Text(parent is null ? "null" : JsonValue.Create(parent).ToJsonString(), "application/json");
    }
}
