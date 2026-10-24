using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class StaticAssetsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StaticAssetsTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _factory = factory;
    }

    [Fact]
    public async Task Favicon_And_Robots_AreServed()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage icon = await client.GetAsync("/favicon.svg");
        Assert.Equal(System.Net.HttpStatusCode.OK, icon.StatusCode);
        Assert.Equal("image/svg+xml", icon.Content.Headers.ContentType?.MediaType);

        using HttpResponseMessage robots = await client.GetAsync("/robots.txt");
        Assert.Equal(System.Net.HttpStatusCode.OK, robots.StatusCode);
        string text = await robots.Content.ReadAsStringAsync();
        Assert.Contains("Disallow: /api/", text);
    }

    [Fact]
    public async Task IndexPage_LinksFaviconAndGameActions()
    {
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");
        Assert.Contains("/favicon.svg", html);
        Assert.Contains("id=\"resignBtn\"", html);
        Assert.Contains("id=\"undoBtn\"", html);
    }

    [Fact]
    public async Task FaviconIco_And_SelfHostedSignalR_AreServed_AndPageHasNoExternalAssets()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage ico = await client.GetAsync("/favicon.ico");
        Assert.Equal(System.Net.HttpStatusCode.OK, ico.StatusCode);
        byte[] bytes = await ico.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 100);
        Assert.Equal(new byte[] { 0, 0, 1, 0 }, bytes[..4]);

        using HttpResponseMessage lib = await client.GetAsync("/lib/signalr.min.js");
        Assert.Equal(System.Net.HttpStatusCode.OK, lib.StatusCode);
        string js = await lib.Content.ReadAsStringAsync();
        Assert.Contains("signalR", js);
        Assert.DoesNotContain("sourceMappingURL", js);

        string html = await client.GetStringAsync("/");
        Assert.Contains("/lib/signalr.min.js", html);
        Assert.DoesNotContain("cdnjs", html);
        Assert.DoesNotContain("googleapis", html);
        Assert.DoesNotContain("gstatic", html);
    }
}
