using System.Net;
using System.Text;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

public class PaymentVerifierTests
{
    internal const string PayTo = "kaspa:qqcp4tea3q82cr9m9t3ctuc633ap89pd80jff5ma5v0a43rfhmw96fpnq6hu2";
    internal const string Payer = "kaspa:qr85gz7hvp76mtws6p236uv5xxdk57dzfe4p06ues0rlqhtqy3rtqqx7eeq3u";
    internal static readonly string Txid = new('a', 64);

    internal sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode, string)> _respond;
        public FakeHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (code, body) = _respond(request);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    internal static string TxJson(string to, long amount, bool accepted = true, long score = 1000, string from = Payer) => $$"""
        {"transaction_id":"{{Txid}}","is_accepted":{{(accepted ? "true" : "false")}},"accepting_block_blue_score":{{score}},
         "inputs":[{"previous_outpoint_address":"{{from}}","previous_outpoint_amount":500000000}],
         "outputs":[{"index":0,"amount":{{amount}},"script_public_key_address":"{{to}}"},
                    {"index":1,"amount":12345,"script_public_key_address":"{{from}}"}]}
        """;

    internal static HttpClient Client(string txJson, long virtualScore = 1100, HttpStatusCode txStatus = HttpStatusCode.OK) =>
        new(new FakeHandler(req => req.RequestUri!.AbsolutePath.StartsWith("/transactions/")
            ? (txStatus, txJson)
            : (HttpStatusCode.OK, $"{{\"blueScore\":{virtualScore}}}")));

    private static KaspaPaymentVerifier Verifier(HttpClient http) =>
        new(http, new PaymentOptions { PayToAddress = PayTo, ApiBaseUrl = "https://api.test", MinConfirmations = 10 });

    [Fact]
    public async Task ValidPaymentPasses()
    {
        var r = await Verifier(Client(TxJson(PayTo, 100_000_000))).VerifyAsync(Txid, Payer);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(100, r.Confirmations);
    }

    [Fact]
    public async Task AmountTooLowFails()
    {
        var r = await Verifier(Client(TxJson(PayTo, 99_999_999))).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.False(r.Pending);
        Assert.Contains("金额不足", r.Error);
    }

    [Fact]
    public async Task WrongAddressFails()
    {
        var r = await Verifier(Client(TxJson(Payer, 500_000_000))).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.False(r.Pending);
    }

    [Fact]
    public async Task UnconfirmedIsPending()
    {
        var r = await Verifier(Client(TxJson(PayTo, 100_000_000), virtualScore: 1005)).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.True(r.Pending);
        var r2 = await Verifier(Client(TxJson(PayTo, 100_000_000, accepted: false))).VerifyAsync(Txid, Payer);
        Assert.True(r2.Pending);
        var r3 = await Verifier(Client("{}", txStatus: HttpStatusCode.NotFound)).VerifyAsync(Txid, Payer);
        Assert.True(r3.Pending);
    }

    [Fact]
    public async Task InputsFromOtherAddressFail()
    {
        var r = await Verifier(Client(TxJson(PayTo, 100_000_000, from: PayTo))).VerifyAsync(Txid, Payer);
        Assert.False(r.Ok);
        Assert.Contains("签名地址", r.Error);
    }

    [Fact]
    public async Task BadTxidFails()
    {
        var r = await Verifier(Client(TxJson(PayTo, 100_000_000))).VerifyAsync("xyz", Payer);
        Assert.False(r.Ok);
    }
}
