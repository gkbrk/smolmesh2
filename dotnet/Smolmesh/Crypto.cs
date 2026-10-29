using System.Buffers.Binary;
using System.Numerics;

namespace Smolmesh;

// These are the legacy protocol's constructions, not standard Gimli AEAD.
internal sealed class Gimli
{
    internal readonly byte[] State = new byte[48];

    internal void Permute()
    {
        Span<uint> s = stackalloc uint[12];
        for (int i = 0; i < 12; i++) s[i] = BinaryPrimitives.ReadUInt32LittleEndian(State.AsSpan(i * 4));
        for (uint round = 24; round > 0; round--)
        {
            for (int col = 0; col < 4; col++)
            {
                uint x = BitOperations.RotateLeft(s[col], 24);
                uint y = BitOperations.RotateLeft(s[col + 4], 9);
                uint z = s[col + 8];
                s[col + 8] = x ^ (z << 1) ^ ((y & z) << 2);
                s[col + 4] = y ^ x ^ ((x | z) << 1);
                s[col] = z ^ y ^ ((x & y) << 3);
            }
            if ((round & 3) == 0)
            {
                (s[0], s[1]) = (s[1], s[0]);
                (s[2], s[3]) = (s[3], s[2]);
                s[0] ^= 0x9e377900 | round;
            }
            if ((round & 3) == 2)
            {
                (s[0], s[2]) = (s[2], s[0]);
                (s[1], s[3]) = (s[3], s[1]);
            }
        }
        for (int i = 0; i < 12; i++) BinaryPrimitives.WriteUInt32LittleEndian(State.AsSpan(i * 4), s[i]);
    }

    internal static byte[] Hash(ReadOnlySpan<byte> input, int length = 32)
    {
        var g = new Gimli();
        int offset = 0;
        while (offset < input.Length)
        {
            int count = Math.Min(16, input.Length - offset);
            for (int i = 0; i < count; i++) g.State[i] ^= input[offset + i];
            offset += count;
            if (count == 16) g.Permute();
        }
        g.State[input.Length % 16] ^= 0x1f;
        g.State[15] ^= 0x80;
        g.Permute();
        byte[] result = new byte[length];
        for (int i = 0; i < length; i += 16)
        {
            if (i != 0) g.Permute();
            g.State.AsSpan(0, Math.Min(16, length - i)).CopyTo(result.AsSpan(i));
        }
        return result;
    }

    internal static byte[] Multi(params byte[][] inputs)
    {
        var g = new Gimli();
        foreach (byte[] input in inputs)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(g.State, (ulong)input.Length);
            g.Permute();
        }
        foreach (byte[] input in inputs) g.Absorb(input);
        return g.State[..16];
    }

    internal void Absorb(ReadOnlySpan<byte> input)
    {
        for (int offset = 0; offset < input.Length; offset += 16)
        {
            int count = Math.Min(16, input.Length - offset);
            for (int i = 0; i < count; i++) State[i] ^= input[offset + i];
            Permute();
        }
    }
}

internal sealed class Keystream
{
    private readonly Gimli state = new();
    private int position;

    internal Keystream(byte[] key)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(state.State, (ulong)key.Length);
        state.Permute();
        state.Absorb(key);
    }

    internal void Apply(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= state.State[position++];
            if (position == 16)
            {
                state.Permute();
                position = 0;
            }
        }
    }
}
