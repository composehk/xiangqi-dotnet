using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Xiangqi.Tests;

public class ForwardedHeadersTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ForwardedHeadersTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly object CreateBody = new
    {
        moves = "炮二平五 马8进7 马二进三",
        ply = 2,
        difficulty = "easy",
        humanColor = "red",
    };

    [Fact]
    public async Task DefaultIgnoresXForwardedFor()
    {
        using var client = ContinueHarness.Configured(_factory, settings: new[] { ("Payment:CreatePerMinute", "3") });

        HttpStatusCode[] codes = new HttpStatusCode[5];
        for (int i = 0; i < 5; i++)
            codes[i] = await PostCreate(client, $"1.2.3.{i + 1}");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, codes[0]);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, codes[1]);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, codes[2]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[3]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[4]);
    }

    [Fact]
    public async Task KnownProxy_HonorsXForwardedFor()
    {
        using var client = ClientBehind("10.9.9.9", ("Forwarded:KnownProxies:0", "10.9.9.9"), ("Payment:CreatePerMinute", "3"));

        for (int i = 0; i < 3; i++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostCreate(client, "1.1.1.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostCreate(client, "1.1.1.1"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostCreate(client, "2.2.2.2"));
    }

    [Fact]
    public async Task UntrustedProxy_IgnoresXForwardedFor()
    {
        using var client = ClientBehind("10.8.8.8", ("Forwarded:KnownProxies:0", "10.9.9.9"), ("Payment:CreatePerMinute", "3"));

        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostCreate(client, "1.1.1.1"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostCreate(client, "2.2.2.2"));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, await PostCreate(client, "3.3.3.3"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostCreate(client, "4.4.4.4"));
    }

    [Fact]
    public void InvalidProxyConfigFailsStartup()
    {
        Assert.ThrowsAny<Exception>(() =>
            ContinueHarness.Configured(_factory, settings: new[] { ("Forwarded:KnownProxies:0", "not-an-ip") }));
    }

    /// <summary>
    /// TestServer's peer is loopback. Pretend the TCP peer is <paramref name="remote"/> before forwarded-headers runs,
    /// so only a configured KnownProxy is allowed to supply X-Forwarded-For.
    /// </summary>
    private HttpClient ClientBehind(string remote, params (string Key, string Value)[] settings)
    {
        var primed = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureTestServices(services =>
            {
                services.AddTransient<IStartupFilter>(_ => new ForceRemoteIpStartupFilter(IPAddress.Parse(remote)));
            });
        });
        return ContinueHarness.Configured(primed, settings: settings);
    }

    private static async Task<HttpStatusCode> PostCreate(HttpClient client, string forwardedFor)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/continue/sessions")
        {
            Content = JsonContent.Create(CreateBody, options: ContinueHarness.Json),
        };
        req.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        var (status, _) = await ContinueHarness.Send(client.SendAsync(req));
        return status;
    }

    private sealed class ForceRemoteIpStartupFilter : IStartupFilter
    {
        private readonly IPAddress _remote;
        public ForceRemoteIpStartupFilter(IPAddress remote) => _remote = remote;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (HttpContext ctx, RequestDelegate nxt) =>
            {
                ctx.Connection.RemoteIpAddress = _remote;
                await nxt(ctx);
            });
            next(app);
        };
    }
}
