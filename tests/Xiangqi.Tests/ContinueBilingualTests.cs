using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Xiangqi.Server;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class ContinueBilingualTests
{
    private static readonly Regex Cjk = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex AsciiWord = new(@"[A-Za-z]{2,}", RegexOptions.Compiled);

    public ContinueBilingualTests() => Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");

    [Fact]
    public async Task BadFen_Create_IsBilingual400()
    {
        using var host = Client(("Payment:PayToAddress", PaymentVerifierTests.PayTo), ("Payment:CreatePerMinute", "0"));
        using HttpResponseMessage response = await host.Client.PostAsJsonAsync(
            "/api/continue/sessions",
            new { fen = "9/9/9/9/9/9/9/9/9 w", difficulty = "easy", humanColor = "red" },
            ContinueHarness.Json);
        string error = await Error(response);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Matches(Cjk, error);
        Assert.Contains(" / ", error);
        Assert.Matches(AsciiWord, error);
    }

    [Fact]
    public async Task BadChineseNotation_Move_IsBilingual400()
    {
        using var factory = new WebApplicationFactory<Program>();
        using HttpClient client = ContinueHarness.Configured(factory,
            settings: new[]
            {
                ("Arena:CreatePerMinute", "0"),
                ("Arena:ActionPerMinute", "0"),
                ("Payment:CreatePerMinute", "0"),
                ("Payment:ActionPerMinute", "0"),
            });
        JsonElement session = await ContinueHarness.Create(client);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(client, session)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(client, session, new string('b', 64))).Status);

        var (status, body) = await ContinueHarness.Move(client, session, "not-a-move");
        string error = body.GetProperty("error").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Matches(Cjk, error);
        Assert.Contains(" / ", error);
        Assert.Matches(AsciiWord, error);
    }

    [Fact]
    public async Task PlayAsync_AfterCleanupInterval_DropsStaleLocks()
    {
        var clock = new MutableClock();
        string path = Path.Combine(Path.GetTempPath(), $"xq-bi-{Guid.NewGuid():N}.db");
        var store = new ContinueStore(path);
        var options = new PaymentOptions
        {
            PayToAddress = PaymentVerifierTests.PayTo,
            Network = "mainnet",
            SessionTtlMinutes = 60,
            ExpiredPaymentGraceMinutes = 10,
            PaymentMinIntervalSeconds = 2,
            PriceSompi = 100_000_000,
            MinConfirmations = 1,
        };
        var svc = new ContinueService(store, new AcceptingVerifier(), options, NullLogger<ContinueService>.Instance, clock);
        ContinueSessionDto stale = svc.Create(new CreateContinueRequest(null, null, null, "easy", "red"));
        try
        {
            svc.Authorize(stale.Id, new ContinueAuthRequest(stale.Token, "kaspa:not-an-address", new string('0', 128), clock.UnixSeconds));
        }
        catch (Exception)
        {
            // The per-session gate stays in _locks after the failed authorize.
        }

        Assert.Contains(stale.Id, LockIds(svc));
        clock.Advance(TimeSpan.FromMinutes(60 + 10).Add(TimeSpan.FromSeconds(1)));

        Exception missing = await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.PlayAsync("not-a-session-id", new ContinueMoveRequest("x", "a0a0"), CancellationToken.None));
        Assert.Contains("Continue session not found", missing.Message);
        Assert.DoesNotContain(stale.Id, LockIds(svc));
    }

    private sealed class AcceptingVerifier : IPaymentVerifier
    {
        public Task<PaymentCheck> VerifyAsync(string txid, string payerAddress, CancellationToken ct = default) =>
            Task.FromResult(new PaymentCheck(true, false, null, 100_000_000, 10, new[] { payerAddress }));
    }

    private static HashSet<string> LockIds(ContinueService svc)
    {
        var dict = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(ContinueService)
            .GetField("_locks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(svc)!;
        return dict.Keys.ToHashSet();
    }

    private static TestApp Client(params (string Key, string Value)[] settings)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arena:CreatePerMinute", "0");
            builder.UseSetting("Arena:ActionPerMinute", "0");
            foreach ((string key, string value) in settings)
                builder.UseSetting(key, value);
        });
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

    private static async Task<string> Error(HttpResponseMessage response)
    {
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetString()!;
    }
}
