// Ported from https://github.com/composehk/kaswallet (dotnet/src/KasWallet.Core).
using System.Buffers.Binary;

namespace Xiangqi.Server.Kaspa;

/// <summary>
/// Kaspa address payload encoding. This is the cashaddr-style bech32 used by
/// rusty-kaspa (8-character / 40-bit polymod checksum, ':' separator), not BIP-173.
/// </summary>
public static class KaspaBech32
{
    public const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    public const byte PubKeyVersion = 0;
    public const byte PubKeyEcdsaVersion = 1;
    public const byte ScriptHashVersion = 8;

    public static string Encode(string prefix, byte version, ReadOnlySpan<byte> payload)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Contains(':'))
            throw new ArgumentException("Invalid address prefix.", nameof(prefix));

        var raw = new byte[payload.Length + 1];
        raw[0] = version;
        payload.CopyTo(raw.AsSpan(1));

        var data = Convert8To5(raw);
        var checksum = Convert8To5(ChecksumBytes(Checksum(prefix, data)));
        var chars = new char[data.Length + checksum.Length];
        for (var i = 0; i < data.Length; i++)
            chars[i] = Charset[data[i]];
        for (var i = 0; i < checksum.Length; i++)
            chars[data.Length + i] = Charset[checksum[i]];
        return prefix + ":" + new string(chars);
    }

    public static KaspaBech32Status Decode(string? address, out KaspaAddressInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(address))
            return KaspaBech32Status.Malformed;

        address = address.Trim();
        var colon = address.IndexOf(':');
        if (colon <= 0 || colon != address.LastIndexOf(':') || colon == address.Length - 1)
            return KaspaBech32Status.Malformed;

        var prefix = address[..colon];
        var data = address[(colon + 1)..];
        if (prefix.Length > 83 || data.Length is < 8 or > 150)
            return KaspaBech32Status.Malformed;

        foreach (var c in prefix)
        {
            if (c is < (char)33 or > (char)126)
                return KaspaBech32Status.Malformed;
        }

        var values = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            var index = Charset.IndexOf(data[i]);
            if (index < 0)
                return KaspaBech32Status.Malformed;
            values[i] = (byte)index;
        }

        var payload5 = values.AsSpan(0, values.Length - 8);
        var checksum5 = values.AsSpan(values.Length - 8);
        var found = BinaryPrimitives.ReadUInt64BigEndian(WidenChecksum(Convert5To8(checksum5)));
        if (found != Checksum(prefix, payload5))
            return KaspaBech32Status.BadChecksum;

        var payload8 = Convert5To8(payload5);
        if (payload8.Length == 0)
            return KaspaBech32Status.BadPayload;

        var version = payload8[0];
        var body = payload8.AsSpan(1).ToArray();
        info = new KaspaAddressInfo(prefix, version, body);
        return IsStandardPayload(version, body.Length)
            ? KaspaBech32Status.Ok
            : KaspaBech32Status.BadPayload;
    }

    public static bool IsStandardPayload(byte version, int payloadLength) => version switch
    {
        PubKeyVersion => payloadLength == 32,
        PubKeyEcdsaVersion => payloadLength == 33,
        ScriptHashVersion => payloadLength == 32,
        _ => false
    };

    private static ulong Checksum(string prefix, ReadOnlySpan<byte> payload5)
    {
        var values = new byte[prefix.Length + 1 + payload5.Length + 8];
        var n = 0;
        foreach (var c in prefix)
            values[n++] = (byte)(c & 0x1f);
        values[n++] = 0;
        payload5.CopyTo(values.AsSpan(n));
        n += payload5.Length;
        return Polymod(values.AsSpan(0, n + 8));
    }

    private static ulong Polymod(ReadOnlySpan<byte> values)
    {
        ulong c = 1;
        foreach (var d in values)
        {
            var c0 = c >> 35;
            c = ((c & 0x07ffffffffUL) << 5) ^ d;
            if ((c0 & 0x01) != 0) c ^= 0x98f2bc8e61UL;
            if ((c0 & 0x02) != 0) c ^= 0x79b76d99e2UL;
            if ((c0 & 0x04) != 0) c ^= 0xf33e5fb3c4UL;
            if ((c0 & 0x08) != 0) c ^= 0xae2eabe2a8UL;
            if ((c0 & 0x10) != 0) c ^= 0x1e4f43e470UL;
        }

        return c ^ 1;
    }

    private static byte[] ChecksumBytes(ulong checksum)
    {
        Span<byte> wide = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(wide, checksum);
        return wide[3..].ToArray();
    }

    private static byte[] WidenChecksum(ReadOnlySpan<byte> fiveBytes)
    {
        var wide = new byte[8];
        if (fiveBytes.Length > 5)
            fiveBytes = fiveBytes[^5..];
        fiveBytes.CopyTo(wide.AsSpan(8 - fiveBytes.Length));
        return wide;
    }

    private static byte[] Convert8To5(ReadOnlySpan<byte> payload)
    {
        var padding = payload.Length % 5 == 0 ? 0 : 1;
        var fiveBit = new byte[payload.Length * 8 / 5 + padding];
        var index = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var value in payload)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                fiveBit[index++] = (byte)((buffer >> bits) & 0x1f);
                buffer &= (1 << bits) - 1;
            }
        }

        if (bits > 0)
            fiveBit[index] = (byte)((buffer << (5 - bits)) & 0x1f);
        return fiveBit;
    }

    private static byte[] Convert5To8(ReadOnlySpan<byte> payload)
    {
        var eightBit = new byte[payload.Length * 5 / 8];
        var index = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var value in payload)
        {
            buffer = (buffer << 5) | (value & 0x1f);
            bits += 5;
            while (bits >= 8)
            {
                bits -= 8;
                eightBit[index++] = (byte)((buffer >> bits) & 0xff);
                buffer &= (1 << bits) - 1;
            }
        }

        return eightBit;
    }
}

public enum KaspaBech32Status
{
    Ok,
    Malformed,
    BadChecksum,
    BadPayload
}

public readonly record struct KaspaAddressInfo(string Prefix, byte Version, byte[] Payload);
