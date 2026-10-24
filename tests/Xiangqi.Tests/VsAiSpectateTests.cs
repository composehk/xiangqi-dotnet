using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class VsAiSpectateTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public VsAiSpectateTests()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
    }

    [Fact]
    public async Task DefaultVsAi_IsHidden_SpectateTrue_IsListed_WithoutAiToken()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        var hidden = await Send(client, () => client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "red",
            difficulty = "hard"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, hidden.Status);
        Guid hiddenId = hidden.Body.GetProperty("roomId").GetGuid();
        string? humanToken = hidden.Body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(humanToken));
        Assert.False(hidden.Body.TryGetProperty("tokens", out _));
        AssertNoAiToken(hidden.Raw, humanToken!);
        string? hiddenName = hidden.Body.GetProperty("state").GetProperty("name").GetString();

        var listedHidden = await Send(client, () => client.GetAsync("/api/rooms"));
        Assert.Equal(HttpStatusCode.OK, listedHidden.Status);
        Assert.Equal(JsonValueKind.Array, listedHidden.Body.ValueKind);
        Assert.DoesNotContain(hiddenId, RoomIds(listedHidden.Body));
        Assert.DoesNotContain(humanToken, listedHidden.Raw, StringComparison.Ordinal);

        var hiddenRoom = await Send(client, () => client.GetAsync($"/api/rooms/{hiddenId}"));
        Assert.Equal(HttpStatusCode.OK, hiddenRoom.Status);
        Assert.Equal(hiddenId, hiddenRoom.Body.GetProperty("id").GetGuid());
        Assert.Equal(hiddenName, hiddenRoom.Body.GetProperty("name").GetString());
        Assert.DoesNotContain(humanToken, hiddenRoom.Raw, StringComparison.Ordinal);

        var shown = await Send(client, () => client.PostAsJsonAsync("/api/games/vs-ai", new
        {
            humanColor = "black",
            difficulty = "hard",
            spectate = true
        }, Json));
        Assert.Equal(HttpStatusCode.OK, shown.Status);
        Guid shownId = shown.Body.GetProperty("roomId").GetGuid();
        string? shownToken = shown.Body.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(shownToken));
        Assert.False(shown.Body.TryGetProperty("tokens", out _));
        AssertNoAiToken(shown.Raw, shownToken!);
        string? shownName = shown.Body.GetProperty("state").GetProperty("name").GetString();
        Assert.Equal("人机 · 执黑 · 困难", shownName);
        Assert.Equal("人机 · 执红 · 困难", hiddenName);

        var listed = await Send(client, () => client.GetAsync("/api/rooms"));
        Assert.Equal(HttpStatusCode.OK, listed.Status);
        Assert.Contains(shownId, RoomIds(listed.Body));
        Assert.DoesNotContain(hiddenId, RoomIds(listed.Body));
        Assert.DoesNotContain(shownToken, listed.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(humanToken, listed.Raw, StringComparison.Ordinal);

        var shownRoom = await Send(client, () => client.GetAsync($"/api/rooms/{shownId}"));
        Assert.Equal(HttpStatusCode.OK, shownRoom.Status);
        Assert.Equal(shownId, shownRoom.Body.GetProperty("id").GetGuid());
        Assert.Equal(shownName, shownRoom.Body.GetProperty("name").GetString());
        Assert.Equal("ai", shownRoom.Body.GetProperty("seats").GetProperty("red").GetProperty("kind").GetString());
        Assert.Equal("hard", shownRoom.Body.GetProperty("seats").GetProperty("red").GetProperty("difficulty").GetString());
        Assert.DoesNotContain(shownToken, shownRoom.Raw, StringComparison.Ordinal);
    }

    private static void AssertNoAiToken(string raw, string humanToken)
    {
        int first = raw.IndexOf(humanToken, StringComparison.Ordinal);
        Assert.True(first >= 0);
        Assert.Equal(-1, raw.IndexOf(humanToken, first + humanToken.Length, StringComparison.Ordinal));
        Assert.DoesNotContain("\"tokens\"", raw, StringComparison.Ordinal);
    }

    private static List<Guid> RoomIds(JsonElement list)
    {
        var ids = new List<Guid>();
        foreach (JsonElement room in list.EnumerateArray())
            ids.Add(room.GetProperty("id").GetGuid());
        return ids;
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
