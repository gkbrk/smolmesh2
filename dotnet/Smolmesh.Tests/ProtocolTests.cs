using System.Net;
using Smolmesh;
using Xunit;

public class ProtocolTests
{
    [Fact]
    public void SpeckAndNodeNameAddressCompatibility()
    {
        Assert.Equal((72057594037927937UL, 72057594037927953UL), Protocol.SpeckRound(1, 2, 3));
        Assert.Equal("fd00:eaeb:953e:cb7e:0b60:e648:3457:9676", Protocol.FormatAddress(Protocol.NodeAddress("example_node")));
    }

    [Fact]
    public void DuplicateCacheEvictionAndZeroCapacity()
    {
        var seen = new SeenPackets(2);
        Assert.False(seen.Add("a"u8));
        Assert.True(seen.Add("a"u8));
        Assert.False(seen.Add("b"u8));
        Assert.False(seen.Add("c"u8));
        Assert.False(seen.Add("a"u8));
        var zero = new SeenPackets(0);
        Assert.False(zero.Add([]));
        Assert.False(zero.Add([]));
    }

    [Fact]
    public void PacketValidationAndTimestampBoundaries()
    {
        for (int i = 0; i < 40; i++)
        {
            byte[] shortPacket = new byte[i];
            if (i > 0) shortPacket[0] = 0x60;
            Assert.Null(Protocol.Destination(shortPacket));
        }
        byte[] v4 = new byte[20];
        v4[0] = 0x45;
        IPAddress.Parse("192.0.2.1").GetAddressBytes().CopyTo(v4, 16);
        Assert.Equal(IPAddress.Parse("192.0.2.1"), Protocol.Destination(v4)!);
        v4[0] = 0x4f;
        Assert.Null(Protocol.Destination(v4));
        Assert.True(Protocol.Fresh(180000, 0));
        Assert.False(Protocol.Fresh(180001, 0));
        Assert.False(Protocol.Fresh(0, ulong.MaxValue));
    }
}
