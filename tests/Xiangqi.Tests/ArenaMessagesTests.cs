using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class ArenaMessagesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private readonly HttpClient _client;

    public ArenaMessagesTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arena:CreatePerMinute", "0");
            builder.UseSetting("Arena:ActionPerMinute", "0");
        }).CreateClient();
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    [Fact]
    public async Task MissingRoom_IsBilingual404()
    {
        await AssertBilingual(
            () => _client.GetAsync($"/api/rooms/{Guid.NewGuid()}"),
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IllegalMove_WrongToken_SeatTaken_AreBilingual()
    {
        var created = await Send(() => _client.PostAsJsonAsync("/api/rooms", new
        {
            name = "双语",
            redKind = "human",
            blackKind = "open"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid id = created.Body.GetProperty("id").GetGuid();
        string redToken = created.Body.GetProperty("tokens").GetProperty("red").GetString()!;

        await AssertBilingual(
            () => _client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = "a0a0", token = redToken }, Json),
            HttpStatusCode.BadRequest);

        await AssertBilingual(
            () => _client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = "nope", token = redToken }, Json),
            HttpStatusCode.BadRequest);

        await AssertBilingual(
            () => _client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = "a0a0", token = "not-the-owner" }, Json),
            HttpStatusCode.Forbidden);

        var joined = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "black", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.OK, joined.Status);
        await AssertBilingual(
            () => _client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "black", kind = "human" }, Json),
            HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task BadDifficulty_BadColor_BadSeatKind_AreBilingual()
    {
        await AssertBilingual(
            () => _client.PostAsJsonAsync("/api/games/vs-ai", new { humanColor = "red", difficulty = "impossible" }, Json),
            HttpStatusCode.BadRequest);

        await AssertBilingual(
            () => _client.PostAsJsonAsync("/api/games/vs-ai", new { humanColor = "green", difficulty = "easy" }, Json),
            HttpStatusCode.BadRequest);

        await AssertBilingual(
            () => _client.PostAsJsonAsync("/api/rooms", new { name = "坏座", redKind = "spectator", blackKind = "open" }, Json),
            HttpStatusCode.BadRequest);

        await AssertBilingual(
            () => _client.PostAsJsonAsync("/api/rooms", new { name = "空座", redKind = "", blackKind = "open" }, Json),
            HttpStatusCode.BadRequest);
    }

    private async Task AssertBilingual(Func<Task<HttpResponseMessage>> call, HttpStatusCode expected)
    {
        var (status, body) = await Send(call);
        Assert.Equal(expected, status);
        string error = body.GetProperty("error").GetString()!;
        Assert.Contains(" / ", error);
        Assert.Matches(Cjk, error);
        Assert.Matches(@"[A-Za-z]", error);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(Func<Task<HttpResponseMessage>> call)
    {
        using HttpResponseMessage response = await call();
        string raw = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(raw);
        return (response.StatusCode, doc.RootElement.Clone());
    }
}
