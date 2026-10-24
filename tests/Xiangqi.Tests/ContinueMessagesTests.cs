using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xiangqi.Server.Kaspa;
using static Xiangqi.Tests.PaymentVerifierTests;

namespace Xiangqi.Tests;

public class ContinueMessagesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ContinueMessagesTests(WebApplicationFactory<Program> factory)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        _factory = factory;
    }

    private static KaspaPaymentVerifier Verifier(HttpClient http, int minConfirmations = 10) =>
        new(http, new PaymentOptions { PayToAddress = PayTo, ApiBaseUrl = "https://api.test", MinConfirmations = minConfirmations });

    [Fact]
    public async Task ChineseNotationIllegalMove_IsExplicitBilingual400()
    {
        using HttpClient client = ContinueHarness.Configured(_factory);
        var session = await ContinueHarness.Create(client);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Auth(client, session)).Status);
        Assert.Equal(HttpStatusCode.OK, (await ContinueHarness.Pay(client, session, new string('b', 64))).Status);

        const string moveText = "炮二平九";
        var (status, body) = await ContinueHarness.Move(client, session, moveText);
        string error = body.GetProperty("error").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("无法识别或非法的着法", error);
        Assert.Contains("Illegal or unrecognised move", error);
        Assert.Contains(moveText, error);
    }

    [Fact]
    public async Task KaspaApi500_WaitMessageIsBilingual()
    {
        var r = await Verifier(Client("{}", txStatus: HttpStatusCode.InternalServerError)).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
        Assert.Contains("Kaspa API 暂不可用 (500)", r.Error);
        Assert.Contains("Kaspa API unavailable (500)", r.Error);
    }

    [Fact]
    public async Task Confirmations2Of10_PendingMessageIsExact()
    {
        // TxJson accepting_block_blue_score=1000; virtualScore 1002 => conf 2; MinConfirmations 10.
        var r = await Verifier(Client(TxJson(PayTo, 100_000_000), virtualScore: 1002)).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
        Assert.Equal("等待确认 2/10 / waiting for confirmations 2/10", r.Error);
    }

    [Fact]
    public async Task ContinuePay_WaitingConfirmations_202InfoHasNoPaymentFailedWording()
    {
        using HttpClient client = ContinueHarness.Configured(_factory, virtualScore: 1002);
        var session = await ContinueHarness.CreateSigned(client);
        var (status, body) = await ContinueHarness.Pay(client, session, new string('e', 64));
        Assert.Equal(HttpStatusCode.Accepted, status);
        string info = body.GetProperty("info").GetString()!;
        Assert.Contains("等待确认 2/10", info);
        Assert.DoesNotContain("payment verification failed", info, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("支付验证失败", info);
    }
}
