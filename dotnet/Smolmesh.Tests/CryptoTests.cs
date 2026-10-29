using Smolmesh;
using Xunit;

public class CryptoTests
{
    [Fact]
    public void GimliHashVectorsFromRust()
    {
        Assert.Equal("b0634b2c0b082aedc5c0a2fe4ee3adcfc989ec05de6f00addb04b3aaac271f67",
            Convert.ToHexString(Gimli.Hash([])).ToLowerInvariant());
        Assert.Equal("4afb3ff784c7ad6943d49cf5da79facfa7c4434e1ce44f5dd4b28f91a84d22c8",
            Convert.ToHexString(Gimli.Hash("There's plenty for the both of us, may the best Dwarf win."u8)).ToLowerInvariant());
    }

    [Fact]
    public void KeystreamChunkBoundaries()
    {
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] whole = new byte[511], chunks = new byte[511];
        new Keystream(key).Apply(whole);
        var stream = new Keystream(key);
        stream.Apply(chunks.AsSpan(0, 1));
        stream.Apply(chunks.AsSpan(1, 16));
        stream.Apply(chunks.AsSpan(17));
        Assert.Equal(whole, chunks);
    }
}
