using System.Net;
using System.Threading.Channels;

namespace Smolmesh;

internal sealed class Peer
{
    // About 6 MB of tunnel traffic, more than the bandwidth-delay product of the links we run on.
    internal const int QueueLimit = 4096;

    // A full queue drops its oldest packet instead of blocking or disconnecting: memory and
    // delay stay bounded, and inner TCP connections see the loss and slow down.
    internal readonly Channel<byte[]> Outgoing;

    private long dropped;

    internal Peer() => Outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueLimit)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    }, _ => Interlocked.Increment(ref dropped));

    internal long Dropped => Interlocked.Read(ref dropped);

    internal bool Send(byte[] packet) => Outgoing.Writer.TryWrite(packet);
}

internal sealed class Routing
{
    private readonly object gate = new();
    private readonly HashSet<Peer> peers = [];
    private readonly Dictionary<IPAddress, (ulong Timestamp, Peer Peer)> routes = [];

    internal void Add(Peer peer) { lock (gate) peers.Add(peer); }

    internal void Remove(Peer peer)
    {
        lock (gate)
        {
            peers.Remove(peer);
            foreach (var address in routes.Where(r => r.Value.Peer == peer).Select(r => r.Key).ToArray())
                routes.Remove(address);
        }
        peer.Outgoing.Writer.TryComplete();
    }

    /// <summary>True when the announcement was newer than the stored route and replaced it.</summary>
    internal bool Learn(ulong timestamp, IPAddress address, Peer peer)
    {
        lock (gate)
        {
            if (!peers.Contains(peer)) return false;
            if (routes.TryGetValue(address, out var old) && timestamp <= old.Timestamp) return false;
            routes[address] = (timestamp, peer);
            return true;
        }
    }

    internal void Broadcast(byte[] packet)
    {
        lock (gate) foreach (var peer in peers) peer.Send(packet);
    }

    internal void Send(IPAddress address, byte[] packet)
    {
        lock (gate) if (routes.TryGetValue(address, out var route)) route.Peer.Send(packet);
    }
}
