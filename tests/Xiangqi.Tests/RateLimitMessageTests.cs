using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class RateLimitMessageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public RateLimitMessageTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Limited429HasBilingualJsonBodyAndRetryAfter()
    {
        using var client = ContinueHarness.Configured(_factory, settings: new[] { ("Payment:CreatePerMinute", "2") });
        var body = new { moves = "炮二平五 马8进7 马二进三", ply = 2, difficulty = "easy", humanColor = "red" };
        HttpResponseMessage? last = null;
        for (int i = 0; i < 4; i++)
            last = await client.PostAsJsonAsync("/api/continue/sessions", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("application/json", last.Content.Headers.ContentType?.MediaType);
        Assert.True(last.Headers.TryGetValues("Retry-After", out var values));
        int retry = int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retry, 1, 3600);
        using var doc = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
        string error = doc.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("请求过于频繁", error);
        Assert.Contains("too many requests", error);
    }
}
