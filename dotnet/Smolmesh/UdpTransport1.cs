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
// Both ends of a link are alike. A node has one socket and a list of neighbours, each a secret
// and maybe a host. It says hello to every host it knows, from that socket, and answers any
// hello under one of its secrets, so a link forms whichever side speaks first, or both. A hello
// is a packet only this transport knows, enciphered under a key of its secret alone, and holds
// the sender's link id: random, one per socket for as long as it runs. A node keeps one link per
// id it hears, so any number of neighbours can share a secret, and moves the link to the address
// of a hello newer than the last one it had for that id. Only a hello starts or moves a link, so
// a captured datagram replayed from a forged address does nothing: a data packet has no link
// there, and a hello is not newer. A link's newest hello is kept until a replay of it would no
// longer be fresh.
//
// Data goes under a key of the secret and both ids, one for each direction, so a datagram sent
// back to where it came from, or kept from an earlier run of either node, is not accepted. A
// captured data datagram stays valid while its timestamp is fresh, and is accepted again from
// the link's address if it is replayed after the mesh has forgotten its packet.
internal static class UdpTransport1
{
    // A node says hello to each host this often while they are linked, and more often until then.
    // A link it has not heard from for the idle time is dropped. Every node announces its name at
    // least every 10 seconds, so a live neighbour is never that quiet.
    internal static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Idle = TimeSpan.FromMinutes(3);
    // Link-level and never passed on to the mesh: the sender's link id, enciphered like any
    // packet. They are the commands after the last mesh command, and 17 bytes with their header,
    // LION's smallest size. A hello is answered, an answer is not.
    internal const byte Hello = Protocol.LastCommand + 1;
    internal const byte Answer = Protocol.LastCommand + 2;
    internal const int HelloSize = 17;
    private const int MaxDatagram = 65507;
    // Links of one socket, idle ones included, at most.
    private const int MaxLinks = 256;
    private const int HeaderSize = 9;
    // Marks a filled-up packet in its command byte, on the wire only. No command is this large,
    // so the bit tells such a packet from a real one of the same length.
    private const byte Padded = 0x80;

    internal static byte[] HelloKey(byte[] secret) => Derive([secret, Encoding.UTF8.GetBytes("udp1 hello")]);

    // The key of data from the node with link id `from` to the one with `to`.
    internal static byte[] DataKey(byte[] secret, ulong from, ulong to) =>
        Derive([secret, Encoding.UTF8.GetBytes("udp1 data"), BitConverter.GetBytes(from), BitConverter.GetBytes(to)]);

    private static byte[] Derive(byte[][] inputs)
    {
        byte[] output = new byte[Lion.KeySize];
        Ascon.Multi(inputs, output);
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

    // A secret, and the host to say hello to under it, if there is one.
    internal sealed record Neighbour(byte[] Secret, string? Host = null, int Port = 0);

    // One per distinct secret of a socket, so that a hello costs one trial per secret.
    private sealed class Secret(byte[] value)
    {
        internal readonly byte[] Value = value;
        internal readonly byte[] HelloKey = UdpTransport1.HelloKey(value);
    }

    // One neighbour, named by the random id in its hellos. It has a peer and an address while it
    // is heard from, and keeps the time of its newest hello after that, for as long as a replay of
    // it would still be fresh.
    private sealed class Link(ulong id, Secret secret, ulong ours)
    {
        internal readonly ulong Id = id;
        internal readonly Secret Secret = secret;
        internal readonly byte[] ReceiveKey = DataKey(secret.Value, id, ours);
        internal readonly byte[] SendKey = DataKey(secret.Value, ours, id);
        // Replaced, never changed in place: the send loop reads it without the lock.
        internal volatile SocketAddress? Remote;
        internal ulong NewestHello;
        internal long Heard;
        internal Peer? Peer;
        internal Task Send = Task.CompletedTask;
    }

    private sealed class Links
    {
        internal readonly Dictionary<ulong, Link> ById = [];
        internal readonly Dictionary<SocketAddress, Link> ByAddress = [];
    }

    internal static async Task RunAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        using Socket socket = Bind(config.Port);
        Log.Information("UDP on port {Port}", ((IPEndPoint)socket.LocalEndPoint!).Port);
        Neighbour[] neighbours = config.Peers.Select(p => new Neighbour(Encoding.UTF8.GetBytes(p.Key!),
            string.IsNullOrWhiteSpace(p.Host) ? null : p.Host, p.Port)).ToArray();
        await RunAsync(socket, neighbours, routing, incoming, Idle, HelloInterval, token);
    }

    internal static async Task RunAsync(Socket socket, Neighbour[] neighbours, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, TimeSpan idle, TimeSpan helloEvery, CancellationToken token)
    {
        ulong ours = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        var bySecret = new Dictionary<string, Secret>();
        foreach (var neighbour in neighbours) bySecret.TryAdd(Convert.ToHexString(neighbour.Secret), new Secret(neighbour.Secret));
        Secret[] secrets = [.. bySecret.Values];
        var links = new Links();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        List<Task> tasks = [ExpireAsync(links, routing, idle, stop.Token)];
        foreach (var neighbour in neighbours)
            if (neighbour.Host is not null)
                tasks.Add(GreetAsync(socket, neighbour, bySecret[Convert.ToHexString(neighbour.Secret)], ours, links, helloEvery, stop.Token));
        try
        {
            byte[] packet = new byte[MaxDatagram];
            await foreach (var (datagram, from) in new DatagramBatch().ReceiveAllAsync(socket, stop.Token))
            {
                // An IPv6 datagram can be longer than the buffer: ignore it, don't crash on it.
                if (datagram.Length > MaxDatagram) continue;
                Link? at;
                lock (links) links.ByAddress.TryGetValue(from, out at);
                if (at is not null && Open(at.ReceiveKey, datagram.Span, packet, out int length, out _) && packet[8] <= Protocol.LastCommand)
                {
                    lock (links) at.Heard = Environment.TickCount64;
                    if (at.Peer is { } peer) await incoming.WriteAsync((packet.AsSpan(0, length).ToArray(), peer), stop.Token);
                    continue;
                }
                // Anything else is a hello or nothing, and only a hello starts a link.
                if (datagram.Length != HelloSize) continue;
                foreach (var secret in secrets)
                {
                    if (!Open(secret.HelloKey, datagram.Span, packet, out _, out ulong timestamp) || packet[8] is not (Hello or Answer)) continue;
                    ulong id = BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(HeaderSize));
                    // Not our own hello sent back to us, or one we said to ourselves.
                    if (id != ours)
                        lock (links) Greet(socket, links, routing, secret, ours, id, timestamp, packet[8] == Hello, from, stop.Token);
                    break;
                }
            }
        }
        finally
        {
            stop.Cancel();
            Link[] all;
            lock (links) all = [.. links.ById.Values];
            foreach (var link in all) lock (links) Retire(links, link, routing);
            try { await Task.WhenAll([.. tasks, .. all.Select(l => l.Send)]); }
            catch (OperationCanceledException) { }
        }
    }

    // A hello or an answer from an address, under the secret it opened with. Starts the link of
    // its id, moves it to this address, or just shows it is alive. Called with links locked.
    private static void Greet(Socket socket, Links links, Routing routing, Secret secret, ulong ours, ulong id, ulong timestamp,
        bool answer, SocketAddress from, CancellationToken token)
    {
        if (links.ById.TryGetValue(id, out Link? link))
        {
            // Not newer than the last hello, so a replay; or not under the secret the link began with.
            if (timestamp <= link.NewestHello || link.Secret != secret) return;
        }
        else
        {
            if (links.ById.Count >= MaxLinks) return;
            links.ById[id] = link = new Link(id, secret, ours);
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
            Log.Information("UDP link with {Remote} is {State}", Describe(remote), started ? "up" : "at a new address");
        }
        if (started)
        {
            Peer peer = link.Peer = new Peer();
            routing.Add(peer);
            link.Send = SendAsync(socket, peer, link.SendKey, () => link.Remote, token);
        }
        // So that the other side knows the link is up without waiting for mesh traffic.
        // A lost answer is like any lost datagram: the next hello is answered again.
        if (answer)
            try { SendHello(socket, secret, Answer, ours, link.Remote!); }
            catch (SocketException) { }
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
                        Log.Information("UDP link with {Remote} went idle", Describe(link.Remote!));
                        Retire(links, link, routing);
                    }
                    // Forgotten only when a replay of its newest hello is no longer fresh.
                    if (link.Peer is null && !Protocol.Fresh(link.NewestHello, Protocol.Millis))
                        links.ById.Remove(link.Id);
                }
    }

    // Says hello to a host every interval while some link is at its address, and more often
    // until one is.
    private static async Task GreetAsync(Socket socket, Neighbour neighbour, Secret secret, ulong ours, Links links,
        TimeSpan every, CancellationToken token)
    {
        string? failure = null;
        while (true)
        {
            bool linked = false;
            try
            {
                // Resolved again for every hello: the host may have moved.
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(neighbour.Host!, token);
                if (addresses.Length == 0) throw new IOException("Host has no address.");
                var remote = new IPEndPoint(addresses[0], neighbour.Port);
                if (socket.AddressFamily == AddressFamily.InterNetworkV6) remote = new IPEndPoint(remote.Address.MapToIPv6(), remote.Port);
                SendHello(socket, secret, Hello, ours, remote.Serialize());
                lock (links) linked = links.ById.Values.Any(l => l.Peer is not null && l.Remote is { } a && remote.Equals(EndPoint(a)));
                failure = null;
            }
            catch (Exception ex) when (ex is SocketException or IOException && !token.IsCancellationRequested)
            {
                // Logged once, not every few seconds while it lasts.
                if (ex.Message != failure) Log.Warning("UDP hello to {Host}:{Port}: {Message}", neighbour.Host, neighbour.Port, failure = ex.Message);
            }
            await Task.Delay(linked ? every : TimeSpan.FromTicks(Math.Min(every.Ticks, RetryInterval.Ticks)), token);
        }
    }

    private static void SendHello(Socket socket, Secret secret, byte command, ulong ours, SocketAddress to)
    {
        Span<byte> datagram = stackalloc byte[HelloSize];
        Span<byte> id = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(id, ours);
        Seal(secret.HelloKey, Protocol.Packet(command, id), datagram);
        socket.SendTo(datagram, SocketFlags.None, to);
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
    // is a mesh packet or a hello of the present moment.
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
        return Protocol.Fresh(timestamp, Protocol.Millis) && packet[8] <= Answer;
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
                if (packet.Length is < HeaderSize or > MaxDatagram || packet[8] > Protocol.LastCommand) continue;
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

    private static IPEndPoint EndPoint(SocketAddress address) => (IPEndPoint)
        new IPEndPoint(address.Family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0).Create(address);

    private static string Describe(SocketAddress address) => LegacyTcp.Describe(EndPoint(address));
}
