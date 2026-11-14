using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Smolmesh.Crypto;

// One direction of a connection as a single Ascon duplex that is never reset, so each message's
// tag authenticates the whole stream so far and replayed, reordered or dropped messages fail.
// Per message: encrypt it like an Ascon-AEAD128 message (16 bytes per 8 rounds, padded final
// block), key the capacity, 12 rounds, tag = first 8 bytes of the rate, then 8 more rounds.
// Only the rate is ever output, and the last permutation keeps the next keystream from being the
// published tag, so the capacity stays hidden. The key must be unique per connection and
// direction. After a failed Open the stream is unusable: its state has absorbed the forgery.
internal sealed class AsconStream
{
    internal const int TagSize = 8;

    // Ascon-AEAD128's IV with a different last byte, so the stream never starts from a state a
    // standard AEAD under the same key could reach.
    private const ulong StreamIV = 0x00001000808c00ffUL;

    private Ascon state;
    private readonly ulong k0, k1;
    private bool failed;

    internal AsconStream(ReadOnlySpan<byte> key)
    {
        if (key.Length != Ascon.KeySize) throw new ArgumentException("AsconStream key must be 16 bytes.");
        k0 = BinaryPrimitives.ReadUInt64LittleEndian(key);
        k1 = BinaryPrimitives.ReadUInt64LittleEndian(key[8..]);
        state = new Ascon { X0 = StreamIV, X1 = k0, X2 = k1 };
        state.Permute12();
        state.X3 ^= k0;
        state.X4 ^= k1;
    }

    // The keystream for the next message's first output.Length (up to 16) bytes, without
    // advancing, so a length prefix can be decrypted before the rest of the message arrives.
    internal void PeekKeystream(Span<byte> output)
    {
        Span<byte> rate = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(rate, state.X0);
        BinaryPrimitives.WriteUInt64LittleEndian(rate[8..], state.X1);
        rate[..output.Length].CopyTo(output);
    }

    // Encrypts message in place and writes its tag.
    internal void Seal(Span<byte> message, Span<byte> tag)
    {
        if (failed) throw new InvalidOperationException("AsconStream failed authentication.");
        state.DuplexEncrypt(message, message);
        Tag(tag);
    }

    // Decrypts message in place and returns whether the tag is valid. On failure the message is
    // cleared and the stream can't be used again.
    internal bool Open(Span<byte> message, ReadOnlySpan<byte> tag)
    {
        if (failed) throw new InvalidOperationException("AsconStream failed authentication.");
        state.DuplexDecrypt(message, message);
        Span<byte> expected = stackalloc byte[TagSize];
        Tag(expected);
        if (CryptographicOperations.FixedTimeEquals(expected, tag)) return true;
        failed = true;
        CryptographicOperations.ZeroMemory(message);
        return false;
    }

    // Keying the capacity before the tag permutation means that even a recovered state can't
    // produce tags without the key.
    private void Tag(Span<byte> tag)
    {
        if (tag.Length != TagSize) throw new ArgumentException("AsconStream tag must be 8 bytes.");
        state.X2 ^= k0;
        state.X3 ^= k1;
        state.Permute12();
        BinaryPrimitives.WriteUInt64LittleEndian(tag, state.X0);
        state.Permute8();
    }
}
