using System.Text;
using JellyServer.Api.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace JellyServer.Tests;

public class EnvironmentEndpointTests
{
    private static async Task<int> Validate(string json)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var result = await EnvironmentEndpoints.ValidatePath(ctx, CancellationToken.None);
        return Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;
    }

    [Fact]
    public async Task ValidatePath_ExistingDirectory_Is204() =>
        Assert.Equal(204, await Validate($$"""{"Path":{{System.Text.Json.JsonSerializer.Serialize(Path.GetTempPath())}}}"""));

    [Fact]
    public async Task ValidatePath_MissingPath_Is404() =>
        Assert.Equal(404, await Validate($$"""{"Path":{{System.Text.Json.JsonSerializer.Serialize(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()))}}}"""));

    [Fact]
    public async Task ValidatePath_NonObjectBody_Is400() => Assert.Equal(400, await Validate("[1]"));
}
