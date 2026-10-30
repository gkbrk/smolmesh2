using System.Buffers.Binary;
using System.Runtime.Intrinsics;

namespace Smolmesh;

// These are the legacy protocol's constructions, not standard Gimli AEAD.
internal sealed class Gimli
{
    internal readonly byte[] State = new byte[48];

    // The state is three rows of four little-endian words. Gimli updates the four columns
    // the same way, so each row is one vector and a round handles all columns at once.
    internal void Permute()
    {
        Span<byte> state = State;
        Vector128<uint> a = Vector128.Create<byte>(state).AsUInt32();
        Vector128<uint> b = Vector128.Create<byte>(state[16..]).AsUInt32();
        Vector128<uint> c = Vector128.Create<byte>(state[32..]).AsUInt32();
        for (uint round = 24; round > 0; round--)
        {
            Vector128<uint> x = RotateLeft(a, 24);
            Vector128<uint> y = RotateLeft(b, 9);
            Vector128<uint> z = c;
            c = x ^ (z << 1) ^ ((y & z) << 2);
            b = y ^ x ^ ((x | z) << 1);
            a = z ^ y ^ ((x & y) << 3);
            if ((round & 3) == 0)
            {
                // Small swap: words 0<->1 and 2<->3, then add the round constant.
                a = Vector128.Shuffle(a, Vector128.Create(1u, 0, 3, 2));
                a ^= Vector128.CreateScalar(0x9e377900 | round);
            }
            else if ((round & 3) == 2)
            {
                // Big swap: words 0<->2 and 1<->3.
                a = Vector128.Shuffle(a, Vector128.Create(2u, 3, 0, 1));
            }
        }
        a.AsByte().CopyTo(state);
        b.AsByte().CopyTo(state[16..]);
        c.AsByte().CopyTo(state[32..]);
    }

    private static Vector128<uint> RotateLeft(Vector128<uint> v, int bits) => (v << bits) | (v >>> (32 - bits));

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
