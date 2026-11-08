using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Smolmesh.Crypto;

namespace Smolmesh.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class PacketBenchmarks
{
    [Params(64, 256, 1500)]
    public int Length { get; set; }

    private byte[] packet = null!;
    private byte[] frame = null!;
    private byte[] output = null!;
    private byte[] macKey = null!;
    private byte[] nonce = null!;
    private byte[] encKey = null!;
    private KeyParameter key = null!;
    private GimliKeystream keystream = null!;
    private AsconAead128 ascon = null!;
    private ulong counter;

    [GlobalSetup]
    public void Setup()
    {
        packet = new byte[Length];
        new Random(42).NextBytes(packet);
        frame = new byte[Length + 18];
        output = new byte[Length + 16];
        macKey = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();
        encKey = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        nonce = new byte[16];
        key = new KeyParameter(encKey);
        keystream = new GimliKeystream(encKey);
        ascon = new AsconAead128();
    }

    // Same crypto/framing operations as LegacyTcp.Encode, with reusable output storage.
    // Session key derivation and GimliKeystream initialization are outside the timed region.
    [Benchmark(Baseline = true)]
    public byte GimliLegacyPacket()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)Length);
        packet.CopyTo(frame, 2);
        Gimli.Multi(macKey, packet).CopyTo(frame, Length + 2);
        keystream.Apply(frame);
        return frame[^1];
    }

    // Standard per-message AEAD: fresh nonce, initialization, ciphertext, and 128-bit tag.
    // No AAD; unlike the legacy format this does not encrypt a two-byte length prefix.
    [Benchmark]
    public byte AsconStandardPacket()
    {
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, ++counter);
        ascon.Init(true, new ParametersWithIV(key, nonce));
        int written = ascon.ProcessBytes(packet, output);
        ascon.DoFinal(output.AsSpan(written));
        return output[^1];
    }

    [Benchmark]
    public byte AsconSimplePacket()
    {
        BinaryPrimitives.WriteUInt64LittleEndian(nonce, ++counter);
        AsconSpec.Encrypt(encKey, nonce, packet, output);
        return output[^1];
    }
}

[MemoryDiagnoser]
[ShortRunJob]
public class DuplexBenchmarks
{
    [Params(64, 256, 1500)]
    public int Length { get; set; }

    private byte[] input = null!;
    private byte[] output = null!;
    private Gimli gimli = null!;
    private AsconAead128 ascon = null!;
    private AsconSpec simple;

    [GlobalSetup]
    public void Setup()
    {
        // Both cores process complete 16-byte blocks: 1500 bytes rounds up to 1504.
        input = new byte[(Length + 15) / 16 * 16];
        new Random(42).NextBytes(input.AsSpan(0, Length));
        output = new byte[input.Length];
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        gimli = new Gimli();
        gimli.Absorb(key);
        ascon = new AsconAead128();
        ascon.Init(true, new ParametersWithIV(new KeyParameter(key), new byte[16]));
        simple = AsconSpec.Start(key, new byte[16]);
    }

    // Throughput experiment ONLY: persistent state, no record boundaries or tags.
    // This is NOT a complete authenticated-encryption construction.
    [Benchmark(Baseline = true)]
    public byte GimliDuplexBlocks()
    {
        Span<byte> state = gimli.State;
        for (int offset = 0; offset < input.Length; offset += 16)
        {
            ulong x = BinaryPrimitives.ReadUInt64LittleEndian(state)
                ^ BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(offset));
            ulong y = BinaryPrimitives.ReadUInt64LittleEndian(state[8..])
                ^ BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(offset + 8));
            BinaryPrimitives.WriteUInt64LittleEndian(state, x);
            BinaryPrimitives.WriteUInt64LittleEndian(state[8..], y);
            state[..16].CopyTo(output.AsSpan(offset));
            gimli.Permute();
        }
        return output[^1];
    }

    // Reuse the library's actual encryption loop, without finalizing the message.
    // Initialization is amortized across ALL benchmark invocations, not per record.
    [Benchmark]
    public byte AsconDuplexBlocks()
    {
        ascon.ProcessBytes(input, output);
        return output[^1];
    }

    [Benchmark]
    public byte AsconSimpleDuplexBlocks()
    {
        simple.EncryptBlocks(input, output);
        return output[^1];
    }
}
