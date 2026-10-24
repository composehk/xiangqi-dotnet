using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class BilingualErrorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    public BilingualErrorTests() => Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");

    [Fact]
    public async Task MalformedJson_OnVsAi_IsBilingual400()
    {
        using var host = Client();
        HttpClient client = host.Client;
        using var content = new StringContent("{bad", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/games/vs-ai", content);
        string error = await Error(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Malformed", error);
        Assert.Matches(Cjk, error);
    }

    [Theory]
    [InlineData("GET", "/api/rooms/not-a-guid")]
    [InlineData("GET", "/api/rooms/xyz/moves")]
    [InlineData("POST", "/api/rooms/xyz/moves")]
    [InlineData("POST", "/api/rooms/xyz/join")]
    [InlineData("POST", "/api/rooms/xyz/resign")]
    [InlineData("POST", "/api/rooms/xyz/undo")]
    public async Task InvalidRoomId_IsBilingual400(string method, string path)
    {
        using var host = Client();
        HttpClient client = host.Client;
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
            request.Content = JsonContent.Create(new { move = "a0a0", token = "t", color = "red", kind = "human" }, options: Json);
        using HttpResponseMessage response = await client.SendAsync(request);
        string error = await Error(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid room id", error);
        Assert.Matches(Cjk, error);
    }

    [Fact]
    public async Task UnknownRoomId_IsJson404()
    {
        using var host = Client();
        using HttpResponseMessage response = await host.Client.GetAsync($"/api/rooms/{Guid.NewGuid()}");
        string error = await Error(response);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Matches(Cjk, error);
    }

    [Fact]
    public async Task ContinueBadDifficulty_IsBilingual()
    {
        using var host = Client(
            ("Payment:PayToAddress", PaymentVerifierTests.PayTo),
            ("Payment:CreatePerMinute", "0"));
        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/api/continue/sessions",
            new { moves = "炮二平五 马8进7 马二进三", ply = 2, difficulty = "impossible", humanColor = "red" },
            Json);
        string error = await Error(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Difficulty must be", error);
        Assert.Matches(Cjk, error);
    }

    [Fact]
    public async Task Limited429_RetryAfterMatchesBody()
    {
        using var host = Client(("Payment:CreatePerMinute", "1"));
        var body = new { moves = "炮二平五 马8进7 马二进三", ply = 2, difficulty = "easy", humanColor = "red" };
        HttpResponseMessage? last = null;
        for (int i = 0; i < 3; i++)
            last = await host.Client.PostAsJsonAsync("/api/continue/sessions", body, Json);

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.TryGetValues("Retry-After", out var values));
        int retry = int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retry, 1, 60);
        string error = await Error(last);
        Assert.Contains(retry.ToString(System.Globalization.CultureInfo.InvariantCulture), error);
    }

    [Fact]
    public async Task Cors_AllowlistMatch_IsCaseInsensitiveWithoutTrailingSlash()
    {
        using WebApplicationFactory<Program> factory = Factory(("Cors:AllowedOrigins:0", "HTTPS://Example.COM/"));
        using HttpResponseMessage allowed = await GetHealth(factory, "https://example.com");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("https://example.com", Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));

        using HttpResponseMessage denied = await GetHealth(factory, "https://evil.example");
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static TestApp Client(params (string Key, string Value)[] settings)
    {
        var factory = Factory(
            settings.Concat(new[] { ("Arena:CreatePerMinute", "0"), ("Arena:ActionPerMinute", "0") }).ToArray());
        HttpClient client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return new TestApp(factory, client);
    }

    private sealed class TestApp : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        public HttpClient Client { get; }
        public TestApp(WebApplicationFactory<Program> factory, HttpClient client)
        {
            _factory = factory;
            Client = client;
        }
        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
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

    private static async Task<string> Error(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString()!;
    }
}
