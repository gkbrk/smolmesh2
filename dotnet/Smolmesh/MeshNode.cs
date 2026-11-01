using Serilog;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Smolmesh;

internal sealed class MeshNode
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Configuration config;
    private readonly Routing routing = new();
    private readonly SeenPackets seen = new(16_384);
    private readonly HashSet<IPAddress> ourAddresses;
    private readonly Channel<(byte[] Packet, Peer Peer)> incoming = Channel.CreateBounded<(byte[], Peer)>(1024);
    private readonly ConcurrentDictionary<string, (IPAddress Address, long Tick)> peers = new();
    private readonly Channel<IPAddress> routeRequests = Channel.CreateBounded<IPAddress>(1024);

    internal MeshNode(Configuration config)
    {
        // The only validation point: nothing below may touch the config before this.
        config.Validate();
        this.config = config;
        ourAddresses = [Protocol.NodeAddress(config.NodeName), .. config.IPv4Addresses.Select(IPAddress.Parse)];
    }

    internal async Task RunAsync(CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using ITun? tun = await Tun.OpenAsync(config, Protocol.NodeAddress(config.NodeName), stop.Token);
        Log.Information("Node {NodeName:l}: {Address}", JsonSerializer.Serialize(config.NodeName), Protocol.FormatAddress(Protocol.NodeAddress(config.NodeName)));
        List<Task> tasks = [];
        try
        {
            foreach (var transport in config.Transports)
                tasks.Add(transport.Type == "legacy_tcp_connect"
                    ? LegacyTcp.ConnectLoopAsync(transport, routing, incoming.Writer, stop.Token)
                    : LegacyTcp.ListenAsync(transport, routing, incoming.Writer, stop.Token));
            tasks.Add(AnnounceAsync(0, Encoding.UTF8.GetBytes(config.NodeName), stop.Token));
            foreach (string address in config.IPv4Addresses)
                tasks.Add(AnnounceAsync(2, IPAddress.Parse(address).GetAddressBytes(), stop.Token));
            tasks.Add(ProcessAsync(tun, stop.Token));
            if (config.ControlPort > 0) tasks.Add(Control.ListenAsync(config.ControlPort, Peers, Trace, stop.Token));
            if (tun is not null)
            {
                tasks.Add(ReadTunAsync(tun, stop.Token));
                tasks.Add(AddRoutesAsync(tun, stop.Token));
            }
            await await Task.WhenAny(tasks);
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private object Peers() => peers.Where(p => !ourAddresses.Contains(p.Value.Address)).Select(p => new
    {
        name = p.Key,
        address = Protocol.FormatAddress(p.Value.Address),
        seconds = (Environment.TickCount64 - p.Value.Tick) / 1000,
    }).OrderBy(p => p.name, StringComparer.Ordinal).ToList();

    private const byte TraceTtl = 32;

    // Probe payload: nonce(8) ttl(1) target(16) source(16). Reply payload: nonce(8) ttl(1) name.
    private void HandleProbe(byte[] payload)
    {
        var target = new IPAddress(payload[9..25]);
        var source = new IPAddress(payload[25..41]);
        byte ttl = payload[8];
        byte[] name = Encoding.UTF8.GetBytes(config.NodeName);
        routing.Send(source, Protocol.Packet(5, [.. payload[..9], .. name]));
        if (ttl > 1 && !ourAddresses.Contains(target))
        {
            payload[8] = (byte)(ttl - 1);
            routing.Send(target, Protocol.Packet(4, payload));
        }
    }

    private readonly ConcurrentDictionary<ulong, ConcurrentQueue<(string Name, byte Ttl, long Tick)>> traces = new();

    /// <summary>Sends one TTL-limited probe toward a node; every hop replies with its name. Waits 3 seconds, then returns the hop table.</summary>
    private async Task<string> Trace(string name)
    {
        var target = Protocol.NodeAddress(name);
        ulong nonce = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var replies = traces[nonce] = new();
        byte[] payload = [.. BitConverter.GetBytes(nonce), TraceTtl, .. target.GetAddressBytes(), .. Protocol.NodeAddress(config.NodeName).GetAddressBytes()];
        long start = Environment.TickCount64;
        routing.Send(target, Protocol.Packet(4, payload));
        await Task.Delay(3000);
        traces.TryRemove(nonce, out _);
        string table = string.Concat(replies.OrderBy(r => TraceTtl - r.Ttl).Select(r => $"{TraceTtl - r.Ttl + 1,3}  {r.Name}  {r.Tick - start}ms\n"));
        return table.Length > 0 ? table : "No replies.\n";
    }

    private async Task AnnounceAsync(byte command, byte[] payload, CancellationToken token)
    {
        while (true)
        {
            routing.Broadcast(Protocol.Packet(command, payload));
            await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.NextDouble() * 5), token);
        }
    }

    private async Task ProcessAsync(ITun? tun, CancellationToken token)
    {
        await foreach (var (packet, peer) in incoming.Reader.ReadAllAsync(token))
            await ProcessPacketAsync(packet, peer, tun, token);
    }

    internal async Task ProcessPacketAsync(byte[] packet, Peer peer, ITun? tun, CancellationToken token)
    {
        if (packet.Length < 9) return;
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(packet);
        if (!Protocol.Fresh(timestamp, Protocol.Millis) || seen.Add(packet)) return;
        byte[] payload = packet[9..];
        IPAddress? address;
        switch (packet[8])
        {
            case 0:
                string nodeName;
                try
                {
                    nodeName = StrictUtf8.GetString(payload);
                    address = Protocol.NodeAddress(nodeName);
                }
                catch (DecoderFallbackException) { return; }
                // JSON quoting keeps peer-controlled names on one log line.
                peers[nodeName] = (address, Environment.TickCount64);
                Log.Information("Received node tracer: {NodeName:l} ({Address})", JsonSerializer.Serialize(nodeName), Protocol.FormatAddress(address));
                break;
            case 1 when payload.Length == 16:
            case 2 when payload.Length == 4:
                address = new IPAddress(payload);
                Log.Information("Received IPv{Version} tracer: {Address}", packet[8] == 1 ? 6 : 4, Protocol.FormatAddress(address));
                break;
            case 3:
                address = Protocol.Destination(payload);
                if (address is null) return;
                if (ourAddresses.Contains(address))
                {
                    if (tun is not null) await tun.WriteAsync(payload, token);
                }
                else routing.Send(address, packet);
                return;
            case 4 when payload.Length == 41:
                HandleProbe(payload);
                return;
            case 5 when payload.Length > 9:
                if (traces.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(payload), out var replies))
                    replies.Enqueue((StrictUtf8.GetString(payload[9..]), payload[8], Environment.TickCount64));
                return;
            default:
                return;
        }
        routing.Learn(timestamp, address, peer);
        routing.Broadcast(packet);
        if (tun is not null && !ourAddresses.Contains(address)) routeRequests.Writer.TryWrite(address);
    }

    private async Task ReadTunAsync(ITun tun, CancellationToken token)
    {
        while (true)
        {
            byte[] packet = await tun.ReadAsync(token);
            var destination = Protocol.Destination(packet);
            if (destination is not null && packet.Length <= ushort.MaxValue - 9)
                routing.Send(destination, Protocol.Packet(3, packet));
        }
    }

    private async Task AddRoutesAsync(ITun tun, CancellationToken token)
    {
        HashSet<IPAddress> added = [];
        await foreach (var address in routeRequests.Reader.ReadAllAsync(token))
        {
            if (added.Contains(address)) continue;
            try
            {
                await tun.AddRouteAsync(address, token);
                added.Add(address);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log.Warning("Route for {Address}: {Message}", address, ex.Message);
            }
        }
    }
}
