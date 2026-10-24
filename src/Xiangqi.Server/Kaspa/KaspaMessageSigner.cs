using System.Text;
using NBitcoin.Secp256k1;

namespace Xiangqi.Server.Kaspa;

/// <summary>
/// Kaspa personal-message signing as implemented by rusty-kaspa
/// (wallet/core/src/message.rs) and used by KasWare's Schnorr signMessage:
/// digest = BLAKE2b-256(key = "PersonalMessageSigningHash", utf8(message)),
/// signature = BIP340 Schnorr over that digest with the address's x-only key.
/// </summary>
public static class KaspaMessageSigner
{
    private static readonly byte[] DomainKey = Encoding.ASCII.GetBytes("PersonalMessageSigningHash");

    public static byte[] PersonalMessageHash(string message) =>
        Blake2b.Hash256(Encoding.UTF8.GetBytes(message), DomainKey);

    public static string AddressPrefix(string network) =>
        network.Trim().ToLowerInvariant() switch
        {
            "mainnet" or "" => "kaspa",
            "testnet" or "testnet-10" or "testnet-11" => "kaspatest",
            "devnet" => "kaspadev",
            "simnet" => "kaspasim",
            _ => throw new ArgumentException($"Unknown Kaspa network '{network}'."),
        };

    /// <summary>Decodes a 64-byte Schnorr signature from hex (128 chars) or base64.</summary>
    public static byte[]? DecodeSignature(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;
        string s = signature.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        if (s.Length == 128)
        {
            try { return Convert.FromHexString(s); } catch (FormatException) { }
        }
        try
        {
            byte[] raw = Convert.FromBase64String(s);
            return raw.Length == 64 ? raw : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static bool VerifyDigest(ReadOnlySpan<byte> xOnly, ReadOnlySpan<byte> digest, ReadOnlySpan<byte> sig)
    {
        if (xOnly.Length != 32 || digest.Length != 32 || sig.Length != 64)
            return false;
        if (!ECXOnlyPubKey.TryCreate(xOnly, Context.Instance, out var pub) || pub is null)
            return false;
        if (!SecpSchnorrSignature.TryCreate(sig, out var schnorr) || schnorr is null)
            return false;
        return pub.SigVerifyBIP340(schnorr, digest);
    }

    /// <summary>
    /// Verifies that <paramref name="signature"/> signs <paramref name="message"/> with the key
    /// encoded in a Schnorr P2PK <paramref name="address"/> on the expected network.
    /// </summary>
    public static SignatureCheck Verify(string? address, string message, string? signature, string network)
    {
        var status = KaspaBech32.Decode(address?.Trim().ToLowerInvariant(), out var info);
        if (status != KaspaBech32Status.Ok)
            return SignatureCheck.Fail("地址格式无效 / invalid Kaspa address");
        if (!string.Equals(info.Prefix, AddressPrefix(network), StringComparison.Ordinal))
            return SignatureCheck.Fail($"地址网络不匹配，需要 {AddressPrefix(network)}: 前缀 / wrong network");
        if (info.Version != KaspaBech32.PubKeyVersion)
            return SignatureCheck.Fail("仅支持 Schnorr 地址（kaspa:q…）签名验证 / only Schnorr P2PK addresses are supported");
        byte[]? sig = DecodeSignature(signature);
        if (sig is null)
            return SignatureCheck.Fail("签名格式无效（需 64 字节 Schnorr 签名，hex 或 base64） / invalid signature encoding");
        return VerifyDigest(info.Payload, PersonalMessageHash(message), sig)
            ? SignatureCheck.Ok()
            : SignatureCheck.Fail("签名验证失败 / signature does not match address");
    }

    /// <summary>Signs like rusty-kaspa sign_message (used by tests and tooling).</summary>
    public static byte[] Sign(string message, ReadOnlySpan<byte> privateKey32, byte[]? auxRand32 = null)
    {
        if (!ECPrivKey.TryCreate(privateKey32, Context.Instance, out var key) || key is null)
            throw new ArgumentException("Invalid private key.");
        byte[] digest = PersonalMessageHash(message);
        var sig = auxRand32 is { Length: 32 } ? key.SignBIP340(digest, new BIP340NonceFunction(auxRand32)) : key.SignBIP340(digest);
        byte[] output = new byte[64];
        sig.WriteToSpan(output);
        return output;
    }

    public static byte[] XOnlyPublicKey(ReadOnlySpan<byte> privateKey32)
    {
        if (!ECPrivKey.TryCreate(privateKey32, Context.Instance, out var key) || key is null)
            throw new ArgumentException("Invalid private key.");
        byte[] x = new byte[32];
        key.CreateXOnlyPubKey().WriteToSpan(x);
        return x;
    }

    public static string AddressFromXOnly(ReadOnlySpan<byte> xOnly, string network) =>
        KaspaBech32.Encode(AddressPrefix(network), KaspaBech32.PubKeyVersion, xOnly);
}

public sealed record SignatureCheck(bool Valid, string? Error)
{
    public static SignatureCheck Ok() => new(true, null);
    public static SignatureCheck Fail(string error) => new(false, error);
}
