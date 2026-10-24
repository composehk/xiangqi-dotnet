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
    /// <summary>Unpaid (pending_auth / pending_payment) sessions older than this can no longer be signed or paid (410).</summary>
    public int SessionTtlMinutes { get; set; } = 60;
    /// <summary>Extra minutes after <see cref="SessionTtlMinutes"/> in which a signed pending_payment session may still submit its txid. 0 disables recovery.</summary>
    public int ExpiredPaymentGraceMinutes { get; set; } = 1440;
    /// <summary>Per-client-IP fixed-window limits (requests per minute) on POST /api/continue/*. 0 disables.</summary>
    public int CreatePerMinute { get; set; } = 30;
    public int ActionPerMinute { get; set; } = 120;
    /// <summary>Minimum seconds between Kaspa REST lookups for one session (protects the upstream API quota).</summary>
    public int PaymentMinIntervalSeconds { get; set; } = 2;
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
        // Fail closed: any transport / parsing problem leaves the session unpaid (never activates).
        try
        {
            return await VerifyCoreAsync(txid, payerAddress, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PaymentCheck.Wait("Kaspa API 超时，请稍后重试 / Kaspa API timeout", 0, 0);
        }
        catch (HttpRequestException)
        {
            return PaymentCheck.Wait("Kaspa API 暂不可用，请稍后重试 / Kaspa API unavailable", 0, 0);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return PaymentCheck.Wait("Kaspa API 返回了无法解析的数据 / malformed Kaspa API response", 0, 0);
        }
    }

    private async Task<PaymentCheck> VerifyCoreAsync(string txid, string payerAddress, CancellationToken ct)
    {
        if (!IsTxid(txid))
            return PaymentCheck.Fail("txid 格式无效（64 位十六进制） / invalid txid");
        txid = txid.ToLowerInvariant();
        string baseUrl = _options.ApiBaseUrl.TrimEnd('/');
        string payTo = _options.PayToAddress.Trim().ToLowerInvariant();
        string payer = payerAddress.Trim().ToLowerInvariant();

        JsonElement tx;
        using (var resp = await _http.GetAsync(
            $"{baseUrl}/transactions/{txid}?inputs=true&outputs=true&resolve_previous_outpoints=light", ct))
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return PaymentCheck.Wait("交易尚未被索引，请稍后重试 / transaction not found yet", 0, 0);
            if (!resp.IsSuccessStatusCode)
                return PaymentCheck.Wait($"Kaspa API 暂不可用 ({(int)resp.StatusCode})", 0, 0);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return PaymentCheck.Wait("Kaspa API 返回了无法解析的数据 / malformed Kaspa API response", 0, 0);
            tx = doc.RootElement.Clone();
        }

        // The API must answer for the transaction we asked about.
        if (Str(tx, "transaction_id") is { } echoed && !echoed.Equals(txid, StringComparison.OrdinalIgnoreCase))
            return PaymentCheck.Fail("接口返回的交易与请求不符 / transaction id mismatch");

        long paid = 0;
        if (tx.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
        {
            foreach (var o in outputs.EnumerateArray())
            {
                string? addr = Str(o, "script_public_key_address");
                if (addr != null && addr.Trim().Equals(payTo, StringComparison.OrdinalIgnoreCase))
                {
                    long amount = Long(o, "amount");
                    if (amount > 0)
                        paid = checked(paid + amount);
                }
            }
        }
        if (paid == 0)
            return PaymentCheck.Fail("交易中没有支付到平台收款地址的输出 / no output to the platform address");
        if (paid < _options.PriceSompi)
            return PaymentCheck.Fail($"金额不足：{paid} sompi < {_options.PriceSompi} sompi / amount too low");

        // Payer == signer. Fail closed: every input must be resolved and belong to the signed address.
        // (A missing/unresolved input list would otherwise let anyone claim somebody else's public payment.)
        var inputs = new List<string>();
        bool unresolved = false;
        if (tx.TryGetProperty("inputs", out var ins) && ins.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in ins.EnumerateArray())
            {
                if (Str(i, "previous_outpoint_address") is { Length: > 0 } a)
                    inputs.Add(a.Trim().ToLowerInvariant());
                else
                    unresolved = true;
            }
        }
        if (inputs.Count == 0 || unresolved)
            return PaymentCheck.Fail("无法确认付款输入地址，已拒绝。请只用签名地址的 UTXO、单笔支付 / cannot resolve the payer's input addresses (rejected); pay from the signed address in a single transaction");
        if (inputs.Any(a => a != payer))
            return PaymentCheck.Fail("付款交易的输入必须全部来自已签名地址，检测到其他地址的输入；请只用签名地址的 UTXO 单笔支付，不要混用其他地址 / all inputs must come from the signed address; this transaction spends inputs from another address — pay from the signed address only, in a single transaction");

        bool accepted = tx.TryGetProperty("is_accepted", out var acc) && acc.ValueKind == JsonValueKind.True;
        long acceptingScore = Long(tx, "accepting_block_blue_score");
        if (!accepted || acceptingScore <= 0)
            return PaymentCheck.Wait("交易尚未被接受 / not accepted yet", paid, 0);

        long virtualScore;
        using (var resp = await _http.GetAsync($"{baseUrl}/info/virtual-chain-blue-score", ct))
        {
            if (!resp.IsSuccessStatusCode)
                return PaymentCheck.Wait("无法获取当前 blue score", paid, 0);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            virtualScore = doc.RootElement.ValueKind == JsonValueKind.Object ? Long(doc.RootElement, "blueScore") : 0;
        }
        long conf = Math.Max(0, virtualScore - acceptingScore);
        if (conf < Math.Max(1, _options.MinConfirmations))
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
            JsonValueKind.Number when v.TryGetInt64(out long n) => n,
            // Floats / exponents (e.g. 1e8, 100000000.0): exact whole numbers only, otherwise 0 (= not paid).
            JsonValueKind.Number when v.TryGetDecimal(out decimal d) && d == decimal.Truncate(d) && d is >= 0 and <= long.MaxValue => (long)d,
            JsonValueKind.String when long.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long l) => l,
            _ => 0,
        };
    }
}
