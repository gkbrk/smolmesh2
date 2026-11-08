using System.Buffers.Binary;

namespace Smolmesh.Crypto;

internal sealed class GimliKeystream
{
    private readonly Gimli state = new();
    private int position;

    internal GimliKeystream(byte[] key)
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
