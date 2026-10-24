using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xiangqi.Server;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class ContinueSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ContinueSecurityTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static string Tx(string to, long amount, bool accepted = true, long score = 1000, string? from = null) =>
        PaymentVerifierTests.TxJson(to, amount, accepted, score, from ?? ContinueHarness.PayerAddress);

    private static string CustomTx(string? inputsJson, string outputsJson)
    {
        string inputs = inputsJson is null ? "" : "\"inputs\":" + inputsJson + ",";
        return "{\"transaction_id\":\"" + PaymentVerifierTests.Txid + "\",\"is_accepted\":true,\"accepting_block_blue_score\":1000,"
            + inputs + "\"outputs\":" + outputsJson + "}";
    }

    private static string Inputs(string address) =>
        "[{\"previous_outpoint_address\":\"" + address + "\",\"previous_outpoint_amount\":500000000}]";

    private static string Output(string address, long amount, int index) =>
        "{\"index\":" + index + ",\"amount\":" + amount + ",\"script_public_key_address\":\"" + address + "\"}";

    [Fact]
    public async Task ConcurrentSameTxidAcrossTwoSessions()
    {
        var c = ContinueHarness.Configured(_factory);
        for (int i = 0; i < 5; i++)
        {
            var s1 = await ContinueHarness.CreateSigned(c);
            var s2 = await ContinueHarness.CreateSigned(c);
            string txid = new((char)('1' + i), 64);
            var paid = await Task.WhenAll(ContinueHarness.Pay(c, s1, txid), ContinueHarness.Pay(c, s2, txid));
            Assert.Equal(1, paid.Count(p => p.Status == HttpStatusCode.OK));
            Assert.Equal(1, paid.Count(p => p.Status == HttpStatusCode.Conflict));
            var loser = paid[0].Status == HttpStatusCode.Conflict ? s1 : s2;
            Assert.Equal("pending_payment", (await ContinueHarness.GetSession(c, loser)).Body.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.PaymentRequired, (await ContinueHarness.Move(c, loser, "马二进三")).Status);
        }

        var many = new JsonElement[8];
        for (int i = 0; i < many.Length; i++)
            many[i] = await ContinueHarness.CreateSigned(c);
        var race = await Task.WhenAll(many.Select(s => ContinueHarness.Pay(c, s, new string('9', 64))));
        Assert.Equal(1, race.Count(p => p.Status == HttpStatusCode.OK));
        Assert.Equal(7, race.Count(p => p.Status == HttpStatusCode.Conflict));
        Assert.Equal("active", race.Single(p => p.Status == HttpStatusCode.OK).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TxidReplaySequential()
    {
        var c = ContinueHarness.Configured(_factory);
        var a = await ContinueHarness.CreateSigned(c);
        var b = await ContinueHarness.CreateSigned(c);
        string first = new('1', 64);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, a, first)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await ContinueHarness.Pay(c, b, first)).Status);
        Assert.Equal("pending_payment", (await ContinueHarness.GetSession(c, b)).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, b, new string('2', 64))).Status);
    }

    [Fact]
    public async Task AlreadyPaidSessionIsIdempotent()
    {
        int lookups = 0;
        var c = ContinueHarness.Configured(_factory, _ =>
        {
            lookups++;
            return (HttpStatusCode.OK, Tx(PaymentVerifierTests.PayTo, 100_000_000));
        });
        var a = await ContinueHarness.CreateSigned(c);
        string first = new('1', 64);
        var (paid, paidBody) = await ContinueHarness.Pay(c, a, first);
        Assert.Equal(HttpStatusCode.OK, paid);
        Assert.Equal("active", paidBody.GetProperty("status").GetString());
        int afterPay = lookups;

        string second = new('2', 64);
        var (again, againBody) = await ContinueHarness.Pay(c, a, second);
        Assert.Equal(HttpStatusCode.OK, again);
        Assert.Equal("active", againBody.GetProperty("status").GetString());
        Assert.Equal(first, againBody.GetProperty("txid").GetString());
        Assert.Equal(afterPay, lookups);

        var b = await ContinueHarness.CreateSigned(c);
        var (bPay, bBody) = await ContinueHarness.Pay(c, b, second);
        Assert.Equal(HttpStatusCode.OK, bPay);
        Assert.Equal("active", bBody.GetProperty("status").GetString());
        Assert.Equal(second, bBody.GetProperty("txid").GetString());
    }

    [Fact]
    public async Task AmountsThroughTheApi()
    {
        string payTo = PaymentVerifierTests.PayTo;
        string payer = ContinueHarness.PayerAddress;
        string other = ContinueHarness.AddressOf(ContinueHarness.Priv2);
        var c = ContinueHarness.Configured(_factory, id => (HttpStatusCode.OK, id[0] switch
        {
            '1' => Tx(payTo, 99_999_999),
            '2' => Tx(payTo, 100_000_000),
            '3' => Tx(payTo, 150_000_000),
            '4' => CustomTx(Inputs(payer), "[" + Output(payTo, 60_000_000, 0) + "," + Output(payTo, 40_000_000, 1) + "]"),
            _ => Tx(other, 100_000_000),
        }));

        var low = await ContinueHarness.CreateSigned(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Pay(c, low, new string('1', 64))).Status);
        Assert.Equal("pending_payment", (await ContinueHarness.GetSession(c, low)).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ContinueHarness.Move(c, low, "马二进三")).Status);

        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, await ContinueHarness.CreateSigned(c), new string('2', 64))).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, await ContinueHarness.CreateSigned(c), new string('3', 64))).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, await ContinueHarness.CreateSigned(c), new string('4', 64))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Pay(c, await ContinueHarness.CreateSigned(c), new string('5', 64))).Status);
    }

    [Fact]
    public async Task PayerMustBeSigner()
    {
        string payer2 = ContinueHarness.AddressOf(ContinueHarness.Priv2);
        string payTo = PaymentVerifierTests.PayTo;
        var c = ContinueHarness.Configured(_factory, id => (HttpStatusCode.OK, id[0] switch
        {
            '2' => CustomTx(null, "[" + Output(payTo, 100_000_000, 0) + "]"),
            _ => Tx(payTo, 100_000_000, from: payer2),
        }));

        var mismatched = await ContinueHarness.CreateSigned(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Pay(c, mismatched, new string('1', 64))).Status);
        Assert.Equal("pending_payment", (await ContinueHarness.GetSession(c, mismatched)).Body.GetProperty("status").GetString());

        var noInputs = await ContinueHarness.CreateSigned(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Pay(c, noInputs, new string('2', 64))).Status);
        Assert.Equal("pending_payment", (await ContinueHarness.GetSession(c, noInputs)).Body.GetProperty("status").GetString());

        var signedBy2 = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, signedBy2, priv: ContinueHarness.Priv2)).Status);
        var (ok, body) = await ContinueHarness.Pay(c, signedBy2, new string('3', 64));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SignatureAttacks()
    {
        var c = ContinueHarness.Configured(_factory);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var a = await ContinueHarness.Create(c);
        var b = await ContinueHarness.Create(c);
        string idA = a.GetProperty("id").GetString()!;
        string sigA = Convert.ToHexString(KaspaMessageSigner.Sign(ContinueService.SignMessage(idA, now), ContinueHarness.Priv));
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, b, ts: now, sig: sigA)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await ContinueHarness.Pay(c, a, new string('8', 64))).Status);

        var s = await ContinueHarness.Create(c);
        string id = s.GetProperty("id").GetString()!;
        string token = s.GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, s, priv: ContinueHarness.Priv2, address: ContinueHarness.PayerAddress)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, s, signMessageForId: Guid.NewGuid().ToString("N"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, s, ts: now - 660)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, s, ts: now + 660)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, s, address: ContinueHarness.AddressOf(ContinueHarness.Priv, "testnet-10"))).Status);
        foreach (var sig in new[] { "", "zz", new string('0', 128), Convert.ToBase64String(new byte[10]) })
            Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Auth(c, s, ts: now, sig: sig)).Status);

        var (forbidden, _) = await ContinueHarness.Send(c.PostAsJsonAsync($"/api/continue/sessions/{id}/auth",
            new { token = new string('0', 32), address = ContinueHarness.PayerAddress, signature = "zz", timestamp = now }, ContinueHarness.Json));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden);

        string unknown = Guid.NewGuid().ToString("N");
        var (missing, _) = await ContinueHarness.Send(c.PostAsJsonAsync($"/api/continue/sessions/{unknown}/auth",
            new { token, address = ContinueHarness.PayerAddress, signature = "zz", timestamp = now }, ContinueHarness.Json));
        Assert.Equal(HttpStatusCode.NotFound, missing);

        var (authed, authedBody) = await ContinueHarness.Auth(c, s, ts: now - 30);
        Assert.Equal(HttpStatusCode.OK, authed);
        Assert.Equal("pending_payment", authedBody.GetProperty("status").GetString());

        var active = await ContinueHarness.CreateSigned(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, active, new string('1', 64))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await ContinueHarness.Auth(c, active)).Status);
    }

    [Fact]
    public async Task PendingConfirmations()
    {
        async Task AssertUnpaid(HttpClient http)
        {
            var s = await ContinueHarness.CreateSigned(http);
            var (st, body) = await ContinueHarness.Pay(http, s, new string('1', 64));
            Assert.Equal(HttpStatusCode.Accepted, st);
            Assert.Equal("pending_payment", body.GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.PaymentRequired, (await ContinueHarness.Move(http, s, "马二进三")).Status);
        }

        await AssertUnpaid(ContinueHarness.Configured(_factory, virtualScore: 1009));
        var ready = ContinueHarness.Configured(_factory, virtualScore: 1010);
        var paid = await ContinueHarness.CreateSigned(ready);
        var (ok, okBody) = await ContinueHarness.Pay(ready, paid, new string('1', 64));
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal("active", okBody.GetProperty("status").GetString());

        await AssertUnpaid(ContinueHarness.Configured(_factory, _ => (HttpStatusCode.OK, Tx(PaymentVerifierTests.PayTo, 100_000_000, accepted: false))));
        await AssertUnpaid(ContinueHarness.Configured(_factory, _ => (HttpStatusCode.InternalServerError, "{}")));
        await AssertUnpaid(ContinueHarness.Configured(_factory, _ => throw new HttpRequestException("kaspa down")));
    }

    [Fact]
    public async Task Gating402()
    {
        var c = ContinueHarness.Configured(_factory);
        var pendingAuth = await ContinueHarness.Create(c);
        Assert.Equal("pending_auth", pendingAuth.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ContinueHarness.Move(c, pendingAuth, "马二进三")).Status);

        var pendingPay = await ContinueHarness.CreateSigned(c);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ContinueHarness.Move(c, pendingPay, "马二进三")).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, pendingPay, new string('1', 64))).Status);
        var (moved, body) = await ContinueHarness.Move(c, pendingPay, "马二进三");
        Assert.Equal(HttpStatusCode.OK, moved);
        Assert.Equal("active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExpiredSession()
    {
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, clock: clock);

        var active = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, active, ts: clock.UnixSeconds)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, active, new string('1', 64))).Status);

        var pendingAuth = await ContinueHarness.Create(c);
        var pendingPay = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, pendingPay, ts: clock.UnixSeconds)).Status);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal("expired", (await ContinueHarness.GetSession(c, pendingAuth)).Body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Gone, (await ContinueHarness.Auth(c, pendingAuth, ts: clock.UnixSeconds)).Status);
        clock.Advance(TimeSpan.FromHours(25)); // past the 24 h recovery grace window (see ContinueRecoveryTests)
        Assert.Equal(HttpStatusCode.Gone, (await ContinueHarness.Pay(c, pendingPay, new string('2', 64))).Status);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await ContinueHarness.Move(c, pendingPay, "马二进三")).Status);

        clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Move(c, active, "马二进三")).Status);
        Assert.NotEqual("expired", (await ContinueHarness.GetSession(c, active)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RateLimits()
    {
        var limited = ContinueHarness.Configured(_factory, null, 1100, null,
            ("Payment:CreatePerMinute", "3"), ("Payment:ActionPerMinute", "5"));

        async Task<HttpStatusCode> PostCreate()
        {
            var (st, _) = await ContinueHarness.Send(limited.PostAsJsonAsync("/api/continue/sessions",
                new { moves = "炮二平五 马8进7 马二进三", ply = 2, difficulty = "easy", humanColor = "red" }, ContinueHarness.Json));
            return st;
        }

        async Task<HttpStatusCode> PostParse()
        {
            var (st, _) = await ContinueHarness.Send(limited.PostAsJsonAsync("/api/continue/parse",
                new { moves = "炮二平五" }, ContinueHarness.Json));
            return st;
        }

        for (int i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, await PostCreate());
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostCreate());

        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, await PostParse());
        Assert.Equal(HttpStatusCode.TooManyRequests, await PostParse());

        for (int i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Send(limited.GetAsync("/api/continue/config"))).Status);

        var pay = ContinueHarness.Configured(_factory,
            _ => (HttpStatusCode.OK, Tx(PaymentVerifierTests.PayTo, 100_000_000, accepted: false)),
            1100, null, ("Payment:PaymentMinIntervalSeconds", "2"));
        var s = await ContinueHarness.CreateSigned(pay);
        string txid = new('1', 64);
        var (first, firstBody) = await ContinueHarness.Pay(pay, s, txid);
        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal("pending_payment", firstBody.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ContinueHarness.Pay(pay, s, txid)).Status);
    }

    [Fact]
    public async Task InputLimits()
    {
        var c = ContinueHarness.Configured(_factory);
        var s = await ContinueHarness.Create(c);
        string id = s.GetProperty("id").GetString()!;
        string token = s.GetProperty("token").GetString()!;
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        async Task<HttpStatusCode> Post<T>(string path, T body)
        {
            var (st, _) = await ContinueHarness.Send(c.PostAsJsonAsync(path, body, ContinueHarness.Json));
            Assert.True((int)st < 500);
            return st;
        }

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post("/api/continue/sessions", new { moves = new string('m', 20_001) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post("/api/continue/parse", new { moves = new string('m', 20_001) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post("/api/continue/sessions", new { fen = new string('f', 121) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post("/api/continue/parse", new { fen = new string('f', 121) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post($"/api/continue/sessions/{id}/auth",
            new { token = new string('t', 65), address = ContinueHarness.PayerAddress, signature = "00", timestamp = ts }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post($"/api/continue/sessions/{id}/auth",
            new { token, address = new string('a', 101), signature = "00", timestamp = ts }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post($"/api/continue/sessions/{id}/auth",
            new { token, address = ContinueHarness.PayerAddress, signature = new string('s', 257), timestamp = ts }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post($"/api/continue/sessions/{id}/payment",
            new { token, txid = new string('x', 81) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Post($"/api/continue/sessions/{id}/moves",
            new { token, move = new string('v', 65) }));

        var huge = new StringContent("{\"moves\":\"" + new string('a', 70_000) + "\"}", Encoding.UTF8, "application/json");
        var (big, _) = await ContinueHarness.Send(c.PostAsync("/api/continue/parse", huge));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big);

        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Send(c.PostAsJsonAsync("/api/continue/parse",
            new { moves = "炮二平五" }, ContinueHarness.Json))).Status);
    }

    [Fact]
    public async Task InjectionSafety()
    {
        var c = ContinueHarness.Configured(_factory);
        var earlier = await ContinueHarness.Create(c);
        var signed = await ContinueHarness.CreateSigned(c);

        foreach (var raw in new[] { "'; DROP TABLE continue_sessions;--", "../../etc/passwd", "%00" })
        {
            var (st, _) = await ContinueHarness.Send(c.GetAsync("/api/continue/sessions/" + Uri.EscapeDataString(raw)));
            Assert.True((int)st < 500);
            Assert.Equal(HttpStatusCode.NotFound, st);
        }

        foreach (var txid in new[] { "' OR '1'='1", "<script>alert(1)</script>", new string('a', 63) + "'" })
        {
            var (st, _) = await ContinueHarness.Pay(c, signed, txid);
            Assert.True((int)st < 500);
            Assert.Equal(HttpStatusCode.BadRequest, st);
        }

        async Task<HttpStatusCode> BadCreate(string? fen = null, string? moves = null)
        {
            var (st, _) = await ContinueHarness.Send(c.PostAsJsonAsync("/api/continue/sessions",
                new { fen, moves, difficulty = "easy", humanColor = "red" }, ContinueHarness.Json));
            Assert.True((int)st < 500);
            return st;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await BadCreate(moves: "炮二平五'"));
        Assert.Equal(HttpStatusCode.BadRequest, await BadCreate(moves: "<img onerror=x>"));
        Assert.Equal(HttpStatusCode.BadRequest, await BadCreate(moves: "'; DROP TABLE continue_sessions;--"));
        Assert.Equal(HttpStatusCode.BadRequest, await BadCreate(fen: "'\" OR 1=1 --"));
        Assert.Equal(HttpStatusCode.BadRequest, await BadCreate(fen: "<img onerror=x>"));
        Assert.Equal(HttpStatusCode.BadRequest, await BadCreate(fen: "'; DROP TABLE continue_sessions;--"));

        var created = await ContinueHarness.Create(c);
        var (got, body) = await ContinueHarness.GetSession(c, earlier);
        Assert.Equal(HttpStatusCode.OK, got);
        Assert.Equal(earlier.GetProperty("id").GetString(), body.GetProperty("id").GetString());
        Assert.Equal(32, created.GetProperty("id").GetString()!.Length);
    }
}
