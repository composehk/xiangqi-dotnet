using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class HostHardeningTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostHardeningTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _factory = factory;
    }

    [Fact]
    public async Task Response_VaryContainsHost()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/api/health");
        Assert.True(response.Headers.TryGetValues("Vary", out IEnumerable<string>? values));
        string vary = string.Join(", ", values!);
        Assert.Contains("Host", vary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Csp_ConnectSrcFollowsCustomHost()
    {
        using HttpClient client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "other.example";
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string csp = Csp(response);
        Assert.Contains("ws://other.example", csp);
    }

    [Fact]
    public async Task OversizedHost_OmitsWsFromConnectSrc()
    {
        using HttpClient client = _factory.CreateClient();
        string host = new string('a', 300) + ".example";
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        try
        {
            request.Headers.Host = host;
        }
        catch
        {
            Assert.True(request.Headers.TryAddWithoutValidation("Host", host));
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string csp = Csp(response);
        Assert.DoesNotContain("ws://", csp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wss://", csp, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connect-src 'self'", csp);
    }

    [Fact]
    public async Task UnknownRoom_Quiet_Returns200Gone()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/api/rooms/{Guid.NewGuid()}?quiet=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("gone").GetBoolean());
        Assert.Equal("房间不存在 / Room not found.", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnknownRoom_WithoutQuiet_Is404()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync($"/api/rooms/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("房间不存在 / Room not found.", doc.RootElement.GetProperty("error").GetString());
        Assert.False(doc.RootElement.TryGetProperty("gone", out _));
    }

    [Fact]
    public async Task ExistingRoom_Quiet_ReturnsRoomJson()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage created = await client.PostAsync(
            "/api/rooms",
            new StringContent("""{"name":"quiet","redKind":"human","blackKind":"open"}""", System.Text.Encoding.UTF8, "application/json"));
        created.EnsureSuccessStatusCode();
        using JsonDocument createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid id = createdDoc.RootElement.GetProperty("id").GetGuid();

        using HttpResponseMessage response = await client.GetAsync($"/api/rooms/{id}?quiet=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetGuid());
        Assert.False(doc.RootElement.TryGetProperty("gone", out _));
    }

    private static string Csp(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? values));
        return string.Join(" ", values!);
    }
}
