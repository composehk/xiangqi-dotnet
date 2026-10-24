using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class LobbyGraceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public LobbyGraceTests()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
    }

    [Fact]
    public async Task ZeroGrace_DropsFinishedRoom_KeepsGetAndOngoing()
    {
        using WebApplicationFactory<Program> factory = Factory(graceSeconds: "0");
        HttpClient client = factory.CreateClient();

        Guid finishedId = await CreatePublicVsAi(client);
        Guid ongoingId = await CreatePublicVsAi(client);

        var resigned = await Send(client, () => client.PostAsJsonAsync(
            $"/api/rooms/{finishedId}/resign",
            new { token = _tokens[finishedId] },
            Json));
        Assert.Equal(HttpStatusCode.OK, resigned.Status);
        Assert.NotEqual("ongoing", resigned.Body.GetProperty("status").GetString());

        var listed = await Send(client, () => client.GetAsync("/api/rooms"));
        Assert.Equal(HttpStatusCode.OK, listed.Status);
        List<Guid> ids = RoomIds(listed.Body);
        Assert.DoesNotContain(finishedId, ids);
        Assert.Contains(ongoingId, ids);

        var still = await Send(client, () => client.GetAsync($"/api/rooms/{finishedId}"));
        Assert.Equal(HttpStatusCode.OK, still.Status);
        Assert.Equal(finishedId, still.Body.GetProperty("id").GetGuid());
        Assert.NotEqual("ongoing", still.Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task DefaultGrace_KeepsJustFinishedRoomListed()
    {
        using WebApplicationFactory<Program> factory = Factory(graceSeconds: null);
        HttpClient client = factory.CreateClient();

        Guid finishedId = await CreatePublicVsAi(client);
        var resigned = await Send(client, () => client.PostAsJsonAsync(
            $"/api/rooms/{finishedId}/resign",
            new { token = _tokens[finishedId] },
            Json));
        Assert.Equal(HttpStatusCode.OK, resigned.Status);
        Assert.NotEqual("ongoing", resigned.Body.GetProperty("status").GetString());

        var listed = await Send(client, () => client.GetAsync("/api/rooms"));
        Assert.Equal(HttpStatusCode.OK, listed.Status);
        Assert.Contains(finishedId, RoomIds(listed.Body));

        var still = await Send(client, () => client.GetAsync($"/api/rooms/{finishedId}"));
        Assert.Equal(HttpStatusCode.OK, still.Status);
        Assert.NotEqual("ongoing", still.Body.GetProperty("status").GetString());
    }

    private readonly Dictionary<Guid, string> _tokens = new();

    private async Task<Guid> CreatePublicVsAi(HttpClient client)
    {
        var created = await Send(client, () => client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "red",
            difficulty = "easy",
            spectate = true
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid id = created.Body.GetProperty("roomId").GetGuid();
        string token = created.Body.GetProperty("token").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(token));
        _tokens[id] = token;
        return id;
    }

    private static WebApplicationFactory<Program> Factory(string? graceSeconds)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arena:CreatePerMinute", "0");
            builder.UseSetting("Arena:ActionPerMinute", "0");
            if (graceSeconds is not null)
                builder.UseSetting("Arena:FinishedLobbyGraceSeconds", graceSeconds);
        });
    }

    private static List<Guid> RoomIds(JsonElement list)
    {
        var ids = new List<Guid>();
        foreach (JsonElement room in list.EnumerateArray())
            ids.Add(room.GetProperty("id").GetGuid());
        return ids;
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
