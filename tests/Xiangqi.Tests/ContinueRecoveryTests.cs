using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class ContinueRecoveryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ContinueRecoveryTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static string Tx(string to, long amount, string? from = null) =>
        PaymentVerifierTests.TxJson(to, amount, from: from ?? ContinueHarness.PayerAddress);

    [Fact]
    public async Task ExpiredPendingPaymentWithinGraceActivates()
    {
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, clock: clock);
        var s = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, s, ts: clock.UnixSeconds)).Status);

        clock.Advance(TimeSpan.FromMinutes(61));
        var (paid, body) = await ContinueHarness.Pay(c, s, new string('a', 64));
        Assert.Equal(HttpStatusCode.OK, paid);
        Assert.Equal("active", body.GetProperty("status").GetString());
        Assert.NotEqual("expired", (await ContinueHarness.GetSession(c, s)).Body.GetProperty("status").GetString());

        var (moved, movedBody) = await ContinueHarness.Move(c, s, "马二进三");
        Assert.Equal(HttpStatusCode.OK, moved);
        Assert.Equal("active", movedBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExpiredBeyondGraceDoesNotConsumeTxid()
    {
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, clock: clock);
        var s = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, s, ts: clock.UnixSeconds)).Status);

        clock.Advance(TimeSpan.FromMinutes(60 + 1440).Add(TimeSpan.FromSeconds(1)));
        string txid = new string('b', 64);
        Assert.Equal(HttpStatusCode.Gone, (await ContinueHarness.Pay(c, s, txid)).Status);
        Assert.NotEqual("active", (await ContinueHarness.GetSession(c, s)).Body.GetProperty("status").GetString());

        var fresh = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, fresh, ts: clock.UnixSeconds)).Status);
        var (retry, retryBody) = await ContinueHarness.Pay(c, fresh, txid);
        Assert.Equal(HttpStatusCode.OK, retry);
        Assert.Equal("active", retryBody.GetProperty("status").GetString());
        Assert.Equal(txid, retryBody.GetProperty("txid").GetString());
    }

    [Fact]
    public async Task ExpiredPendingAuthCannotPay()
    {
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, clock: clock);
        var s = await ContinueHarness.Create(c);
        clock.Advance(TimeSpan.FromMinutes(61));

        var pay = await ContinueHarness.Pay(c, s, new string('c', 64));
        Assert.True(pay.Status is HttpStatusCode.Conflict or HttpStatusCode.Gone);
        var got = await ContinueHarness.GetSession(c, s);
        Assert.NotEqual("active", got.Body.GetProperty("status").GetString());
        Assert.Equal("expired", got.Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WithinGraceStillRejectsBadPayments()
    {
        string payTo = PaymentVerifierTests.PayTo;
        string other = ContinueHarness.AddressOf(ContinueHarness.Priv2);
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, id => (HttpStatusCode.OK, id[0] switch
        {
            '1' => Tx(payTo, 99_999_999),
            '2' => Tx(payTo, 100_000_000, from: other),
            _ => Tx(payTo, 100_000_000),
        }), clock: clock);

        var low = await ContinueHarness.Create(c);
        var wrong = await ContinueHarness.Create(c);
        var first = await ContinueHarness.Create(c);
        var replay = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, low, ts: clock.UnixSeconds)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, wrong, ts: clock.UnixSeconds)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, first, ts: clock.UnixSeconds)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, replay, ts: clock.UnixSeconds)).Status);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Pay(c, low, new string('1', 64))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ContinueHarness.Pay(c, wrong, new string('2', 64))).Status);
        Assert.NotEqual("active", (await ContinueHarness.GetSession(c, low)).Body.GetProperty("status").GetString());
        Assert.NotEqual("active", (await ContinueHarness.GetSession(c, wrong)).Body.GetProperty("status").GetString());

        string txid = new string('d', 64);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(c, first, txid)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await ContinueHarness.Pay(c, replay, txid)).Status);
        Assert.NotEqual("active", (await ContinueHarness.GetSession(c, replay)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ZeroGraceDisablesRecovery()
    {
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, clock: clock, settings: new[] { ("Payment:ExpiredPaymentGraceMinutes", "0") });
        var s = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, s, ts: clock.UnixSeconds)).Status);
        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Gone, (await ContinueHarness.Pay(c, s, new string('e', 64))).Status);
        Assert.NotEqual("active", (await ContinueHarness.GetSession(c, s)).Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AuthorizeOnExpiredSessionStillGone()
    {
        var clock = new MutableClock();
        var c = ContinueHarness.Configured(_factory, clock: clock);
        var pendingAuth = await ContinueHarness.Create(c);
        var pendingPay = await ContinueHarness.Create(c);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(c, pendingPay, ts: clock.UnixSeconds)).Status);

        clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(HttpStatusCode.Gone, (await ContinueHarness.Auth(c, pendingAuth, ts: clock.UnixSeconds)).Status);
        Assert.Equal(HttpStatusCode.Gone, (await ContinueHarness.Auth(c, pendingPay, ts: clock.UnixSeconds)).Status);
    }
}
