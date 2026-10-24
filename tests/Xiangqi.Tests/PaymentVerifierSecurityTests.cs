using System.Net;
using Xiangqi.Server.Kaspa;
using static Xiangqi.Tests.PaymentVerifierTests;

namespace Xiangqi.Tests;

public class PaymentVerifierSecurityTests
{
    private static readonly PaymentOptions Options = new()
    {
        PayToAddress = PayTo,
        ApiBaseUrl = "https://api.test",
        MinConfirmations = 10,
    };

    private static async Task<PaymentCheck> Verify(string txJson, long virtualScore = 1100, string? payer = null)
    {
        var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath.StartsWith("/transactions/")
                ? (HttpStatusCode.OK, txJson)
                : (HttpStatusCode.OK, $"{{\"blueScore\":{virtualScore}}}")));
        return await new KaspaPaymentVerifier(http, Options).VerifyAsync(Txid, payer ?? Payer);
    }

    [Theory]
    [InlineData(100_000_000L)]
    [InlineData(100_000_001L)]
    [InlineData(250_000_000L)]
    public async Task AmountAtOrAbovePrice_IsOk(long amount)
    {
        var r = await Verify(TxJson(PayTo, amount));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(amount, Convert.ToInt64(r.AmountSompi));
    }

    [Fact]
    public async Task AmountTooLow_FailsClosed()
    {
        var r = await Verify(TxJson(PayTo, 99_999_999));
        Assert.False(r.Ok);
        Assert.False(r.Pending);
        Assert.Contains("金额不足", r.Error);
    }

    [Fact]
    public async Task SplitPayToOutputs_RequireFullSum()
    {
        var ok = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"}],"outputs":[{"amount":60000000,"script_public_key_address":"{{PayTo}}"},{"amount":40000000,"script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(100_000_000L, Convert.ToInt64(ok.AmountSompi));

        var low = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"}],"outputs":[{"amount":60000000,"script_public_key_address":"{{PayTo}}"},{"amount":39999999,"script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.False(low.Ok);
    }

    [Fact]
    public async Task OutputToOtherAddress_Fails()
    {
        var r = await Verify(TxJson(Payer, 500_000_000));
        Assert.False(r.Ok);
        Assert.Contains("收款地址", r.Error);
    }

    [Fact]
    public async Task ZeroPayToAmount_IsNotOk()
    {
        var r = await Verify(TxJson(PayTo, 0));
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task PayToInUpperCase_StillCounts()
    {
        var r = await Verify(TxJson(PayTo.ToUpperInvariant(), 100_000_000));
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public async Task MissingOutputs_IsNotOk()
    {
        var r = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"}]}
            """);
        Assert.False(r.Ok);
    }

    [Theory]
    [InlineData(1009L)]
    [InlineData(0L)]
    [InlineData(900L)]
    public async Task TooFewConfirmations_IsPending(long virtualScore)
    {
        var r = await Verify(TxJson(PayTo, 100_000_000), virtualScore);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
    }

    [Fact]
    public async Task ExactMinConfirmations_IsOk()
    {
        var r = await Verify(TxJson(PayTo, 100_000_000), 1010);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(10, r.Confirmations);
    }

    [Fact]
    public async Task OneAboveMinConfirmations_IsOk()
    {
        var r = await Verify(TxJson(PayTo, 100_000_000), 1011);
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public async Task NotAccepted_IsPending()
    {
        var r = await Verify(TxJson(PayTo, 100_000_000, accepted: false));
        Assert.False(r.Ok);
        Assert.True(r.Pending);
    }

    [Fact]
    public async Task ZeroOrMissingAcceptingScore_IsPending()
    {
        var zero = await Verify(TxJson(PayTo, 100_000_000, score: 0));
        Assert.False(zero.Ok);
        Assert.True(zero.Pending);

        var missing = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"inputs":[{"previous_outpoint_address":"{{Payer}}"}],"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.False(missing.Ok);
        Assert.True(missing.Pending);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "err")]
    [InlineData(HttpStatusCode.OK, "{}")]
    public async Task BlueScoreEndpointUnusable_IsPending(HttpStatusCode status, string body)
    {
        var tx = TxJson(PayTo, 100_000_000);
        var http = new HttpClient(new FakeHandler(req =>
            req.RequestUri!.AbsolutePath.StartsWith("/transactions/")
                ? (HttpStatusCode.OK, tx)
                : (status, body)));
        var r = await new KaspaPaymentVerifier(http, Options).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
    }

    [Fact]
    public async Task InputsMatchPayer_IsOk()
    {
        var r = await Verify(TxJson(PayTo, 100_000_000));
        Assert.True(r.Ok, r.Error);
        Assert.Contains(Payer, r.InputAddresses);
    }

    [Fact]
    public async Task InputsFromOtherAddress_FailsClosed()
    {
        var r = await Verify(TxJson(PayTo, 100_000_000, from: PayTo));
        Assert.False(r.Ok);
        Assert.False(r.Pending);
        Assert.Contains("签名地址", r.Error);
    }

    [Fact]
    public async Task MissingEmptyOrAddresslessInputs_FailClosed()
    {
        string[] bodies =
        [
            $$"""{"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}""",
            $$"""{"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[],"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}""",
            $$"""{"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{}],"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}""",
        ];
        foreach (var body in bodies)
        {
            var r = await Verify(body);
            Assert.False(r.Ok);
            Assert.False(r.Pending);
        }
    }

    [Fact]
    public async Task OneForeignInput_IsNotOk()
    {
        var r = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"},{"previous_outpoint_address":"{{PayTo}}"}],"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task AllInputsFromPayer_IsOk()
    {
        var r = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"},{"previous_outpoint_address":"{{Payer}}"}],"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.True(r.Ok, r.Error);
    }

    [Fact]
    public async Task PayerArgumentInUpperCase_IsOk()
    {
        var r = await Verify(TxJson(PayTo, 100_000_000), payer: Payer.ToUpperInvariant());
        Assert.True(r.Ok, r.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task TxEndpointError_IsPending(HttpStatusCode status)
    {
        var r = await new KaspaPaymentVerifier(Client("{}", txStatus: status), Options).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
    }

    [Fact]
    public async Task HandlerExceptions_ArePending()
    {
        var net = new HttpClient(new FakeHandler(_ => throw new HttpRequestException("down")));
        var r1 = await new KaspaPaymentVerifier(net, Options).VerifyAsync(Txid, Payer);
        Assert.False(r1.Ok);
        Assert.True(r1.Pending);

        var timeout = new HttpClient(new FakeHandler(_ => throw new TaskCanceledException()));
        var r2 = await new KaspaPaymentVerifier(timeout, Options).VerifyAsync(Txid, Payer);
        Assert.False(r2.Ok);
        Assert.True(r2.Pending);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task MalformedTxBody_IsPending(string body)
    {
        var r = await new KaspaPaymentVerifier(Client(body), Options).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
    }

    [Fact]
    public async Task AmountAsString_IsNotOk()
    {
        var r = await Verify($$"""
            {"transaction_id":"{{Txid}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"}],"outputs":[{"amount":"abc","script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task TransactionIdMismatch_FailsClosed()
    {
        var other = new string('b', 64);
        var r = await Verify($$"""
            {"transaction_id":"{{other}}","is_accepted":true,"accepting_block_blue_score":1000,"inputs":[{"previous_outpoint_address":"{{Payer}}"}],"outputs":[{"amount":100000000,"script_public_key_address":"{{PayTo}}"}]}
            """);
        Assert.False(r.Ok);
        Assert.False(r.Pending);
    }

    [Fact]
    public async Task CallerCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var http = new HttpClient(new CancelHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KaspaPaymentVerifier(http, Options).VerifyAsync(Txid, Payer, cts.Token));
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("")]
    [InlineData("a?b=c")]
    public async Task InvalidTxid_MakesNoHttpRequest(string txid)
    {
        await AssertNoRequest(txid);
    }

    [Fact]
    public async Task NonHexAndTraversalTxids_MakeNoHttpRequest()
    {
        await AssertNoRequest(new string('g', 64));
        await AssertNoRequest(Txid + "/../x");
    }

    [Fact]
    public async Task UpperCaseTxid_RequestsLowerCasePath()
    {
        string? path = null;
        var http = new HttpClient(new FakeHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.StartsWith("/transactions/"))
            {
                path = req.RequestUri.AbsolutePath;
                return (HttpStatusCode.OK, TxJson(PayTo, 100_000_000));
            }
            return (HttpStatusCode.OK, """{"blueScore":1100}""");
        }));
        var r = await new KaspaPaymentVerifier(http, Options).VerifyAsync(Txid.ToUpperInvariant(), Payer);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("/transactions/" + Txid, path);
    }

    private static async Task AssertNoRequest(string txid)
    {
        var calls = 0;
        var http = new HttpClient(new FakeHandler(_ =>
        {
            calls++;
            return (HttpStatusCode.OK, TxJson(PayTo, 100_000_000));
        }));
        var r = await new KaspaPaymentVerifier(http, Options).VerifyAsync(txid, Payer);
        Assert.False(r.Ok);
        Assert.Equal(0, calls);
    }

    private sealed class CancelHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
