using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xiangqi.Server;

namespace Xiangqi.Tests;

public class ArenaLifecycleTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ArenaLifecycleTests()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
    }

    [Fact]
    public async Task ThirdRoom_Returns503_WhenMaxRoomsIs2()
    {
        using WebApplicationFactory<Program> factory = Factory(("Arena:MaxRooms", "2"));
        HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostRoom(client, "human", "open")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostRoom(client, "human", "open")).StatusCode);

        HttpResponseMessage full = await PostRoom(client, "human", "open");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
        JsonElement body = await full.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task AiVsAi_IsForbiddenUnlessAllowed()
    {
        using (WebApplicationFactory<Program> blocked = Factory())
        {
            HttpResponseMessage denied = await PostRoom(blocked.CreateClient(), "ai", "ai");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            JsonElement body = await denied.Content.ReadFromJsonAsync<JsonElement>(Json);
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        }

        using WebApplicationFactory<Program> allowed = Factory(("Arena:AllowAiVsAi", "true"));
        HttpResponseMessage created = await PostRoom(allowed.CreateClient(), "ai", "ai");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    [Fact]
    public async Task VsAiRoom_IsHiddenFromList_ButGetByIdWorks()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage vsAi = await client.PostAsJsonAsync("/api/games/vs-ai", new { humanColor = "red", difficulty = "easy" }, Json);
        Assert.Equal(HttpStatusCode.OK, vsAi.StatusCode);
        JsonElement vsBody = await vsAi.Content.ReadFromJsonAsync<JsonElement>(Json);
        Guid privateId = vsBody.GetProperty("roomId").GetGuid();

        HttpResponseMessage created = await PostRoom(client, "human", "open");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Guid publicId = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        JsonElement list = await client.GetFromJsonAsync<JsonElement>("/api/rooms", Json);
        var ids = new List<Guid>();
        foreach (JsonElement room in list.EnumerateArray())
            ids.Add(room.GetProperty("id").GetGuid());

        Assert.Contains(publicId, ids);
        Assert.DoesNotContain(privateId, ids);

        HttpResponseMessage got = await client.GetAsync($"/api/rooms/{privateId}");
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal(privateId, (await got.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task SweepExpired_RemovesIdleUnfinishedRoom()
    {
        using WebApplicationFactory<Program> factory = Factory(("Arena:SweepIntervalSeconds", "0"));
        HttpClient client = factory.CreateClient();
        ArenaService arena = factory.Services.GetRequiredService<ArenaService>();

        HttpResponseMessage created = await PostRoom(client, "human", "open");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Guid id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Equal(0, arena.SweepExpired(now));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/rooms/{id}")).StatusCode);

        JsonElement before = await client.GetFromJsonAsync<JsonElement>("/api/rooms", Json);
        Assert.Contains(id, before.EnumerateArray().Select(room => room.GetProperty("id").GetGuid()));

        Assert.Equal(1, arena.SweepExpired(now.AddHours(2)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/rooms/{id}")).StatusCode);

        JsonElement after = await client.GetFromJsonAsync<JsonElement>("/api/rooms", Json);
        Assert.DoesNotContain(id, after.EnumerateArray().Select(room => room.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task SweepIntervalZero_AppStarts()
    {
        using WebApplicationFactory<Program> factory = Factory(("Arena:SweepIntervalSeconds", "0"));
        HttpClient client = factory.CreateClient();
        HttpResponseMessage health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        _ = factory.Services.GetRequiredService<ArenaService>();
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

    private static Task<HttpResponseMessage> PostRoom(HttpClient client, string redKind, string blackKind) =>
        client.PostAsJsonAsync("/api/rooms", new { name = "生命周期", redKind, blackKind }, Json);
}
