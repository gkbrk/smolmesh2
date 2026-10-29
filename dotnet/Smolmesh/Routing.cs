using System.Net;
using System.Threading.Channels;

namespace Smolmesh;

internal sealed class Peer
{
    // Match Rust: transfer bursts must not disconnect an otherwise healthy peer.
    // A slow peer can accumulate packets; byte-budgeted backpressure is future work.
    internal readonly Channel<byte[]> Outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true
    });

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

    internal void Learn(ulong timestamp, IPAddress address, Peer peer)
    {
        lock (gate)
        {
            if (!peers.Contains(peer)) return;
            if (!routes.TryGetValue(address, out var old) || timestamp > old.Timestamp)
                routes[address] = (timestamp, peer);
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
