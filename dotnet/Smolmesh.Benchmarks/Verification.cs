using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Smolmesh.Benchmarks;

internal static class Verification
{
    internal static void Run()
    {
        VerifySimple();
        // Check API use, boundaries, tag verification, and the persistent streaming path.
        foreach (int length in new[] { 0, 1, 15, 16, 17, 64, 256, 1500 })
        {
            byte[] input = new byte[length];
            new Random(42).NextBytes(input);
            var parameters = new ParametersWithIV(new KeyParameter(new byte[16]), new byte[16]);
            var cipher = new AsconAead128();
            byte[] encrypted = new byte[length + 16];
            cipher.Init(true, parameters);
            int count = cipher.ProcessBytes(input, encrypted);
            count += cipher.DoFinal(encrypted.AsSpan(count));
            if (count != encrypted.Length) throw new Exception("Incorrect ciphertext length.");

            cipher.Init(false, parameters);
            byte[] decrypted = new byte[length];
            count = cipher.ProcessBytes(encrypted, decrypted);
            count += cipher.DoFinal(decrypted.AsSpan(count));
            if (count != length || !input.AsSpan().SequenceEqual(decrypted))
                throw new Exception("Ascon round trip failed.");

            encrypted[^1] ^= 1;
            cipher.Init(false, parameters);
            count = cipher.ProcessBytes(encrypted, decrypted);
            try
            {
                cipher.DoFinal(decrypted.AsSpan(count));
                throw new Exception("Ascon accepted a modified tag.");
            }
            catch (InvalidCipherTextException) { }
        }

        byte[] blocks = new byte[64];
        new Random(43).NextBytes(blocks);
        var p = new ParametersWithIV(new KeyParameter(new byte[16]), new byte[16]);
        var whole = new AsconAead128();
        var split = new AsconAead128();
        whole.Init(true, p);
        split.Init(true, p);
        byte[] expected = new byte[64];
        byte[] actual = new byte[64];
        if (whole.ProcessBytes(blocks, expected) != 64)
            throw new Exception("Ascon buffered a full block unexpectedly.");
        for (int i = 0; i < 64; i += 16)
        {
            if (split.ProcessBytes(blocks.AsSpan(i, 16), actual.AsSpan(i)) != 16)
                throw new Exception("Ascon buffered a full block unexpectedly.");
        }
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new Exception("Ascon streaming comparison failed.");
        Console.WriteLine("Ascon API checks passed (round trip, modified tags, streaming).");
    }

    private static void VerifySimple()
    {
        var random = new Random(44);
        int cases = 0;
        foreach (int length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 31, 32, 33, 64, 256, 1500 })
        foreach (int aadLength in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 32, 33 })
        {
            byte[] key = new byte[16], nonce = new byte[16], input = new byte[length], aad = new byte[aadLength];
            random.NextBytes(key);
            random.NextBytes(nonce);
            random.NextBytes(input);
            random.NextBytes(aad);
            byte[] expected = new byte[length + 16], actual = new byte[length + 16];
            var reference = new AsconAead128();
            reference.Init(true, new ParametersWithIV(new KeyParameter(key), nonce));
            reference.ProcessAadBytes(aad);
            int count = reference.ProcessBytes(input, expected);
            reference.DoFinal(expected.AsSpan(count));
            AsconSpec.Encrypt(key, nonce, input, actual, aad);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new Exception($"Simple Ascon differs from Bouncy Castle: length={length}, aad={aadLength}.");
            cases++;
        }

        byte[] blockKey = new byte[16], blockNonce = new byte[16], blocks = new byte[16 * 20];
        random.NextBytes(blockKey);
        random.NextBytes(blockNonce);
        random.NextBytes(blocks);
        var streaming = new AsconAead128();
        streaming.Init(true, new ParametersWithIV(new KeyParameter(blockKey), blockNonce));
        var simple = AsconSpec.Start(blockKey, blockNonce);
        byte[] referenceBlocks = new byte[blocks.Length], simpleBlocks = new byte[blocks.Length];
        for (int i = 0; i < blocks.Length; i += 32)
        {
            streaming.ProcessBytes(blocks.AsSpan(i, 32), referenceBlocks.AsSpan(i, 32));
            simple.EncryptBlocks(blocks.AsSpan(i, 32), simpleBlocks.AsSpan(i, 32));
        }
        if (!simpleBlocks.AsSpan().SequenceEqual(referenceBlocks))
            throw new Exception("Simple Ascon persistent blocks differ from Bouncy Castle.");
        Console.WriteLine($"Simple Ascon matches Bouncy Castle in {cases} AEAD cases and persistent blocks.");
    }
}
