using Serilog;
using Smolmesh.Crypto;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Smolmesh;

// Mesh packets over UDP, one per datagram. A datagram is the packet enciphered with LION under a
// key derived from the shared secret, and nothing else: it is as long as the packet and looks
// like random bytes. There is no handshake, nonce, tag or keepalive.
//
// The timestamp that starts every mesh packet does their work. It makes every packet different.
// It stands in for the tag: a datagram made without the key decrypts to random bytes, whose
// timestamp is not the present moment. And it orders datagrams, so a neighbour is followed to a
// new address only by a datagram newer than any seen from it, which a replayed one is not.
//
// A captured datagram stays valid while its timestamp is fresh, and is accepted again if it is
// replayed after the mesh has forgotten its packet.
internal static class UdpTransport1
{
    // Every node announces its name at least every 10 seconds, so a live neighbour is never this quiet.
    internal static readonly TimeSpan Silence = TimeSpan.FromSeconds(30);
    private const int MaxDatagram = 65507;
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

    // One key is one neighbour of a listener. It has a peer while it is heard from.
    private sealed class Neighbour(byte[][] keys)
    {
        internal readonly byte[] ReceiveKey = keys[0], SendKey = keys[1];
        // Replaced, never changed in place: the send loop reads it without the lock.
        internal volatile SocketAddress? Remote;
        internal ulong Newest;
        internal long Heard;
        internal Peer? Peer;
        internal Task Send = Task.CompletedTask;
    }

    internal static async Task ListenAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        using Socket socket = Bind(config.Port);
        Log.Information("Listening on UDP port {Port}", config.Port);
        await ListenAsync(socket, config.Keys.Select(Encoding.UTF8.GetBytes).ToArray(), routing, incoming, Silence, token);
    }

    internal static async Task ListenAsync(Socket socket, byte[][] keys, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, TimeSpan silence, CancellationToken token)
    {
        Neighbour[] neighbours = keys.Select(k => new Neighbour(DeriveKeys(k))).ToArray();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task expire = ExpireAsync(neighbours, routing, silence, stop.Token);
        try
        {
            byte[] datagram = new byte[MaxDatagram], packet = new byte[MaxDatagram];
            var from = new SocketAddress(socket.AddressFamily);
            while (true)
            {
                int count;
                try { count = await socket.ReceiveFromAsync(datagram, SocketFlags.None, from, stop.Token); }
                // Windows reports the ICMP errors of earlier sends here.
                catch (SocketException) when (!stop.IsCancellationRequested) { continue; }

                // The neighbour at this address first, so that only a new or moved one costs a trial of
                // every key. From any other address a datagram has to be newer than all before it.
                Neighbour? heard = null;
                int length = 0;
                ulong timestamp = 0;
                for (int pass = 0; pass < 2 && heard is null; pass++)
                    foreach (var neighbour in neighbours)
                        if (from.Equals(neighbour.Remote) == (pass == 0)
                            && Open(neighbour.ReceiveKey, datagram.AsSpan(0, count), packet, out length, out timestamp)
                            && (pass == 0 || timestamp > neighbour.Newest))
                        {
                            heard = neighbour;
                            break;
                        }
                if (heard is null) continue;

                Peer peer;
                lock (heard)
                {
                    if (!from.Equals(heard.Remote))
                    {
                        Log.Information("UDP link from {Remote} is {State}", Describe(from), heard.Peer is null ? "up" : "at a new address");
                        heard.Remote = Copy(from);
                    }
                    if (heard.Peer is null)
                    {
                        Neighbour started = heard;
                        peer = started.Peer = new Peer();
                        routing.Add(peer);
                        started.Send = SendAsync(socket, peer, started.SendKey, () => started.Remote, stop.Token);
                    }
                    else peer = heard.Peer;
                    heard.Newest = Math.Max(heard.Newest, timestamp);
                    heard.Heard = Environment.TickCount64;
                }
                await incoming.WriteAsync((packet.AsSpan(0, length).ToArray(), peer), stop.Token);
            }
        }
        finally
        {
            stop.Cancel();
            foreach (var neighbour in neighbours)
                lock (neighbour) Drop(neighbour, routing);
            try { await Task.WhenAll([expire, .. neighbours.Select(n => n.Send)]); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task ExpireAsync(Neighbour[] neighbours, Routing routing, TimeSpan silence, CancellationToken token)
    {
        using var timer = new PeriodicTimer(silence / 4);
        while (await timer.WaitForNextTickAsync(token))
            foreach (var neighbour in neighbours)
                lock (neighbour)
                {
                    if (neighbour.Peer is null || Environment.TickCount64 - neighbour.Heard < silence.TotalMilliseconds) continue;
                    Log.Information("UDP link from {Remote} went silent", Describe(neighbour.Remote!));
                    Drop(neighbour, routing);
                }
    }

    // Forgets the address too, so the neighbour comes back only with a datagram newer than its last.
    private static void Drop(Neighbour neighbour, Routing routing)
    {
        if (neighbour.Peer is not null) routing.Remove(neighbour.Peer);
        neighbour.Peer = null;
        neighbour.Remote = null;
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
    // the start, since the listener learns of us from our first datagram. Returns whether the
    // listener was ever heard.
    internal static async Task<bool> RunAsync(IPEndPoint remote, byte[][] keys, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, TimeSpan silence, CancellationToken token)
    {
        using Socket socket = Bind(0);
        if (socket.AddressFamily == AddressFamily.InterNetworkV6) remote = new IPEndPoint(remote.Address.MapToIPv6(), remote.Port);
        SocketAddress address = remote.Serialize();
        var peer = new Peer();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        routing.Add(peer);
        Task send = SendAsync(socket, peer, keys[0], () => address, stop.Token);
        bool heard = false;
        try
        {
            byte[] datagram = new byte[MaxDatagram], packet = new byte[MaxDatagram];
            var from = new SocketAddress(socket.AddressFamily);
            stop.CancelAfter(silence);
            while (true)
            {
                int count;
                try { count = await socket.ReceiveFromAsync(datagram, SocketFlags.None, from, stop.Token); }
                catch (SocketException) when (!stop.IsCancellationRequested) { continue; }
                // Whoever holds the key is the listener, whatever address the datagram came from.
                if (!Open(keys[1], datagram.AsSpan(0, count), packet, out int length, out _)) continue;
                if (!heard) Log.Information("UDP link to {Remote} is up", LegacyTcp.Describe(remote));
                heard = true;
                stop.CancelAfter(silence);
                await incoming.WriteAsync((packet.AsSpan(0, length).ToArray(), peer), stop.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return heard; }
        finally
        {
            stop.Cancel();
            routing.Remove(peer);
            try { await send; }
            catch (OperationCanceledException) { }
        }
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
        return Protocol.Fresh(timestamp, Protocol.Millis) && packet[8] <= Protocol.LastCommand;
    }

    // Enciphers and sends every queued packet until the peer is removed from routing, or its
    // neighbour has no address any more.
    private static async Task SendAsync(Socket socket, Peer peer, byte[] key, Func<SocketAddress?> remote, CancellationToken token)
    {
        byte[] datagram = new byte[MaxDatagram];
        await foreach (byte[] packet in peer.Outgoing.Reader.ReadAllAsync(token))
        {
            // Nothing the other side would not take for a mesh packet is worth sending.
            if (packet.Length is < HeaderSize or > MaxDatagram || packet[8] > Protocol.LastCommand) continue;
            if (remote() is not { } address) return;
            // Packets are shared between peers, so each is enciphered in a copy.
            int length = Seal(key, packet, datagram);
            try { await socket.SendToAsync(datagram.AsMemory(0, length), SocketFlags.None, address, token); }
            // A datagram that cannot be sent is one more that was lost.
            catch (SocketException) { }
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
