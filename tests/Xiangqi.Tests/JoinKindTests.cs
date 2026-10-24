using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

public class JoinKindTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public JoinKindTests()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
    }

    [Fact]
    public async Task Join_RejectsBuiltinAi_ThenAcceptsHumanAndBot()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        var created = await Send(client, () => client.PostAsJsonAsync("/api/rooms", new
        {
            redKind = "open",
            blackKind = "open"
        }, Json));
        Assert.Equal(HttpStatusCode.OK, created.Status);
        Guid id = created.Body.GetProperty("id").GetGuid();

        var asAi = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "red", kind = "ai" }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, asAi.Status);
        string error = asAi.Body.GetProperty("error").GetString()!;
        Assert.Contains("加入座位只能是 human 或 bot", error, StringComparison.Ordinal);
        Assert.Contains("Joining a seat is only allowed as human or bot.", error, StringComparison.Ordinal);

        var room = await Send(client, () => client.GetAsync($"/api/rooms/{id}"));
        Assert.Equal(HttpStatusCode.OK, room.Status);
        Assert.Equal("open", room.Body.GetProperty("seats").GetProperty("red").GetProperty("kind").GetString());

        var red = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "red", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.OK, red.Status);

        var black = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "black", kind = "bot" }, Json));
        Assert.Equal(HttpStatusCode.OK, black.Status);

        var again = await Send(client, () => client.PostAsJsonAsync($"/api/rooms/{id}/join", new { color = "red", kind = "human" }, Json));
        Assert.Equal(HttpStatusCode.Conflict, again.Status);
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
