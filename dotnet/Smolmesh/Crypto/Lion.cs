namespace Smolmesh.Crypto;

// LION (Anderson and Biham, "Two Practical and Provably Secure Block Ciphers: BEAR and LION"),
// a block cipher whose block is a whole packet, built from Ascon:
//   S(k, n): the Ascon-AEAD128 keystream for key k and nonce n
//   H(k, R): the Ascon-AEAD128 tag of an empty message with R as associated data, a PRF of R
// Block = L (16 bytes) || R (the rest). Key = K1 || K2 || K3, 16 bytes each.
//   R ^= S(K1, L); L ^= H(K2, R); R ^= S(K3, L)
// R cannot be empty, so the smallest block is 17 bytes. The paper keys the stream cipher with
// L ^ K. Ascon takes a nonce, so L goes there instead and the key stays fixed.
//
// The output is as long as the input and every bit of it depends on every bit of the input: there
// is no nonce and no tag. Equal blocks encrypt alike, so the caller's blocks must not repeat, and a
// modified ciphertext decrypts to a block that is random throughout, so the caller must be able to
// tell such a block from a real one. Three rounds stand up to chosen plaintexts or to chosen
// ciphertexts, not to both mixed; that takes LIONESS and a fourth round. Not audited.
internal static class Lion
{
    internal const int KeySize = 48;
    private const int Half = 16;
    internal const int MinSize = Half + 1;
    private static readonly byte[] ZeroNonce = new byte[Ascon.NonceSize];

    internal static void Encrypt(ReadOnlySpan<byte> key, Span<byte> block)
    {
        Check(key, block);
        Span<byte> left = block[..Half], right = block[Half..];
        Ascon.XorKeystream(key[..16], left, right);
        Hash(key.Slice(16, 16), right, left);
        Ascon.XorKeystream(key.Slice(32, 16), left, right);
    }

    internal static void Decrypt(ReadOnlySpan<byte> key, Span<byte> block)
    {
        Check(key, block);
        Span<byte> left = block[..Half], right = block[Half..];
        Ascon.XorKeystream(key.Slice(32, 16), left, right);
        Hash(key.Slice(16, 16), right, left);
        Ascon.XorKeystream(key[..16], left, right);
    }

    private static void Check(ReadOnlySpan<byte> key, Span<byte> block)
    {
        if (key.Length != KeySize) throw new ArgumentException("LION key must be 48 bytes.");
        if (block.Length < MinSize) throw new ArgumentException("LION block must be at least 17 bytes.");
    }

    // left ^= H(k, right)
    private static void Hash(ReadOnlySpan<byte> k, ReadOnlySpan<byte> right, Span<byte> left)
    {
        Span<byte> digest = stackalloc byte[Ascon.TagSize];
        Ascon.Encrypt(k, ZeroNonce, right, default, default, digest);
        for (int i = 0; i < left.Length; i++) left[i] ^= digest[i];
    }
}
