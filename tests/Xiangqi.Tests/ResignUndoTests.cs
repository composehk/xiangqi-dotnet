using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xiangqi.Core;

namespace Xiangqi.Tests;

public class ResignUndoTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ResignUndoTests()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
    }

    [Fact]
    public void Resign_SetsWinner_AndThrowsWhenAlreadyOver()
    {
        var game = new Game();
        game.Resign(Color.Red);
        Assert.Equal(GameStatus.BlackWins, game.Status);

        var blackResigns = new Game();
        blackResigns.Resign(Color.Black);
        Assert.Equal(GameStatus.RedWins, blackResigns.Status);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => game.Resign(Color.Black));
        Assert.Equal("The game is already over.", ex.Message);
        Assert.Equal(GameStatus.BlackWins, game.Status);
    }

    [Fact]
    public async Task HumanVsHuman_Resign_ThenMoveAndSecondResignFail()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        var created = await Send(client, () => client.PostAsJsonAsync("/api/rooms", new
        {
            name = "认输",
            redKind = "open",
            blackKind = "open"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid id = created.Body.GetProperty("id").GetGuid();

        var redJoin = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "red", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.OK, redJoin.Status);
        string redToken = redJoin.Body.GetProperty("token").GetString()!;

        var blackJoin = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "black", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.OK, blackJoin.Status);
        Assert.False(string.IsNullOrWhiteSpace(blackJoin.Body.GetProperty("token").GetString()));

        var resigned = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/resign", new { token = redToken }, Json));
        Assert.Equal(HttpStatusCode.OK, resigned.Status);
        Assert.Equal("blackWins", resigned.Body.GetProperty("status").GetString());

        string notation = "b2e2";
        var lateMove = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/moves", new { move = notation, token = redToken }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, lateMove.Status);

        var wrong = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/resign", new { token = "not-a-seat-token" }, Json));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);

        var twice = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/resign", new { token = redToken }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, twice.Status);
        Assert.Contains("already over", twice.Body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VsAi_Red_UndoRestoresStart_ThenRejectsFurtherUndo()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        var created = await Send(client, () => client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "red",
            difficulty = "easy"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid roomId = created.Body.GetProperty("roomId").GetGuid();
        string token = created.Body.GetProperty("token").GetString()!;

        var moves = await Send(client, () => client.GetAsync($"/api/rooms/{roomId}/moves"));
        string move = moves.Body.EnumerateArray().First().GetString()!;
        var played = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/moves", new { move, token }, Json));
        Assert.Equal(HttpStatusCode.OK, played.Status);

        JsonElement afterAi = await WaitForPly(client, roomId, 2);
        Assert.Equal(2, afterAi.GetProperty("history").GetArrayLength());

        var wrong = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/undo", new { token = "not-a-seat-token" }, Json));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.Status);

        var undone = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/undo", new { token }, Json));
        Assert.Equal(HttpStatusCode.OK, undone.Status);
        Assert.Equal(0, undone.Body.GetProperty("history").GetArrayLength());
        Assert.Equal("red", undone.Body.GetProperty("sideToMove").GetString());

        var again = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/undo", new { token }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, again.Status);
        Assert.Contains("Nothing to undo", again.Body.GetProperty("error").GetString(), StringComparison.Ordinal);

        var freshMoves = await Send(client, () => client.GetAsync($"/api/rooms/{roomId}/moves"));
        string next = freshMoves.Body.EnumerateArray().First().GetString()!;
        var replay = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/moves", new { move = next, token }, Json));
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Equal(1, replay.Body.GetProperty("history").GetArrayLength());

        var hvh = await Send(client, () => client.PostAsJsonAsync("/api/rooms", new
        {
            redKind = "open",
            blackKind = "open"
        }, Json));
        Guid hvhId = hvh.Body.GetProperty("id").GetGuid();
        var joined = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{hvhId}/join", new { color = "red", kind = "human" }, Json));
        string hvhToken = joined.Body.GetProperty("token").GetString()!;
        var joinedBlack = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{hvhId}/join", new { color = "black", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.OK, joinedBlack.Status);
        var undoHvh = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{hvhId}/undo", new { token = hvhToken }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, undoHvh.Status);
        Assert.Contains("Undo is only available", undoHvh.Body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VsAi_Black_CannotUndoOpeningAiMove()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        var created = await Send(client, () => client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "black",
            difficulty = "easy"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid roomId = created.Body.GetProperty("roomId").GetGuid();
        string token = created.Body.GetProperty("token").GetString()!;

        JsonElement afterAi = await WaitForPly(client, roomId, 1);
        Assert.Equal(1, afterAi.GetProperty("history").GetArrayLength());
        Assert.Equal("black", afterAi.GetProperty("sideToMove").GetString());

        var undone = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/undo", new { token }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, undone.Status);
        Assert.Contains("Nothing to undo", undone.Body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VsAi_Red_Resign_ThenUndoIsRejected_AndGameStaysOver()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        var created = await Send(client, () => client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "red",
            difficulty = "easy"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid roomId = created.Body.GetProperty("roomId").GetGuid();
        string token = created.Body.GetProperty("token").GetString()!;

        var resigned = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/resign", new { token }, Json));
        Assert.Equal(HttpStatusCode.OK, resigned.Status);
        Assert.NotEqual("ongoing", resigned.Body.GetProperty("status").GetString());

        var undone = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/undo", new { token }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, undone.Status);
        Assert.Contains("The game is over", undone.Body.GetProperty("error").GetString(), StringComparison.Ordinal);

        var room = await Send(client, () => client.GetAsync($"/api/rooms/{roomId}"));
        Assert.Equal(HttpStatusCode.OK, room.Status);
        Assert.NotEqual("ongoing", room.Body.GetProperty("status").GetString());

        var late = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{roomId}/moves", new { move = "b2e2", token }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, late.Status);
    }

    private static WebApplicationFactory<Program> Factory()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arena:CreatePerMinute", "0");
            builder.UseSetting("Arena:ActionPerMinute", "0");
        });
    }

    private static async Task<JsonElement> WaitForPly(HttpClient client, Guid roomId, int ply)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await Send(client, () => client.GetAsync($"/api/rooms/{roomId}"));
            Assert.Equal(HttpStatusCode.OK, snapshot.Status);
            last = snapshot.Raw;
            if (snapshot.Body.GetProperty("history").GetArrayLength() >= ply)
                return snapshot.Body;
            await Task.Delay(100);
        }

        throw new TimeoutException("AI did not reply. Last state: " + last);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> Send(HttpClient client, Func<Task<HttpResponseMessage>> call)
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
