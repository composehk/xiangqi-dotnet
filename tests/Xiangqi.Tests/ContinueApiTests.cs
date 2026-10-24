using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xiangqi.Server;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class ContinueApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Priv = Convert.FromHexString("B7E151628AED2A6ABF7158809CF4F3C762E7160F38B4DA56A784D9045190CFEF");
    private readonly WebApplicationFactory<Program> _factory;

    public ContinueApiTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _factory = factory;
    }

    private static string PayerAddress => KaspaMessageSigner.AddressFromXOnly(KaspaMessageSigner.XOnlyPublicKey(Priv), "mainnet");

    /// <summary>Server with the pay address configured and Kaspa REST mocked: tx amount and blue scores are adjustable.</summary>
    private HttpClient Configured(Func<string, (HttpStatusCode, string)>? tx = null, long virtualScore = 1100)
    {
        tx ??= _ => (HttpStatusCode.OK, PaymentVerifierTests.TxJson(PaymentVerifierTests.PayTo, 100_000_000, from: PayerAddress));
        var app = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Payment:PayToAddress", PaymentVerifierTests.PayTo);
            b.UseSetting("Payment:MinConfirmations", "10");
            b.UseSetting("Payment:PaymentMinIntervalSeconds", "0");
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IPaymentVerifier>();
                services.AddSingleton<IPaymentVerifier>(sp => new KaspaPaymentVerifier(
                    new HttpClient(new PaymentVerifierTests.FakeHandler(req =>
                        req.RequestUri!.AbsolutePath.StartsWith("/transactions/")
                            ? Echo(tx, req.RequestUri.AbsolutePath["/transactions/".Length..])
                            : (HttpStatusCode.OK, $"{{\"blueScore\":{virtualScore}}}"))),
                    sp.GetRequiredService<PaymentOptions>()));
            });
        });
        return app.CreateClient();
    }

    /// <summary>The mocked API echoes the requested txid like the real one (the verifier rejects mismatches).</summary>
    private static (HttpStatusCode, string) Echo(Func<string, (HttpStatusCode, string)> tx, string id)
    {
        var (code, body) = tx(id);
        return (code, body.Replace(PaymentVerifierTests.Txid, id));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> Send(Task<HttpResponseMessage> call)
    {
        using var r = await call;
        string text = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<JsonElement> Create(HttpClient c, string moves = "炮二平五 马8进7 马二进三", int? ply = 2)
    {
        var (status, body) = await Send(c.PostAsJsonAsync("/api/continue/sessions", new { moves, ply, difficulty = "easy", humanColor = "red" }, Json));
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    private static async Task<(HttpStatusCode, JsonElement)> Auth(HttpClient c, JsonElement s, long? ts = null, string? sig = null)
    {
        string id = s.GetProperty("id").GetString()!;
        long timestamp = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        sig ??= Convert.ToHexString(KaspaMessageSigner.Sign(ContinueService.SignMessage(id, timestamp), Priv));
        return await Send(c.PostAsJsonAsync($"/api/continue/sessions/{id}/auth",
            new { token = s.GetProperty("token").GetString(), address = PayerAddress, signature = sig, timestamp }, Json));
    }

    private static Task<(HttpStatusCode, JsonElement)> Pay(HttpClient c, JsonElement s, string txid) =>
        Send(c.PostAsJsonAsync($"/api/continue/sessions/{s.GetProperty("id").GetString()}/payment",
            new { token = s.GetProperty("token").GetString(), txid }, Json));

    private static Task<(HttpStatusCode, JsonElement)> Move(HttpClient c, JsonElement s, string move) =>
        Send(c.PostAsJsonAsync($"/api/continue/sessions/{s.GetProperty("id").GetString()}/moves",
            new { token = s.GetProperty("token").GetString(), move }, Json));

    [Fact]
    public async Task NotConfigured_Returns503AndMovesAre402()
    {
        var c = _factory.CreateClient();
        var (_, cfg) = await Send(c.GetAsync("/api/continue/config"));
        Assert.False(cfg.GetProperty("enabled").GetBoolean());
        // L5: creation itself is refused while the paid entry is closed, and nothing is stored.
        var (created, _) = await Send(c.PostAsJsonAsync("/api/continue/sessions", new { moves = "炮二平五", ply = 1 }, Json));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created);
        var ghost = System.Text.Json.JsonDocument.Parse("{\"id\":\"" + new string('a', 32) + "\",\"token\":\"x\"}").RootElement;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Auth(c, ghost)).Item1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Pay(c, ghost, new string('b', 64))).Item1);
    }

    [Fact]
    public async Task FullFlow_SignPayPlay_ThenTxidReplayRejected()
    {
        var c = Configured();
        var s = await Create(c);
        Assert.Equal(PaymentVerifierTests.PayTo, s.GetProperty("payToAddress").GetString());
        Assert.Equal(100_000_000, s.GetProperty("amountSompi").GetInt64());
        Assert.StartsWith("XiangqiArena:Continue:", s.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Move(c, s, "h0g2")).Item1);

        // Payment before signature is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, s, new string('c', 64))).Item1);
        var (authStatus, authed) = await Auth(c, s);
        Assert.Equal(HttpStatusCode.OK, authStatus);
        Assert.Equal("pending_payment", authed.GetProperty("status").GetString());

        string txid = new('c', 64);
        var (payStatus, paid) = await Pay(c, s, txid);
        Assert.Equal(HttpStatusCode.OK, payStatus);
        Assert.Equal("active", paid.GetProperty("status").GetString());

        var (moveStatus, after) = await Move(c, s, "马二进三");
        Assert.Equal(HttpStatusCode.OK, moveStatus);
        Assert.Equal(2, after.GetProperty("state").GetProperty("history").GetArrayLength()); // human + AI reply
        Assert.Equal("red", after.GetProperty("state").GetProperty("sideToMove").GetString());

        // Same txid for another session => 409 (anti-replay).
        var s2 = await Create(c);
        Assert.Equal(HttpStatusCode.OK, (await Auth(c, s2)).Item1);
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, s2, txid)).Item1);

        // Wrong token cannot play.
        var (forbidden, _) = await Send(c.PostAsJsonAsync($"/api/continue/sessions/{s.GetProperty("id").GetString()}/moves",
            new { token = "nope", move = "h2e2" }, Json));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);
    }

    [Fact]
    public async Task BadSignatureAndStaleTimestampRejected()
    {
        var c = Configured();
        var s = await Create(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await Auth(c, s, sig: new string('0', 128))).Item1);
        Assert.Equal(HttpStatusCode.BadRequest, (await Auth(c, s, ts: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600)).Item1);
    }

    [Fact]
    public async Task LowAmountAndUnconfirmedRejected()
    {
        var low = Configured(_ => (HttpStatusCode.OK, PaymentVerifierTests.TxJson(PaymentVerifierTests.PayTo, 50_000_000, from: PayerAddress)));
        var s = await Create(low);
        await Auth(low, s);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(low, s, new string('d', 64))).Item1);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await Move(low, s, "h0g2")).Item1);

        var unconf = Configured(virtualScore: 1003);
        var s2 = await Create(unconf);
        await Auth(unconf, s2);
        var (status, body) = await Pay(unconf, s2, new string('e', 64));
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal("pending_payment", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ParseEndpointReturnsReplay()
    {
        var c = _factory.CreateClient();
        var (status, body) = await Send(c.PostAsJsonAsync("/api/continue/parse", new { moves = "1. 炮二平五 马8进7" }, Json));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body.GetProperty("fens").GetArrayLength());
        Assert.Equal("h9g7", body.GetProperty("iccs")[1].GetString());
        var (bad, _) = await Send(c.PostAsJsonAsync("/api/continue/parse", new { moves = "炮二平九" }, Json));
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }
}
