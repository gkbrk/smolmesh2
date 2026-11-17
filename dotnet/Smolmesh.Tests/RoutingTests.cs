using System.Buffers.Binary;
using System.Net;
using Smolmesh;
using Xunit;

public class RoutingTests
{
    [Fact]
    public async Task FullPeerQueuesDropTheOldestPackets()
    {
        var peer = new Peer();
        const int count = 16_384;
        for (int i = 0; i < count; i++)
        {
            byte[] packet = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(packet, i);
            Assert.True(peer.Send(packet));
        }
        Assert.Equal(count - Peer.QueueLimit, peer.Dropped);
        for (int i = count - Peer.QueueLimit; i < count; i++)
        {
            Assert.True(peer.Outgoing.Reader.TryRead(out byte[]? packet));
            Assert.Equal(i, BinaryPrimitives.ReadInt32LittleEndian(packet!));
        }
        Assert.False(peer.Outgoing.Reader.TryRead(out _));
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
        Assert.True(routes.Learn(100, address, first));
        Assert.False(routes.Learn(100, address, second));
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

    [Fact]
    public void RoutesAreBounded()
    {
        var routes = new Routing();
        var peer = new Peer();
        routes.Add(peer);
        for (uint i = 0; i < Routing.MaxRoutes; i++) Assert.True(routes.Learn(1, new IPAddress(i), peer));
        Assert.False(routes.Learn(1, IPAddress.Parse("192.0.2.1"), peer));
        Assert.True(routes.Learn(2, new IPAddress(0), peer)); // Known addresses still update.
    }
}
