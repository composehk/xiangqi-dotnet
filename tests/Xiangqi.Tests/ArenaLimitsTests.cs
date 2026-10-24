using Microsoft.AspNetCore.TestHost;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Xiangqi.Tests;

public class ArenaLimitsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ArenaLimitsTests()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
    }

    [Fact]
    public async Task VsAiCreate_FourthFromSameIpIs429_OtherIpStillOk()
    {
        using WebApplicationFactory<Program> factory = Factory(("Arena:CreatePerMinute", "3"));
        HttpClient same = factory.CreateClient();
        HttpResponseMessage? last = null;
        for (int i = 0; i < 4; i++)
            last = await PostVsAi(same, ip: null);

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal("application/json", last.Content.Headers.ContentType?.MediaType);
        Assert.True(last.Headers.TryGetValues("Retry-After", out var retry));
        int seconds = int.Parse(retry!.Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 3600);
        using var doc = JsonDocument.Parse(await last.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));

        using HttpResponseMessage other = await PostVsAi(same, ip: "203.0.113.9");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task CreatePerMinute_SharesRoomsAndVsAi()
    {
        using WebApplicationFactory<Program> factory = Factory(("Arena:CreatePerMinute", "3"));
        HttpClient client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await PostVsAi(client, ip: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostVsAi(client, ip: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostRoom(client)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostVsAi(client, ip: null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostRoom(client)).StatusCode);
    }

    [Fact]
    public async Task CreatePerMinute_ZeroDisables()
    {
        using WebApplicationFactory<Program> factory = Factory(("Arena:CreatePerMinute", "0"));
        HttpClient client = factory.CreateClient();
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostVsAi(client, ip: null)).StatusCode);
    }

    [Fact]
    public async Task ActionPerMinute_LimitsJoin_GetRoomsIsUnlimited()
    {
        using WebApplicationFactory<Program> factory = Factory(
            ("Arena:CreatePerMinute", "20"),
            ("Arena:ActionPerMinute", "2"));
        HttpClient client = factory.CreateClient();

        using HttpResponseMessage created = await PostRoom(client);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Guid id = (await created.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetGuid();

        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostJoin(client, id)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await PostJoin(client, id)).StatusCode);
        using HttpResponseMessage limited = await PostJoin(client, id);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        for (int i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/rooms")).StatusCode);
    }

    [Fact]
    public async Task PostRoom_BodyOver64Kb_Returns413()
    {
        using WebApplicationFactory<Program> factory = Factory();
        HttpClient client = factory.CreateClient();
        using var huge = new StringContent("{\"name\":\"" + new string('a', 70_000) + "\"}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("/api/rooms", huge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    private static WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arena:MaxRooms", "1000");
            foreach ((string key, string value) in settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                services.AddTransient<IStartupFilter>(_ => new FakeRemoteIpStartupFilter());
            });
        });
    }

    private static Task<HttpResponseMessage> PostVsAi(HttpClient client, string? ip) =>
        Send(client, ip, new HttpRequestMessage(HttpMethod.Post, "/api/games/vs-ai")
        {
            Content = JsonContent.Create(new { humanColor = "red", difficulty = "easy" }, options: Json),
        });

    private static Task<HttpResponseMessage> PostRoom(HttpClient client) =>
        Send(client, ip: null, new HttpRequestMessage(HttpMethod.Post, "/api/rooms")
        {
            Content = JsonContent.Create(new { name = "限额", redKind = "human", blackKind = "open" }, options: Json),
        });

    private static Task<HttpResponseMessage> PostJoin(HttpClient client, Guid id) =>
        Send(client, ip: null, new HttpRequestMessage(HttpMethod.Post, $"/api/rooms/{id}/join")
        {
            Content = JsonContent.Create(new { color = "black", kind = "human" }, options: Json),
        });

    private static Task<HttpResponseMessage> Send(HttpClient client, string? ip, HttpRequestMessage request)
    {
        if (ip is not null)
            request.Headers.TryAddWithoutValidation("X-Fake-Ip", ip);
        return client.SendAsync(request);
    }

    /// <summary>Same idea as ForwardedHeadersTests.ForceRemoteIpStartupFilter, but the address comes from X-Fake-Ip.</summary>
    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext ctx, RequestDelegate nxt) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Fake-Ip", out var raw)
                    && System.Net.IPAddress.TryParse(raw.ToString(), out var ip))
                    ctx.Connection.RemoteIpAddress = ip;
                await nxt(ctx);
            });
            next(app);
        };
    }
}
