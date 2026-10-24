using System.Net;
using System.Text.Json;

namespace Xiangqi.Server.Kaspa;

public sealed class PaymentOptions
{
    /// <summary>Platform receiving address. Empty = feature not open (endpoints return 503).</summary>
    public string PayToAddress { get; set; } = "";
    /// <summary>mainnet | testnet-10</summary>
    public string Network { get; set; } = "mainnet";
    /// <summary>Kaspa REST API base (kaspa-rest-server).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.kaspa.org";
    /// <summary>1 KAS = 100,000,000 sompi.</summary>
    public long PriceSompi { get; set; } = 100_000_000;
    public int MinConfirmations { get; set; } = 10;
    public int SignatureMaxAgeSeconds { get; set; } = 600;
    public int SessionTtlMinutes { get; set; } = 60;
    /// <summary>Development-only switch for automated tests. Never enable in production.</summary>
    public bool TestMode { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PayToAddress);
}

public sealed record PaymentCheck(bool Ok, bool Pending, string? Error, long AmountSompi, long Confirmations, string[] InputAddresses)
{
    public static PaymentCheck Fail(string error) => new(false, false, error, 0, 0, Array.Empty<string>());
    public static PaymentCheck Wait(string error, long amount, long conf) => new(false, true, error, amount, conf, Array.Empty<string>());
}

public interface IPaymentVerifier
{
    Task<PaymentCheck> VerifyAsync(string txid, string payerAddress, CancellationToken ct = default);
}

/// <summary>
/// Verifies a 1 KAS service-fee payment through the public Kaspa REST API:
/// GET /transactions/{txid}?inputs=true&amp;outputs=true&amp;resolve_previous_outpoints=light
/// and GET /info/virtual-chain-blue-score for confirmations.
/// </summary>
public sealed class KaspaPaymentVerifier : IPaymentVerifier
{
    private readonly HttpClient _http;
    private readonly PaymentOptions _options;

    public KaspaPaymentVerifier(HttpClient http, PaymentOptions options)
    {
        _http = http;
        _options = options;
    }

    public static bool IsTxid(string? txid) =>
        txid is { Length: 64 } && txid.All(Uri.IsHexDigit);

    public async Task<PaymentCheck> VerifyAsync(string txid, string payerAddress, CancellationToken ct = default)
    {
        if (!IsTxid(txid))
            return PaymentCheck.Fail("txid 格式无效（64 位十六进制） / invalid txid");
        string baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        string payTo = _options.PayToAddress.Trim().ToLowerInvariant();
        string payer = payerAddress.Trim().ToLowerInvariant();

        JsonElement tx;
        using (var resp = await _http.GetAsync(
            $"{baseUrl}/transactions/{txid.ToLowerInvariant()}?inputs=true&outputs=true&resolve_previous_outpoints=light", ct))
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return PaymentCheck.Wait("交易尚未被索引，请稍后重试 / transaction not found yet", 0, 0);
            if (!resp.IsSuccessStatusCode)
                return PaymentCheck.Wait($"Kaspa API 暂不可用 ({(int)resp.StatusCode})", 0, 0);
            tx = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        }

        long paid = 0;
        if (tx.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in outputs.EnumerateArray())
            {
                string? addr = Str(o, "script_public_key_address");
                if (addr != null && addr.Equals(payTo, StringComparison.OrdinalIgnoreCase))
                    paid += Long(o, "amount");
            }
        }
        if (paid == 0)
            return PaymentCheck.Fail("交易中没有支付到平台收款地址的输出 / no output to the platform address");
        if (paid < _options.PriceSompi)
            return PaymentCheck.Fail($"金额不足：{paid} sompi < {_options.PriceSompi} sompi / amount too low");

        var inputs = new List<string>();
        if (tx.TryGetProperty("inputs", out var ins) && ins.ValueKind == JsonValueKind.Array)
            foreach (var i in ins.EnumerateArray())
                if (Str(i, "previous_outpoint_address") is { } a)
                    inputs.Add(a.ToLowerInvariant());
        if (inputs.Count > 0 && !inputs.Contains(payer))
            return PaymentCheck.Fail("付款输入地址与已签名地址不一致 / inputs not from the signed address");

        bool accepted = tx.TryGetProperty("is_accepted", out var acc) && acc.ValueKind == JsonValueKind.True;
        long acceptingScore = Long(tx, "accepting_block_blue_score");
        if (!accepted || acceptingScore <= 0)
            return PaymentCheck.Wait("交易尚未被接受 / not accepted yet", paid, 0);

        long virtualScore;
        using (var resp = await _http.GetAsync($"{baseUrl}/info/virtual-chain-blue-score", ct))
        {
            if (!resp.IsSuccessStatusCode)
                return PaymentCheck.Wait("无法获取当前 blue score", paid, 0);
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
            virtualScore = Long(doc, "blueScore");
        }
        long conf = Math.Max(0, virtualScore - acceptingScore);
        if (conf < _options.MinConfirmations)
            return PaymentCheck.Wait($"确认数不足：{conf}/{_options.MinConfirmations}", paid, conf);

        return new PaymentCheck(true, false, null, paid, conf, inputs.ToArray());
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt64(),
            JsonValueKind.String when long.TryParse(v.GetString(), out long l) => l,
            _ => 0,
        };
    }
}
