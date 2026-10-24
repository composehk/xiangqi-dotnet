// Ported from https://github.com/composehk/kaswallet (dotnet/src/KasWallet.Core).
using System.Buffers.Binary;
using System.Numerics;

namespace Xiangqi.Server.Kaspa;

/// <summary>
/// RFC 7693 BLAKE2b, including keyed mode. The key is padded to one 128-byte block.
/// </summary>
public static class Blake2b
{
    public const int BlockSize = 128;
    public const int MaxHashSize = 64;
    public const int MaxKeySize = 64;

    private static readonly ulong[] Iv =
    {
        0x6A09E667F3BCC908UL,
        0xBB67AE8584CAA73BUL,
        0x3C6EF372FE94F82BUL,
        0xA54FF53A5F1D36F1UL,
        0x510E527FADE682D1UL,
        0x9B05688C2B3E6C1FUL,
        0x1F83D9ABFB41BD6BUL,
        0x5BE0CD19137E2179UL,
    };

    // BLAKE2 message schedule. Rounds 10 and 11 repeat rounds 0 and 1.
    private static readonly byte[] Sigma =
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
        11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4,
        7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8,
        9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13,
        2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9,
        12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11,
        13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10,
        6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5,
        10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
        14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3,
    };

    public static byte[] Hash(ReadOnlySpan<byte> data, int hashSize, ReadOnlySpan<byte> key = default)
    {
        var hasher = new Hasher(hashSize, key);
        hasher.Update(data);
        return hasher.Finish();
    }

    public static byte[] Hash256(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key = default) => Hash(data, 32, key);

    private sealed class Hasher
    {
        private readonly int _hashSize;
        private readonly ulong[] _h = new ulong[8];
        private readonly byte[] _buffer = new byte[BlockSize];
        private int _bufferLen;
        private ulong _t0;
        private ulong _t1;
        private bool _finished;

        public Hasher(int hashSize, ReadOnlySpan<byte> key)
        {
            if (hashSize is < 1 or > MaxHashSize)
                throw new ArgumentOutOfRangeException(nameof(hashSize));
            if (key.Length > MaxKeySize)
                throw new ArgumentException("BLAKE2b key must be at most 64 bytes.", nameof(key));

            _hashSize = hashSize;
            Iv.AsSpan().CopyTo(_h);
            // fanout = 1, depth = 1. Sequential mode leaves the rest of the parameter block zero.
            _h[0] ^= 0x01010000UL ^ ((ulong)key.Length << 8) ^ (ulong)hashSize;

            if (!key.IsEmpty)
            {
                key.CopyTo(_buffer);
                _bufferLen = BlockSize;
            }
        }

        public void Update(ReadOnlySpan<byte> data)
        {
            if (_finished)
                throw new InvalidOperationException("Hasher is already finished.");
            if (data.IsEmpty)
                return;

            if (_bufferLen > 0)
            {
                var fill = BlockSize - _bufferLen;
                if (data.Length > fill)
                {
                    data[..fill].CopyTo(_buffer.AsSpan(_bufferLen));
                    _bufferLen = 0;
                    IncrementCounter(BlockSize);
                    Compress(_buffer, last: false);
                    data = data[fill..];
                }
                else
                {
                    data.CopyTo(_buffer.AsSpan(_bufferLen));
                    _bufferLen += data.Length;
                    return;
                }
            }

            while (data.Length > BlockSize)
            {
                IncrementCounter(BlockSize);
                Compress(data[..BlockSize], last: false);
                data = data[BlockSize..];
            }

            if (!data.IsEmpty)
            {
                data.CopyTo(_buffer);
                _bufferLen = data.Length;
            }
        }

        public byte[] Finish()
        {
            if (_finished)
                throw new InvalidOperationException("Hasher is already finished.");
            _finished = true;

            IncrementCounter((ulong)_bufferLen);
            _buffer.AsSpan(_bufferLen).Clear();
            Compress(_buffer, last: true);

            var output = new byte[_hashSize];
            for (var i = 0; i < _hashSize; i++)
                output[i] = (byte)(_h[i >> 3] >> (8 * (i & 7)));
            return output;
        }

        private void IncrementCounter(ulong inc)
        {
            var sum = _t0 + inc;
            if (sum < _t0)
                _t1++;
            _t0 = sum;
        }

        private void Compress(ReadOnlySpan<byte> block, bool last)
        {
            Span<ulong> m = stackalloc ulong[16];
            Span<ulong> v = stackalloc ulong[16];
            for (var i = 0; i < 16; i++)
                m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(i * 8, 8));

            for (var i = 0; i < 8; i++)
            {
                v[i] = _h[i];
                v[i + 8] = Iv[i];
            }

            v[12] ^= _t0;
            v[13] ^= _t1;
            if (last)
                v[14] ^= ulong.MaxValue;

            for (var round = 0; round < 12; round++)
            {
                var s = round * 16;
                Mix(v, 0, 4, 8, 12, m[Sigma[s + 0]], m[Sigma[s + 1]]);
                Mix(v, 1, 5, 9, 13, m[Sigma[s + 2]], m[Sigma[s + 3]]);
                Mix(v, 2, 6, 10, 14, m[Sigma[s + 4]], m[Sigma[s + 5]]);
                Mix(v, 3, 7, 11, 15, m[Sigma[s + 6]], m[Sigma[s + 7]]);
                Mix(v, 0, 5, 10, 15, m[Sigma[s + 8]], m[Sigma[s + 9]]);
                Mix(v, 1, 6, 11, 12, m[Sigma[s + 10]], m[Sigma[s + 11]]);
                Mix(v, 2, 7, 8, 13, m[Sigma[s + 12]], m[Sigma[s + 13]]);
                Mix(v, 3, 4, 9, 14, m[Sigma[s + 14]], m[Sigma[s + 15]]);
            }

            for (var i = 0; i < 8; i++)
                _h[i] ^= v[i] ^ v[i + 8];
        }

        private static void Mix(Span<ulong> v, int a, int b, int c, int d, ulong x, ulong y)
        {
            unchecked
            {
                v[a] = v[a] + v[b] + x;
                v[d] = BitOperations.RotateRight(v[d] ^ v[a], 32);
                v[c] += v[d];
                v[b] = BitOperations.RotateRight(v[b] ^ v[c], 24);
                v[a] = v[a] + v[b] + y;
                v[d] = BitOperations.RotateRight(v[d] ^ v[a], 16);
                v[c] += v[d];
                v[b] = BitOperations.RotateRight(v[b] ^ v[c], 63);
            }
        }
    }
}
