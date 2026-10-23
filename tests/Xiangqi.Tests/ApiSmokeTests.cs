using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class ApiSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;

    public ApiSmokeTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _client = factory.CreateClient();
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    [Fact]
    public async Task Health_IsOk()
    {
        var (status, body, _) = await Send(() => _client.GetAsync("/api/health"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ui_IsServed()
    {
        string html = await _client.GetStringAsync("/");
        Assert.Contains("象棋擂台", html);
        Assert.Contains("https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js", html);
        Assert.Contains("/app.js", html);

        string script = await _client.GetStringAsync("/app.js");
        Assert.Contains("楚河", script);
        Assert.Contains("汉界", script);
        Assert.Contains("JoinRoom", script);
        foreach (string character in new[] { "帥", "仕", "相", "傌", "俥", "炮", "兵", "將", "士", "象", "馬", "車", "砲", "卒" })
            Assert.Contains(character, script);

        string css = await _client.GetStringAsync("/style.css");
        Assert.Contains("board", css);
    }

    [Fact]
    public async Task CreateRoom_List_Join_AndRejectBadMoves()
    {
        var bad = await Send(() => _client.PostAsJsonAsync("/api/games/vs-ai", new { humanColor = "red", difficulty = "impossible" }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.False(string.IsNullOrWhiteSpace(bad.Body.GetProperty("error").GetString()));

        var created = await Send(() => _client.PostAsJsonAsync("/api/rooms", new
        {
            name = "友谊赛",
            redKind = "human",
            blackKind = "open"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid id = created.Body.GetProperty("id").GetGuid();
        string? redToken = created.Body.GetProperty("tokens").GetProperty("red").GetString();
        Assert.False(string.IsNullOrWhiteSpace(redToken));
        Assert.Equal(JsonValueKind.Null, created.Body.GetProperty("tokens").GetProperty("black").ValueKind);
        Assert.Equal("友谊赛", created.Body.GetProperty("name").GetString());

        JsonElement state = created.Body.GetProperty("state");
        Assert.Equal(id, state.GetProperty("id").GetGuid());
        Assert.Equal("red", state.GetProperty("sideToMove").GetString());
        Assert.Equal("ongoing", state.GetProperty("status").GetString());
        Assert.False(state.GetProperty("inCheck").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("lastMove").ValueKind);
        Assert.Equal(0, state.GetProperty("history").GetArrayLength());
        Assert.Equal(10, state.GetProperty("board").GetArrayLength());
        Assert.Equal(9, state.GetProperty("board")[0].GetArrayLength());
        Assert.Equal("r", state.GetProperty("board")[0][0].GetString());
        Assert.Equal("k", state.GetProperty("board")[0][4].GetString());
        Assert.Equal("R", state.GetProperty("board")[9][0].GetString());
        Assert.Equal("K", state.GetProperty("board")[9][4].GetString());
        Assert.Equal("human", state.GetProperty("seats").GetProperty("red").GetProperty("kind").GetString());
        Assert.Equal("open", state.GetProperty("seats").GetProperty("black").GetProperty("kind").GetString());
        Assert.False(state.GetProperty("seats").GetProperty("black").GetProperty("occupied").GetBoolean());

        var list = await Send(() => _client.GetAsync("/api/rooms"));
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains(list.Body.EnumerateArray(), room => room.GetProperty("id").GetGuid() == id);

        var missing = await Send(() => _client.GetAsync($"/api/rooms/{Guid.NewGuid()}"));
        Assert.Equal(HttpStatusCode.NotFound, missing.Status);

        var moves = await Send(() => _client.GetAsync($"/api/rooms/{id}/moves"));
        Assert.Equal(HttpStatusCode.OK, moves.Status);
        var notations = moves.Body.EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.NotEmpty(notations);
        Assert.All(notations, notation => Assert.Equal(4, notation.Length));

        string fen = state.GetProperty("fen").GetString()!;
        var illegal = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = "a0a0", token = redToken }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, illegal.Status);
        var afterIllegal = await Send(() => _client.GetAsync($"/api/rooms/{id}"));
        Assert.Equal(fen, afterIllegal.Body.GetProperty("fen").GetString());

        var joined = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "black", kind = "bot" }, Json));
        Assert.Equal(HttpStatusCode.OK, joined.Status);
        string? botToken = joined.Body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(botToken));
        Assert.Equal("bot", joined.Body.GetProperty("state").GetProperty("seats").GetProperty("black").GetProperty("kind").GetString());

        var again = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "black", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.Conflict, again.Status);

        var wrongSeat = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = notations[0], token = botToken }, Json));
        Assert.Equal(HttpStatusCode.Forbidden, wrongSeat.Status);

        var played = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = notations[0], token = redToken }, Json));
        Assert.Equal(HttpStatusCode.OK, played.Status);
        Assert.Equal(notations[0], played.Body.GetProperty("history")[0].GetString());
        Assert.Equal(notations[0], played.Body.GetProperty("lastMove").GetString());
        Assert.Equal("black", played.Body.GetProperty("sideToMove").GetString());
    }

    [Fact]
    public async Task VsAi_HumanMove_ThenEngineReplies_AndIllegalMoveIs400()
    {
        var created = await Send(() => _client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "red",
            difficulty = "easy"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid roomId = created.Body.GetProperty("roomId").GetGuid();
        string token = created.Body.GetProperty("token").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(token));
        JsonElement opening = created.Body.GetProperty("state");
        Assert.Equal("red", opening.GetProperty("sideToMove").GetString());
        Assert.Equal("human", opening.GetProperty("seats").GetProperty("red").GetProperty("kind").GetString());
        Assert.Equal("ai", opening.GetProperty("seats").GetProperty("black").GetProperty("kind").GetString());
        Assert.Equal("easy", opening.GetProperty("seats").GetProperty("black").GetProperty("difficulty").GetString());

        var moves = await Send(() => _client.GetAsync($"/api/rooms/{roomId}/moves"));
        Assert.Equal(HttpStatusCode.OK, moves.Status);
        string move = moves.Body.EnumerateArray().First().GetString()!;
        Assert.Equal(4, move.Length);

        var played = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{roomId}/moves", new { move, token }, Json));
        Assert.Equal(HttpStatusCode.OK, played.Status);
        Assert.Equal(move, played.Body.GetProperty("history")[0].GetString());
        Assert.Equal("black", played.Body.GetProperty("sideToMove").GetString());

        JsonElement afterAi = await WaitForPly(roomId, 2);
        Assert.Equal("ongoing", afterAi.GetProperty("status").GetString());
        Assert.Equal("red", afterAi.GetProperty("sideToMove").GetString());
        Assert.Equal(move, afterAi.GetProperty("history")[0].GetString());
        Assert.Equal(4, afterAi.GetProperty("history")[1].GetString()!.Length);
        string fen = afterAi.GetProperty("fen").GetString()!;

        var illegal = await Send(() => _client.PostAsJsonAsync($"/api/rooms/{roomId}/moves", new { move = "a0a0", token }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, illegal.Status);
        Assert.False(string.IsNullOrWhiteSpace(illegal.Body.GetProperty("error").GetString()));

        var unchanged = await Send(() => _client.GetAsync($"/api/rooms/{roomId}"));
        Assert.Equal(fen, unchanged.Body.GetProperty("fen").GetString());
        Assert.Equal(2, unchanged.Body.GetProperty("history").GetArrayLength());
    }

    private async Task<JsonElement> WaitForPly(Guid roomId, int ply)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await Send(() => _client.GetAsync($"/api/rooms/{roomId}"));
            Assert.Equal(HttpStatusCode.OK, snapshot.Status);
            last = snapshot.Raw;
            if (snapshot.Body.GetProperty("history").GetArrayLength() >= ply)
                return snapshot.Body;
            await Task.Delay(100);
        }

        throw new TimeoutException("AI did not reply. Last state: " + last);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> Send(Func<Task<HttpResponseMessage>> call)
    {
        using HttpResponseMessage response = await call();
        string raw = await response.Content.ReadAsStringAsync();
        JsonElement body = default;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            body = doc.RootElement.Clone();
        }

        return (response.StatusCode, body, raw);
    }
}
