using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Smolmesh.Crypto;

// Ascon-AEAD128, Ascon-Hash256 and Ascon-XOF128 from NIST SP 800-232, plus Multi (a hash of
// several inputs) and the AEAD's duplex steps for AsconStream.
// https://doi.org/10.6028/NIST.SP.800-232
//
// Unlike Gimli the state is five 64-bit words rather than a byte array: every Ascon operation is
// a whole-word XOR, AND-NOT or rotate, so the permutation runs entirely in registers. The bitsliced
// S-box already processes all 64 columns at once, so SIMD has nothing left to parallelize.
internal struct Ascon
{
    internal const int KeySize = 16;
    internal const int NonceSize = 16;
    internal const int TagSize = 16;
    internal const int HashSize = 32;

    private const ulong AeadIV = 0x00001000808c0001UL;
    private const int AeadRate = 16;
    private const int HashRate = 8;

    internal ulong X0, X1, X2, X3, X4;

    private Ascon(ulong x0, ulong x1, ulong x2, ulong x3, ulong x4) => (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);

    // The hash and XOF start from a fixed IV with a zero state, so the first permutation is a constant.
    private static Ascon HashStart => new(0x9b1e5494e934d681UL, 0x4bc3a01e333751d2UL, 0xae65396c6b34b81aUL,
        0x3c7fd4a4d56a4db3UL, 0x1a5c464906c5976dUL);
    private static Ascon XofStart => new(0xda82ce768d9447ebUL, 0xcc7ce6c75f1ef969UL, 0xe7508fd780085631UL,
        0x0ee0ea53416b58ccUL, 0xe0547524db6f0bdeUL);

    // Permutation.
    //
    // Never inlined, like the block loops: when PGO inlines a 1.4 KB permutation or a whole loop
    // into its caller, the result runs up to 2.4x slower. Loops over many blocks copy the state
    // into locals and inline the rounds themselves. The rounds take and return the state by value,
    // which schedules slightly better than ref parameters.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Permute12()
    {
        ulong x0 = X0, x1 = X1, x2 = X2, x3 = X3, x4 = X4;
        (x0, x1, x2, x3, x4) = Rounds12(x0, x1, x2, x3, x4);
        (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void Permute8()
    {
        ulong x0 = X0, x1 = X1, x2 = X2, x3 = X3, x4 = X4;
        (x0, x1, x2, x3, x4) = Rounds8(x0, x1, x2, x3, x4);
        (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (ulong, ulong, ulong, ulong, ulong) Rounds12(ulong x0, ulong x1, ulong x2, ulong x3, ulong x4)
    {
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0xf0);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0xe1);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0xd2);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0xc3);
        return Rounds8(x0, x1, x2, x3, x4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (ulong, ulong, ulong, ulong, ulong) Rounds8(ulong x0, ulong x1, ulong x2, ulong x3, ulong x4)
    {
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0xb4);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0xa5);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0x96);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0x87);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0x78);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0x69);
        (x0, x1, x2, x3, x4) = Round(x0, x1, x2, x3, x4, 0x5a);
        return Round(x0, x1, x2, x3, x4, 0x4b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (ulong, ulong, ulong, ulong, ulong) Round(ulong x0, ulong x1, ulong x2, ulong x3, ulong x4, ulong c)
    {
        // Constant addition, then the S-box as a Keccak chi wrapped in linear XORs.
        x2 ^= c;
        x0 ^= x4;
        x4 ^= x3;
        x2 ^= x1;
        ulong t0 = x0 ^ (~x1 & x2);
        ulong t1 = x1 ^ (~x2 & x3);
        ulong t2 = x2 ^ (~x3 & x4);
        ulong t3 = x3 ^ (~x4 & x0);
        ulong t4 = x4 ^ (~x0 & x1);
        t1 ^= t0;
        t0 ^= t4;
        t3 ^= t2;
        // Linear diffusion; the S-box's final NOT of word 2 commutes with it and is applied last.
        x0 = t0 ^ BitOperations.RotateRight(t0, 19) ^ BitOperations.RotateRight(t0, 28);
        x1 = t1 ^ BitOperations.RotateRight(t1, 61) ^ BitOperations.RotateRight(t1, 39);
        x2 = ~(t2 ^ BitOperations.RotateRight(t2, 1) ^ BitOperations.RotateRight(t2, 6));
        x3 = t3 ^ BitOperations.RotateRight(t3, 10) ^ BitOperations.RotateRight(t3, 17);
        x4 = t4 ^ BitOperations.RotateRight(t4, 7) ^ BitOperations.RotateRight(t4, 41);
        return (x0, x1, x2, x3, x4);
    }

    // Hashing: 8 bytes per 12-round permutation.

    internal static byte[] Hash256(ReadOnlySpan<byte> input)
    {
        byte[] output = new byte[HashSize];
        Hash256(input, output);
        return output;
    }

    internal static void Hash256(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (output.Length != HashSize) throw new ArgumentException("Ascon-Hash256 output must be 32 bytes.");
        var state = HashStart;
        int filled = 0;
        state.AbsorbHash(input, ref filled);
        state.FinishHash(filled);
        state.Squeeze(output);
    }

    internal static void Xof128(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var state = XofStart;
        int filled = 0;
        state.AbsorbHash(input, ref filled);
        state.FinishHash(filled);
        state.Squeeze(output);
    }

    // Like Gimli.Multi, an unambiguous hash of several inputs for key derivation and keyed hashing
    // (a secret first input makes it a PRF). This is Ascon-XOF128 over each input's 8-byte
    // little-endian length followed by its bytes, absorbed in place without concatenating.
    internal static byte[] Multi(params ReadOnlySpan<byte[]> inputs)
    {
        byte[] output = new byte[16];
        Multi(inputs, output);
        return output;
    }

    internal static void Multi(ReadOnlySpan<byte[]> inputs, Span<byte> output)
    {
        var state = XofStart;
        int filled = 0;
        Span<byte> length = stackalloc byte[8];
        foreach (byte[] input in inputs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)input.Length);
            state.AbsorbHash(length, ref filled);
            state.AbsorbHash(input, ref filled);
        }
        state.FinishHash(filled);
        state.Squeeze(output);
    }

    // Absorbs more message into the rate, which already holds `filled` (0 to 7) bytes of a word.
    private void AbsorbHash(ReadOnlySpan<byte> input, ref int filled)
    {
        if (filled > 0)
        {
            int take = Math.Min(HashRate - filled, input.Length);
            X0 ^= Load(input[..take]) << (8 * filled);
            filled += take;
            input = input[take..];
            if (filled < HashRate) return;
            Permute12();
        }
        input = input[AbsorbWords(input)..];
        X0 ^= Load(input);
        filled = input.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int AbsorbWords(ReadOnlySpan<byte> input)
    {
        ulong x0 = X0, x1 = X1, x2 = X2, x3 = X3, x4 = X4;
        int offset = 0;
        for (; input.Length - offset >= HashRate; offset += HashRate)
        {
            x0 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[offset..]);
            (x0, x1, x2, x3, x4) = Rounds12(x0, x1, x2, x3, x4);
        }
        (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);
        return offset;
    }

    private void FinishHash(int filled)
    {
        X0 ^= Pad(filled);
        Permute12();
    }

    // The final permutation is part of absorbing, so each 8 bytes but the first costs one.
    private void Squeeze(Span<byte> output)
    {
        while (true)
        {
            if (output.Length <= HashRate)
            {
                Span<byte> last = stackalloc byte[HashRate];
                BinaryPrimitives.WriteUInt64LittleEndian(last, X0);
                last[..output.Length].CopyTo(output);
                return;
            }
            BinaryPrimitives.WriteUInt64LittleEndian(output, X0);
            output = output[HashRate..];
            Permute12();
        }
    }

    // AEAD: 16 bytes per 8-round permutation, 12 rounds to start and to make the tag.

    // Writes ciphertext (same length as plaintext; may be the same buffer) and a 16-byte tag.
    internal static void Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag)
    {
        if (ciphertext.Length != plaintext.Length) throw new ArgumentException("Ciphertext must match plaintext length.");
        if (tag.Length != TagSize) throw new ArgumentException("Ascon-AEAD128 tag must be 16 bytes.");
        var state = Start(key, nonce, associatedData, out ulong k0, out ulong k1);
        state.DuplexEncrypt(plaintext, ciphertext);
        state.Finish(k0, k1, tag);
    }

    // Writes plaintext (same length as ciphertext; may be the same buffer) and returns whether the
    // tag is valid. On failure the plaintext is cleared, but it must not be used either way.
    internal static bool Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData,
        ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext)
    {
        if (plaintext.Length != ciphertext.Length) throw new ArgumentException("Plaintext must match ciphertext length.");
        if (tag.Length != TagSize) throw new ArgumentException("Ascon-AEAD128 tag must be 16 bytes.");
        var state = Start(key, nonce, associatedData, out ulong k0, out ulong k1);
        state.DuplexDecrypt(ciphertext, plaintext);
        Span<byte> expected = stackalloc byte[TagSize];
        state.Finish(k0, k1, expected);
        if (CryptographicOperations.FixedTimeEquals(expected, tag)) return true;
        CryptographicOperations.ZeroMemory(plaintext);
        return false;
    }

    private static Ascon Start(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> associatedData,
        out ulong k0, out ulong k1)
    {
        if (key.Length != KeySize) throw new ArgumentException("Ascon-AEAD128 key must be 16 bytes.");
        if (nonce.Length != NonceSize) throw new ArgumentException("Ascon-AEAD128 nonce must be 16 bytes.");
        k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
        k1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..]);
        var state = new Ascon(AeadIV, k0, k1, BinaryPrimitives.ReadUInt64LittleEndian(nonce),
            BinaryPrimitives.ReadUInt64LittleEndian(nonce[8..]));
        state.Permute12();
        state.X3 ^= k0;
        state.X4 ^= k1;

        if (!associatedData.IsEmpty)
        {
            ReadOnlySpan<byte> tail = associatedData[state.AbsorbBlocks(associatedData)..];
            state.X0 ^= Partial(tail, 0);
            state.X1 ^= Partial(tail, 8);
            state.Permute8();
        }
        // Domain separation between associated data and the message.
        state.X4 ^= 1UL << 63;
        return state;
    }

    // The tag comes from the capacity, which is fine only because the state is discarded after it.
    private void Finish(ulong k0, ulong k1, Span<byte> tag)
    {
        X2 ^= k0;
        X3 ^= k1;
        Permute12();
        BinaryPrimitives.WriteUInt64LittleEndian(tag, X3 ^ k0);
        BinaryPrimitives.WriteUInt64LittleEndian(tag[8..], X4 ^ k1);
    }

    // Duplex steps.

    // The message phase of the AEAD duplex: encrypts every whole block with an 8-round permutation
    // after each, then the padded final block (0 to 15 bytes) without permuting. Exposed for
    // constructions that keep one duplex running, which must permute before reusing the rate.
    internal void DuplexEncrypt(ReadOnlySpan<byte> plaintext, Span<byte> ciphertext)
    {
        int full = EncryptBlocks(plaintext, ciphertext);
        ReadOnlySpan<byte> tail = plaintext[full..];
        X0 ^= Partial(tail, 0);
        X1 ^= Partial(tail, 8);
        Span<byte> block = stackalloc byte[AeadRate];
        BinaryPrimitives.WriteUInt64LittleEndian(block, X0);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], X1);
        block[..tail.Length].CopyTo(ciphertext[full..]);
    }

    internal void DuplexDecrypt(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext)
    {
        int full = DecryptBlocks(ciphertext, plaintext);
        // Absorbing the zero-padded plaintext leaves the ciphertext in its bytes and the state elsewhere.
        ReadOnlySpan<byte> tail = ciphertext[full..];
        int n = tail.Length;
        ulong p0 = (Partial(tail, 0) ^ X0) & Mask(n);
        ulong p1 = (Partial(tail, 8) ^ X1) & Mask(n - 8);
        Span<byte> block = stackalloc byte[AeadRate];
        BinaryPrimitives.WriteUInt64LittleEndian(block, p0);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], p1);
        block[..n].CopyTo(plaintext[full..]);
        X0 ^= p0 ^ Pad(n);
        X1 ^= p1 ^ Pad(n - 8);
    }

    // The three block loops below each handle every whole 16-byte block and return its byte count.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int AbsorbBlocks(ReadOnlySpan<byte> input)
    {
        ulong x0 = X0, x1 = X1, x2 = X2, x3 = X3, x4 = X4;
        int offset = 0;
        for (; input.Length - offset >= AeadRate; offset += AeadRate)
        {
            x0 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[offset..]);
            x1 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[(offset + 8)..]);
            (x0, x1, x2, x3, x4) = Rounds8(x0, x1, x2, x3, x4);
        }
        (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);
        return offset;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int EncryptBlocks(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ulong x0 = X0, x1 = X1, x2 = X2, x3 = X3, x4 = X4;
        int offset = 0;
        for (; input.Length - offset >= AeadRate; offset += AeadRate)
        {
            x0 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[offset..]);
            x1 ^= BinaryPrimitives.ReadUInt64LittleEndian(input[(offset + 8)..]);
            BinaryPrimitives.WriteUInt64LittleEndian(output[offset..], x0);
            BinaryPrimitives.WriteUInt64LittleEndian(output[(offset + 8)..], x1);
            (x0, x1, x2, x3, x4) = Rounds8(x0, x1, x2, x3, x4);
        }
        (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);
        return offset;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int DecryptBlocks(ReadOnlySpan<byte> input, Span<byte> output)
    {
        ulong x0 = X0, x1 = X1, x2 = X2, x3 = X3, x4 = X4;
        int offset = 0;
        for (; input.Length - offset >= AeadRate; offset += AeadRate)
        {
            ulong c0 = BinaryPrimitives.ReadUInt64LittleEndian(input[offset..]);
            ulong c1 = BinaryPrimitives.ReadUInt64LittleEndian(input[(offset + 8)..]);
            BinaryPrimitives.WriteUInt64LittleEndian(output[offset..], x0 ^ c0);
            BinaryPrimitives.WriteUInt64LittleEndian(output[(offset + 8)..], x1 ^ c1);
            x0 = c0;
            x1 = c1;
            (x0, x1, x2, x3, x4) = Rounds8(x0, x1, x2, x3, x4);
        }
        (X0, X1, X2, X3, X4) = (x0, x1, x2, x3, x4);
        return offset;
    }

    // Helpers for final blocks shorter than the rate.

    // The little-endian word at byte offset `start` of a final block, zero-padded and with the
    // 0x01 padding byte after the data.
    private static ulong Partial(ReadOnlySpan<byte> tail, int start)
    {
        int n = tail.Length - start;
        return n <= 0 ? Pad(n) : Load(tail.Slice(start, Math.Min(n, 8))) | Pad(n);
    }

    // Up to 8 bytes as a zero-padded little-endian word.
    private static ulong Load(ReadOnlySpan<byte> bytes)
    {
        Span<byte> word = stackalloc byte[8];
        word.Clear();
        bytes.CopyTo(word);
        return BinaryPrimitives.ReadUInt64LittleEndian(word);
    }

    // For a word holding n bytes of the final block: its padding bit and its data-byte mask.
    private static ulong Pad(int n) => n is >= 0 and < 8 ? 1UL << (8 * n) : 0;
    private static ulong Mask(int n) => n <= 0 ? 0 : n >= 8 ? ulong.MaxValue : (1UL << (8 * n)) - 1;
}
