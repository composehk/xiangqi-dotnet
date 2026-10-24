using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Xiangqi.Tests;

/// <summary>L5: with no pay-to address the paid entry is closed (503 at creation) and exotic amount encodings never cause a 500.</summary>
public class ContinueClosedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ContinueClosedTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task UnconfiguredPayToGives503AtCreationAndConfigIsDisabled()
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        using var client = _factory.WithWebHostBuilder(b => b.UseSetting("Payment:PayToAddress", "")).CreateClient();
        var (cfgStatus, cfg) = await ContinueHarness.Send(client.GetAsync("/api/continue/config"));
        Assert.Equal(HttpStatusCode.OK, cfgStatus);
        Assert.False(cfg.GetProperty("enabled").GetBoolean());
        var (status, _) = await ContinueHarness.Send(client.PostAsJsonAsync("/api/continue/sessions",
            new { moves = "炮二平五", ply = 1 }, ContinueHarness.Json));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        // free vs-AI and the read-only parse tool keep working while the paid entry is closed
        var (parse, _) = await ContinueHarness.Send(client.PostAsJsonAsync("/api/continue/parse", new { moves = "炮二平五" }, ContinueHarness.Json));
        Assert.Equal(HttpStatusCode.OK, parse);
        var (ai, _) = await ContinueHarness.Send(client.PostAsJsonAsync("/api/games/vs-ai", new { humanColor = "red" }, ContinueHarness.Json));
        Assert.Equal(HttpStatusCode.OK, ai);
    }

    [Theory]
    [InlineData("100000000", HttpStatusCode.OK)]
    [InlineData("100000000.0", HttpStatusCode.OK)]
    [InlineData("1e8", HttpStatusCode.OK)]
    [InlineData("\"100000000\"", HttpStatusCode.OK)]
    [InlineData("99999999.5", HttpStatusCode.BadRequest)]   // unparsable as whole sompi => not paid; fail closed, never 500
    [InlineData("1e30", HttpStatusCode.BadRequest)]
    [InlineData("-5", HttpStatusCode.BadRequest)]
    public async Task AmountEncodingsNeverCrash(string amountJson, HttpStatusCode expected)
    {
        string body = PaymentVerifierTests.TxJson(PaymentVerifierTests.PayTo, 100_000_000, from: ContinueHarness.PayerAddress)
            .Replace("100000000", amountJson);
        using var client = ContinueHarness.Configured(_factory, tx: _ => (HttpStatusCode.OK, body));
        var s = await ContinueHarness.CreateSigned(client);
        var (status, _) = await ContinueHarness.Pay(client, s, PaymentVerifierTests.Txid);
        Assert.NotEqual(HttpStatusCode.InternalServerError, status);
        Assert.Equal(expected, status);
    }
}
