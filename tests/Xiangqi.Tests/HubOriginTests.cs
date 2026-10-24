using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class HubOriginTests
{
    public HubOriginTests() => Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");

    [Fact]
    public async Task Negotiate_NoOrigin_IsOk()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsync("/hub/game/negotiate?negotiateVersion=1", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Negotiate_EvilOrigin_IsBilingual403()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hub/game/negotiate?negotiateVersion=1");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string error = doc.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("来源不被允许", error);
        Assert.Contains("Origin not allowed", error);
    }

    [Fact]
    public async Task Negotiate_SameOriginAndLocalDev_AreOk()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();

        using var same = new HttpRequestMessage(HttpMethod.Post, "/hub/game/negotiate?negotiateVersion=1");
        same.Headers.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using HttpResponseMessage sameResponse = await client.SendAsync(same);
        Assert.Equal(HttpStatusCode.OK, sameResponse.StatusCode);

        using var local = new HttpRequestMessage(HttpMethod.Post, "/hub/game/negotiate?negotiateVersion=1");
        local.Headers.TryAddWithoutValidation("Origin", "http://localhost:3000");
        using HttpResponseMessage localResponse = await client.SendAsync(local);
        Assert.Equal(HttpStatusCode.OK, localResponse.StatusCode);
    }

    [Fact]
    public async Task Csp_ConnectSrcFollowsHostHeader()
    {
        using WebApplicationFactory<Program> factory = new();
        using HttpClient client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.TryAddWithoutValidation("Host", "other.example");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? values));
        string csp = string.Join(" ", values!);
        Assert.Contains("ws://other.example", csp);
        Assert.DoesNotContain("chess.kastop.com", csp);
    }
}
