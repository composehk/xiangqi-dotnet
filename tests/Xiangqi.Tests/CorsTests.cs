using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class CorsTests
{
    private const string Allowed = "https://chess.kastop.com";
    private const string Local = "http://localhost:5173";
    private const string Evil = "https://evil.example";

    public CorsTests() => Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");

    [Fact]
    public async Task Health_ReflectsConfiguredOrigin()
    {
        using HttpResponseMessage response = await GetHealth(Factory(), Allowed);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Allowed, AllowOrigin(response));
    }

    [Fact]
    public async Task Health_AllowsLocalhostAnyPort()
    {
        using HttpResponseMessage response = await GetHealth(Factory(), Local);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Local, AllowOrigin(response));
    }

    [Fact]
    public async Task Health_OmitsHeaderForUnlistedOrigin()
    {
        using HttpResponseMessage response = await GetHealth(Factory(), Evil);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_OmitsHeaderForUnlistedOrigin()
    {
        using HttpResponseMessage response = await Preflight(Factory(), Evil);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_AllowsConfiguredOrigin()
    {
        using HttpResponseMessage response = await Preflight(Factory(), Allowed);
        Assert.Equal(Allowed, AllowOrigin(response));
    }

    [Fact]
    public async Task Health_WithoutOrigin_HasNoCorsHeader()
    {
        using WebApplicationFactory<Program> factory = Factory();
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task AllowAnyOrigin_EchoesWildcard()
    {
        using WebApplicationFactory<Program> factory = Factory(("Cors:AllowAnyOrigin", "true"));
        using HttpResponseMessage response = await GetHealth(factory, Evil);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", AllowOrigin(response));
    }

    [Fact]
    public async Task AllowedOriginsOverride_AllowsThatOrigin()
    {
        const string other = "https://other.example";
        using WebApplicationFactory<Program> factory = Factory(("Cors:AllowedOrigins:0", other));
        using HttpResponseMessage response = await GetHealth(factory, other);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(other, AllowOrigin(response));
    }

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach ((string key, string value) in settings)
                builder.UseSetting(key, value);
        });
    }

    private static Task<HttpResponseMessage> GetHealth(WebApplicationFactory<Program> factory, string origin)
    {
        HttpClient client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Preflight(WebApplicationFactory<Program> factory, string origin)
    {
        HttpClient client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/rooms");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        return client.SendAsync(request);
    }

    private static string AllowOrigin(HttpResponseMessage response) =>
        Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin"));
}
