using System.Buffers.Binary;
using System.Numerics;

namespace Smolmesh.Benchmarks;

// Benchmark-only scalar transcription of Ascon-AEAD128, NIST SP 800-232.
// https://doi.org/10.6028/NIST.SP.800-232
// Encryption only; no unsafe code, intrinsics, native calls, or heap allocations.
// This is not an audited production implementation.
internal struct AsconSpec
{
    private ulong s0, s1, s2, s3, s4;

    internal static AsconSpec Start(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> associatedData = default)
    {
        if (key.Length != 16 || nonce.Length != 16)
            throw new ArgumentException("Ascon-AEAD128 requires a 16-byte key and nonce.");
        ulong k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
        ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..]);
        var state = new AsconSpec
        {
            s0 = 0x00001000808c0001UL,
            s1 = k0,
            s2 = k1,
            s3 = BinaryPrimitives.ReadUInt64LittleEndian(nonce),
            s4 = BinaryPrimitives.ReadUInt64LittleEndian(nonce[8..])
        };
        state.Permute(12);
        state.s3 ^= k0;
        state.s4 ^= k1;

        if (!associatedData.IsEmpty)
        {
            while (associatedData.Length >= 16)
            {
                state.s0 ^= BinaryPrimitives.ReadUInt64LittleEndian(associatedData);
                state.s1 ^= BinaryPrimitives.ReadUInt64LittleEndian(associatedData[8..]);
                state.Permute(8);
                associatedData = associatedData[16..];
            }
            for (int i = 0; i < associatedData.Length; i++)
                state.AbsorbByte(i, associatedData[i]);
            state.Pad(associatedData.Length);
            state.Permute(8);
        }
        state.s4 ^= 1UL << 63;
        return state;
    }

    internal static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext, Span<byte> output, ReadOnlySpan<byte> associatedData = default)
    {
        if (output.Length < plaintext.Length + 16)
            throw new ArgumentException("Output must fit ciphertext and a 16-byte tag.");
        var state = Start(key, nonce, associatedData);
        int full = plaintext.Length / 16 * 16;
        state.EncryptBlocks(plaintext[..full], output[..full]);
        ReadOnlySpan<byte> tail = plaintext[full..];
        for (int i = 0; i < tail.Length; i++)
        {
            state.AbsorbByte(i, tail[i]);
            output[full + i] = (byte)(i < 8 ? state.s0 >> (8 * i) : state.s1 >> (8 * (i - 8)));
        }
        state.Pad(tail.Length);

        ulong k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
        ulong k1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..]);
        state.s2 ^= k0;
        state.s3 ^= k1;
        state.Permute(12);
        state.s3 ^= k0;
        state.s4 ^= k1;
        BinaryPrimitives.WriteUInt64LittleEndian(output[plaintext.Length..], state.s3);
        BinaryPrimitives.WriteUInt64LittleEndian(output[(plaintext.Length + 8)..], state.s4);
    }

    // Exposed only to measure a persistent block loop; not authenticated records.
    internal void EncryptBlocks(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length % 16 != 0 || output.Length < input.Length)
            throw new ArgumentException("Expected whole 16-byte blocks and sufficient output.");
        for (int offset = 0; offset < input.Length; offset += 16)
        {
            s0 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[offset..]);
            s1 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[(offset + 8)..]);
            BinaryPrimitives.WriteUInt64LittleEndian(output[offset..], s0);
            BinaryPrimitives.WriteUInt64LittleEndian(output[(offset + 8)..], s1);
            Permute(8);
        }
    }

    private void AbsorbByte(int offset, byte value)
    {
        if (offset < 8) s0 ^= (ulong)value << (8 * offset);
        else s1 ^= (ulong)value << (8 * (offset - 8));
    }

    private void Pad(int offset) => AbsorbByte(offset, 1);

    private void Permute(int rounds)
    {
        ulong x0 = s0, x1 = s1, x2 = s2, x3 = s3, x4 = s4;
        for (int i = 12 - rounds; i < 12; i++)
        {
            x2 ^= (ulong)(((15 - i) << 4) | i);

            x0 ^= x4;
            x4 ^= x3;
            x2 ^= x1;
            ulong t0 = ~x0 & x1, t1 = ~x1 & x2, t2 = ~x2 & x3,
                t3 = ~x3 & x4, t4 = ~x4 & x0;
            x0 ^= t1;
            x1 ^= t2;
            x2 ^= t3;
            x3 ^= t4;
            x4 ^= t0;
            x1 ^= x0;
            x0 ^= x4;
            x3 ^= x2;
            x2 = ~x2;

            x0 ^= BitOperations.RotateRight(x0, 19) ^ BitOperations.RotateRight(x0, 28);
            x1 ^= BitOperations.RotateRight(x1, 61) ^ BitOperations.RotateRight(x1, 39);
            x2 ^= BitOperations.RotateRight(x2, 1) ^ BitOperations.RotateRight(x2, 6);
            x3 ^= BitOperations.RotateRight(x3, 10) ^ BitOperations.RotateRight(x3, 17);
            x4 ^= BitOperations.RotateRight(x4, 7) ^ BitOperations.RotateRight(x4, 41);
        }
        s0 = x0;
        s1 = x1;
        s2 = x2;
        s3 = x3;
        s4 = x4;
    }
}
