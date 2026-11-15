using Serilog;
using Smolmesh.Crypto;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Smolmesh;

// Mesh packets over UDP, one per datagram. A datagram is the packet enciphered with LION under a
// key derived from the shared secret, and nothing else: it is as long as the packet and looks
// like random bytes. There is no nonce or tag.
//
// The timestamp that starts every mesh packet does their work. It makes every packet different.
// It stands in for the tag: a datagram made without the key decrypts to random bytes, whose
// timestamp is not the present moment.
//
// A connector starts with a hello, a packet only this transport knows, and repeats it. It holds a
// random link id. A listener keeps one link per id, so any number of neighbours can share a key,
// and moves the link to the address of a hello newer than the last one it had for that id. Only
// a hello starts or moves a link, so a captured datagram replayed from a forged address does
// nothing: a data packet has no link there, and a hello is not newer. A link's newest hello is
// kept until a replay of it would no longer be fresh.
//
// A captured data datagram stays valid while its timestamp is fresh, and is accepted again from
// the link's address if it is replayed after the mesh has forgotten its packet.
internal static class UdpTransport1
{
    // Every node announces its name at least every 10 seconds, so a live listener is never this quiet.
    internal static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);
    // A connector says hello this often. A listener drops a neighbour it has not heard from for the idle time.
    internal static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);
    // Link-level and never passed on to the mesh: a random link id, enciphered like any packet. It
    // is the command after the last mesh command, and 17 bytes with its header, LION's smallest size.
    internal const byte Hello = Protocol.LastCommand + 1;
    private const int HelloSize = 17;
    private const int MaxDatagram = 65507;
    // Links of one listener, idle ones included, at most.
    private const int MaxLinks = 256;
    private const int HeaderSize = 9;
    // Marks a filled-up packet in its command byte, on the wire only. No command is this large,
    // so the bit tells such a packet from a real one of the same length.
    private const byte Padded = 0x80;

    // The keys of the two directions: [0] from the connecting side, [1] from the listening side.
    internal static byte[][] DeriveKeys(byte[] key) => [Derive(key, "udp1 connect"), Derive(key, "udp1 listen")];

    private static byte[] Derive(byte[] key, string label)
    {
        byte[] output = new byte[Lion.KeySize];
        Ascon.Multi([key, Encoding.UTF8.GetBytes(label)], output);
        return output;
    }

    // IPv4 and IPv6 on one dual-mode socket, or IPv4 only if the host has no IPv6.
    internal static Socket Bind(int port)
    {
        bool v6 = Socket.OSSupportsIPv6;
        var socket = new Socket(v6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (v6) socket.DualMode = true;
        socket.Bind(new IPEndPoint(v6 ? IPAddress.IPv6Any : IPAddress.Any, port));
        LegacyTcp.SizeBuffers(socket);
        return socket;
    }

    // One neighbour of a listener, named by the random id in its hellos. It has a peer and an
    // address while it is heard from, and keeps the time of its newest hello after that, for as
    // long as a replay of it would still be fresh.
    private sealed class Link(ulong id, byte[][] keys)
    {
        internal readonly ulong Id = id;
        internal readonly byte[][] Keys = keys;
        internal byte[] ReceiveKey => Keys[0];
        internal byte[] SendKey => Keys[1];
        // Replaced, never changed in place: the send loop reads it without the lock.
        internal volatile SocketAddress? Remote;
        internal ulong NewestHello;
        internal long Heard;
        internal Peer? Peer;
        internal Task Send = Task.CompletedTask;
    }

    internal static async Task ListenAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        using Socket socket = Bind(config.Port);
        Log.Information("Listening on UDP port {Port}", config.Port);
        await ListenAsync(socket, config.Keys.Select(Encoding.UTF8.GetBytes).ToArray(), routing, incoming, Idle, token);
    }

    internal static async Task ListenAsync(Socket socket, byte[][] keys, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, TimeSpan idle, CancellationToken token)
    {
        byte[][][] keySets = keys.Select(DeriveKeys).ToArray();
        var links = new Links();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task expire = ExpireAsync(links, routing, idle, stop.Token);
        try
        {
            byte[] packet = new byte[MaxDatagram];
            await foreach (var (datagram, from) in new DatagramBatch().ReceiveAllAsync(socket, stop.Token))
            {
                // The link at this address first, so that only a new one costs a trial of every key.
                Link? at;
                lock (links) links.ByAddress.TryGetValue(from, out at);
                byte[][]? opened = null;
                int length = 0;
                ulong timestamp = 0;
                if (at is not null && Open(at.ReceiveKey, datagram.Span, packet, out length, out timestamp)) opened = at.Keys;
                else
                {
                    at = null;
                    foreach (var set in keySets)
                        if (Open(set[0], datagram.Span, packet, out length, out timestamp))
                        {
                            opened = set;
                            break;
                        }
                }
                if (opened is null) continue;

                if (packet[8] == Hello)
                {
                    if (length == HelloSize)
                        lock (links) Greet(socket, links, routing, opened, BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(HeaderSize)),
                            timestamp, from, stop.Token);
                    continue;
                }
                // Only a hello starts a link, so a packet from an address that has none goes nowhere.
                if (at is null) continue;
                lock (links) at.Heard = Environment.TickCount64;
                Peer? peer = at.Peer;
                if (peer is null) continue;
                await incoming.WriteAsync((packet.AsSpan(0, length).ToArray(), peer), stop.Token);
            }
        }
        finally
        {
            stop.Cancel();
            Link[] all;
            lock (links) all = [.. links.ById.Values];
            foreach (var link in all) lock (links) Retire(links, link, routing);
            try { await Task.WhenAll([expire, .. all.Select(l => l.Send)]); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class Links
    {
        internal readonly Dictionary<ulong, Link> ById = [];
        internal readonly Dictionary<SocketAddress, Link> ByAddress = [];
    }

    // A hello from an address, under the keys it opened with. Starts the link of its id, moves it
    // to this address, or just shows it is alive. Called with links locked.
    private static void Greet(Socket socket, Links links, Routing routing, byte[][] keys, ulong id, ulong timestamp,
        SocketAddress from, CancellationToken token)
    {
        if (links.ById.TryGetValue(id, out Link? link))
        {
            // Not newer than the last hello, so a replay; or not under the keys the link began with.
            if (timestamp <= link.NewestHello || !ReferenceEquals(link.Keys, keys)) return;
        }
        else
        {
            if (links.ById.Count >= MaxLinks) return;
            links.ById[id] = link = new Link(id, keys);
        }
        link.NewestHello = timestamp;
        link.Heard = Environment.TickCount64;
        bool started = link.Peer is null;
        if (started || !from.Equals(link.Remote))
        {
            // An address belongs to one link: a node that started over from it has a new id.
            if (links.ByAddress.TryGetValue(from, out Link? other) && other != link) Retire(links, other, routing);
            if (link.Remote is { } old) links.ByAddress.Remove(old);
            SocketAddress remote = Copy(from);
            link.Remote = remote;
            links.ByAddress[remote] = link;
            Log.Information("UDP link from {Remote} is {State}", Describe(remote), started ? "up" : "at a new address");
        }
        if (started)
        {
            Peer peer = link.Peer = new Peer();
            routing.Add(peer);
            link.Send = SendAsync(socket, peer, link.SendKey, () => link.Remote, token);
        }
        // Answered, so that the connector knows the link is up without waiting for mesh traffic.
        link.Peer!.Send(Protocol.Packet(Hello, BitConverter.GetBytes(id)));
    }

    // Takes the peer and the address away from a link. Its newest hello stays in ById.
    private static void Retire(Links links, Link link, Routing routing)
    {
        if (link.Peer is not null) routing.Remove(link.Peer);
        link.Peer = null;
        if (link.Remote is { } address) links.ByAddress.Remove(address);
        link.Remote = null;
    }

    private static async Task ExpireAsync(Links links, Routing routing, TimeSpan idle, CancellationToken token)
    {
        using var timer = new PeriodicTimer(idle / 4);
        while (await timer.WaitForNextTickAsync(token))
            lock (links)
                foreach (Link link in links.ById.Values.ToArray())
                {
                    if (link.Peer is not null && Environment.TickCount64 - link.Heard >= idle.TotalMilliseconds)
                    {
                        Log.Information("UDP link from {Remote} went idle", Describe(link.Remote!));
                        Retire(links, link, routing);
                    }
                    // Forgotten only when a replay of its newest hello is no longer fresh.
                    if (link.Peer is null && !Protocol.Fresh(link.NewestHello, Protocol.Millis))
                        links.ById.Remove(link.Id);
                }
    }

    internal static async Task ConnectAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        byte[][] keys = DeriveKeys(Encoding.UTF8.GetBytes(config.Key!));
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Resolved again for every attempt: the listener may have moved.
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(config.Host, token);
                if (addresses.Length == 0) throw new IOException("Host has no address.");
                bool heard = await RunAsync(new IPEndPoint(addresses[0], config.Port), keys, routing, incoming, Silence, token);
                Log.Warning("UDP link to {Host}:{Port}: {Message}", config.Host, config.Port, heard ? "went silent" : "no answer");
            }
            catch (Exception ex) when (ex is SocketException or IOException && !token.IsCancellationRequested)
            {
                Log.Warning("UDP link to {Host}:{Port}: {Message}", config.Host, config.Port, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.NextDouble() * 5), token);
            }
        }
    }

    // One link to a listener, until it has been silent for too long. The peer exists and sends from
    // the start, with a hello before anything else, since the listener learns of us from that.
    // Returns whether the listener was ever heard.
    internal static async Task<bool> RunAsync(IPEndPoint remote, byte[][] keys, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, TimeSpan silence, CancellationToken token, TimeSpan? helloEvery = null)
    {
        using Socket socket = Bind(0);
        if (socket.AddressFamily == AddressFamily.InterNetworkV6) remote = new IPEndPoint(remote.Address.MapToIPv6(), remote.Port);
        SocketAddress address = remote.Serialize();
        var peer = new Peer();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        routing.Add(peer);
        Task send = SendAsync(socket, peer, keys[0], () => address, stop.Token);
        Task hellos = HelloAsync(peer, helloEvery ?? HelloInterval, stop.Token);
        bool heard = false;
        try
        {
            byte[] packet = new byte[MaxDatagram];
            stop.CancelAfter(silence);
            await foreach (var (datagram, _) in new DatagramBatch().ReceiveAllAsync(socket, stop.Token))
            {
                // Whoever holds the key is the listener, whatever address the datagram came from.
                if (!Open(keys[1], datagram.Span, packet, out int length, out _)) continue;
                if (!heard) Log.Information("UDP link to {Remote} is up", LegacyTcp.Describe(remote));
                heard = true;
                stop.CancelAfter(silence);
                if (packet[8] == Hello) continue;
                await incoming.WriteAsync((packet.AsSpan(0, length).ToArray(), peer), stop.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally
        {
            stop.Cancel();
            routing.Remove(peer);
            try { await Task.WhenAll(send, hellos); }
            catch (OperationCanceledException) { }
        }
        return heard;
    }

    // The first hello goes out at once, the rest every interval. The id names this link to the
    // listener for as long as this runs.
    private static async Task HelloAsync(Peer peer, TimeSpan every, CancellationToken token)
    {
        byte[] id = RandomNumberGenerator.GetBytes(8);
        using var timer = new PeriodicTimer(every);
        do peer.Send(Protocol.Packet(Hello, id));
        while (await timer.WaitForNextTickAsync(token));
    }

    // Copies a packet into datagram and enciphers it there, returning the datagram's length. A
    // packet shorter than LION's smallest block (a name or IPv4 tracer) is filled up to it: zeros,
    // then how many bytes were added, counting that one.
    internal static int Seal(byte[] key, ReadOnlySpan<byte> packet, Span<byte> datagram)
    {
        int length = packet.Length;
        packet.CopyTo(datagram);
        if (length < Lion.MinSize)
        {
            datagram[length..Lion.MinSize].Clear();
            datagram[Lion.MinSize - 1] = (byte)(Lion.MinSize - length);
            datagram[8] |= Padded;
            length = Lion.MinSize;
        }
        Lion.Encrypt(key, datagram[..length]);
        return length;
    }

    // Deciphers a datagram into the start of packet and gives the packet's length. False unless it
    // is a mesh packet of the present moment.
    internal static bool Open(byte[] key, ReadOnlySpan<byte> datagram, Span<byte> packet, out int length, out ulong timestamp)
    {
        timestamp = 0;
        length = datagram.Length;
        if (length < Lion.MinSize) return false;
        packet = packet[..length];
        datagram.CopyTo(packet);
        Lion.Decrypt(key, packet);
        if ((packet[8] & Padded) != 0)
        {
            int added = packet[^1];
            if (length != Lion.MinSize || added < 1 || added > Lion.MinSize - HeaderSize
                || packet[(length - added)..^1].ContainsAnyExcept((byte)0)) return false;
            packet[8] ^= Padded;
            length -= added;
        }
        timestamp = BinaryPrimitives.ReadUInt64LittleEndian(packet);
        return Protocol.Fresh(timestamp, Protocol.Millis) && packet[8] <= Hello;
    }

    // Enciphers and sends every queued packet until the peer is removed from routing, or its
    // neighbour has no address any more. What is queued goes out in one batch.
    private static async Task SendAsync(Socket socket, Peer peer, byte[] key, Func<SocketAddress?> remote, CancellationToken token)
    {
        var batch = new DatagramBatch();
        var queue = peer.Outgoing.Reader;
        while (await queue.WaitToReadAsync(token))
        {
            if (remote() is not { } address) return;
            while (!batch.Full && queue.TryRead(out byte[]? packet))
            {
                // Nothing the other side would not take for a mesh packet is worth sending.
                if (packet.Length is < HeaderSize or > MaxDatagram || packet[8] > Hello) continue;
                // Packets are shared between peers, so each is enciphered in a copy.
                batch.Add(Seal(key, packet, batch.Next));
            }
            batch.Send(socket, address);
        }
    }

    private static SocketAddress Copy(SocketAddress address)
    {
        var copy = new SocketAddress(address.Family, address.Size);
        address.Buffer.Span[..address.Size].CopyTo(copy.Buffer.Span);
        return copy;
    }

    private static string Describe(SocketAddress address) => LegacyTcp.Describe(
        new IPEndPoint(address.Family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0).Create(address));
}
