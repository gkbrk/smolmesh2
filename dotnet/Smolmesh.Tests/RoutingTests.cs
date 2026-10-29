using System.Buffers.Binary;
using System.Net;
using Smolmesh;
using Xunit;

public class RoutingTests
{
    [Fact]
    public async Task PeerQueuesRetainTransferBurstsWithoutDisconnecting()
    {
        var peer = new Peer();
        const int count = 16_384;
        for (int i = 0; i < count; i++)
        {
            byte[] packet = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(packet, i);
            Assert.True(peer.Send(packet));
        }
        for (int i = 0; i < count; i++)
        {
            Assert.True(peer.Outgoing.Reader.TryRead(out byte[]? packet));
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(packet!));
        }
        Assert.True(peer.Send([42]));
        Assert.True(peer.Outgoing.Reader.TryRead(out _));
        peer.Outgoing.Writer.TryComplete();
        Assert.False(peer.Send([43]));
        await peer.Outgoing.Reader.Completion;
    }

    [Fact]
    public void FastestPathRoutingAndDisconnectedPeerCleanup()
    {
        var routes = new Routing();
        var first = new Peer();
        var second = new Peer();
        routes.Add(first);
        routes.Add(second);
        var address = IPAddress.Parse("192.0.2.1");
        routes.Learn(100, address, first);
        routes.Learn(100, address, second);
        routes.Send(address, [1]);
        Assert.True(first.Outgoing.Reader.TryRead(out _));
        Assert.False(second.Outgoing.Reader.TryRead(out _));
        routes.Learn(101, address, second);
        routes.Send(address, [2]);
        Assert.True(second.Outgoing.Reader.TryRead(out _));
        routes.Remove(second);
        routes.Send(address, [3]);
        routes.Learn(102, address, second); // Removed peers cannot reintroduce dead routes.
        routes.Broadcast([4]);
        Assert.True(first.Outgoing.Reader.TryRead(out _));
        Assert.False(second.Outgoing.Reader.TryRead(out _));
    }
}
