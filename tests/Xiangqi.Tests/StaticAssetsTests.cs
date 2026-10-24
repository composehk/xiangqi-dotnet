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
}
