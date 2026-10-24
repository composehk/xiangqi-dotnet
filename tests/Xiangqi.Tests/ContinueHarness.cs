using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xiangqi.Server;
using Xiangqi.Server.Kaspa;

namespace Xiangqi.Tests;

/// <summary>Controllable server clock: pass it to ContinueHarness.Configured(clock: ...) and move it with Advance.</summary>
internal sealed class MutableClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public long UnixSeconds => _now.ToUnixTimeSeconds();
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Shared helpers for API-level payment tests: configured server + mocked Kaspa REST + signing client.</summary>
internal static class ContinueHarness
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>Signer #1 (the payer used by default).</summary>
    internal static readonly byte[] Priv = Convert.FromHexString("B7E151628AED2A6ABF7158809CF4F3C762E7160F38B4DA56A784D9045190CFEF");
    /// <summary>Signer #2 (a different wallet).</summary>
    internal static readonly byte[] Priv2 = Convert.FromHexString("0B432B2677937381AEF05BB02A66ECD012773062CF3FA2549E44F58ED2401710");

    internal static string AddressOf(byte[] priv, string network = "mainnet") =>
        KaspaMessageSigner.AddressFromXOnly(KaspaMessageSigner.XOnlyPublicKey(priv), network);

    internal static string PayerAddress => AddressOf(Priv);

    /// <summary>
    /// A server with Payment:PayToAddress configured (PaymentVerifierTests.PayTo) and Kaspa REST mocked.
    /// <paramref name="tx"/> maps a txid to (status, json); <paramref name="virtualScore"/> is the mocked chain blue score.
    /// <paramref name="settings"/> are extra configuration overrides, e.g. ("Payment:CreatePerMinute","3").
    /// The per-session payment throttle is disabled unless overridden.
    /// </summary>
    internal static HttpClient Configured(WebApplicationFactory<Program> factory,
        Func<string, (HttpStatusCode, string)>? tx = null, long virtualScore = 1100, TimeProvider? clock = null,
        params (string Key, string Value)[] settings)
    {
        Environment.SetEnvironmentVariable("XIANGQI_TESTING", "1");
        tx ??= _ => (HttpStatusCode.OK, PaymentVerifierTests.TxJson(PaymentVerifierTests.PayTo, 100_000_000, from: PayerAddress));
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Payment:PayToAddress", PaymentVerifierTests.PayTo);
            b.UseSetting("Payment:MinConfirmations", "10");
            b.UseSetting("Payment:PaymentMinIntervalSeconds", "0");
            foreach (var (key, value) in settings)
                b.UseSetting(key, value);
            b.ConfigureServices(services =>
            {
                if (clock is not null)
                {
                    services.RemoveAll<TimeProvider>();
                    services.AddSingleton(clock);
                }
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

    internal static async Task<(HttpStatusCode Status, JsonElement Body)> Send(Task<HttpResponseMessage> call)
    {
        using var r = await call;
        string text = await r.Content.ReadAsStringAsync();
        JsonElement body = default;
        if (!string.IsNullOrEmpty(text))
        {
            try { body = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
        }
        return (r.StatusCode, body);
    }

    /// <summary>Creates a session (POST /api/continue/sessions) and returns its JSON (includes "id" and "token").</summary>
    internal static async Task<JsonElement> Create(HttpClient c, string moves = "炮二平五 马8进7 马二进三", int? ply = 2)
    {
        var (status, body) = await Send(c.PostAsJsonAsync("/api/continue/sessions", new { moves, ply, difficulty = "easy", humanColor = "red" }, Json));
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    /// <summary>Signs the session message with <paramref name="priv"/> (default Priv) and posts /auth. Overridable timestamp/signature/address.</summary>
    internal static async Task<(HttpStatusCode Status, JsonElement Body)> Auth(HttpClient c, JsonElement s,
        long? ts = null, string? sig = null, byte[]? priv = null, string? address = null, string? signMessageForId = null)
    {
        priv ??= Priv;
        string id = s.GetProperty("id").GetString()!;
        long timestamp = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        sig ??= Convert.ToHexString(KaspaMessageSigner.Sign(ContinueService.SignMessage(signMessageForId ?? id, timestamp), priv));
        return await Send(c.PostAsJsonAsync($"/api/continue/sessions/{id}/auth",
            new { token = s.GetProperty("token").GetString(), address = address ?? AddressOf(priv), signature = sig, timestamp }, Json));
    }

    internal static Task<(HttpStatusCode Status, JsonElement Body)> Pay(HttpClient c, JsonElement s, string txid) =>
        Send(c.PostAsJsonAsync($"/api/continue/sessions/{s.GetProperty("id").GetString()}/payment",
            new { token = s.GetProperty("token").GetString(), txid }, Json));

    internal static Task<(HttpStatusCode Status, JsonElement Body)> Move(HttpClient c, JsonElement s, string move) =>
        Send(c.PostAsJsonAsync($"/api/continue/sessions/{s.GetProperty("id").GetString()}/moves",
            new { token = s.GetProperty("token").GetString(), move }, Json));

    internal static Task<(HttpStatusCode Status, JsonElement Body)> GetSession(HttpClient c, JsonElement s) =>
        Send(c.GetAsync($"/api/continue/sessions/{s.GetProperty("id").GetString()}"));

    /// <summary>Creates a session and signs it with the default payer: status becomes pending_payment.</summary>
    internal static async Task<JsonElement> CreateSigned(HttpClient c)
    {
        var s = await Create(c);
        Assert.Equal(HttpStatusCode.OK, (await Auth(c, s)).Status);
        return s;
    }
}
