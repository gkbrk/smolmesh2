using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using BenchmarkDotNet.Attributes;

namespace Smolmesh.NodeBenchmarks;

/// <summary>
/// One IP data packet through <see cref="MeshNode.ProcessPacketAsync"/>: forwarded to a peer, or written to the TUN.
/// </summary>
[MemoryDiagnoser]
public class ProcessPacketBenchmarks
{
    [Params(64, 1400)]
    public int Length { get; set; }

    private MeshNode node = null!;
    private Peer peer = null!;
    private readonly NullTun tun = new();
    private byte[] transit = null!;
    private byte[] local = null!;
    private ulong counter;

    [GlobalSetup]
    public void Setup()
    {
        node = new MeshNode(new Configuration { NodeName = "bench", ControlPort = 0 });
        var routing = (Routing)typeof(MeshNode).GetField("routing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(node)!;
        peer = new Peer();
        routing.Add(peer);
        var elsewhere = Protocol.NodeAddress("elsewhere");
        routing.Learn(1, elsewhere, peer);
        transit = Protocol.Packet(3, IPv6(Protocol.NodeAddress("origin"), elsewhere));
        local = Protocol.Packet(3, IPv6(elsewhere, Protocol.NodeAddress("bench")));

        // Both paths must get past the freshness and duplicate checks, or this measures the early return.
        Transit().GetAwaiter().GetResult();
        Local().GetAwaiter().GetResult();
        if (tun.Writes != 1) throw new InvalidOperationException("Transit packet was not forwarded or local packet not delivered.");
    }

    [Benchmark]
    public Task Transit()
    {
        Vary(transit);
        Task task = node.ProcessPacketAsync(transit, peer, tun, default);
        if (!peer.Outgoing.Reader.TryRead(out _)) throw new InvalidOperationException("Transit packet was not forwarded.");
        return task;
    }

    [Benchmark]
    public Task Local()
    {
        Vary(local);
        return node.ProcessPacketAsync(local, peer, tun, default);
    }

    private byte[] IPv6(IPAddress source, IPAddress destination)
    {
        byte[] packet = new byte[Length];
        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)(Length - 40));
        packet[6] = 17;
        packet[7] = 64;
        source.GetAddressBytes().CopyTo(packet, 8);
        destination.GetAddressBytes().CopyTo(packet, 24);
        return packet;
    }

    // A counter after the IP header keeps every packet new to the duplicate filter; the timestamp is
    // refreshed now and then so long runs stay inside the freshness window.
    private void Vary(byte[] packet)
    {
        if ((counter++ & 0xffff) == 0) BinaryPrimitives.WriteUInt64LittleEndian(packet, Protocol.Millis);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(9 + 40), counter);
    }

    private sealed class NullTun : ITun
    {
        internal int Writes;
        public Task<byte[]> ReadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token) { Writes++; return Task.CompletedTask; }
        public Task RouteAsync(IPAddress address, bool add, CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
}
