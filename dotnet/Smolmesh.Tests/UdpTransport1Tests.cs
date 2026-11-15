using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Smolmesh;
using Smolmesh.Crypto;
using Xunit;

public class UdpTransport1Tests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly byte[] Secret = "secret"u8.ToArray();

    private static Channel<(byte[] Packet, Peer Peer)> Incoming() => Channel.CreateUnbounded<(byte[] Packet, Peer Peer)>();

    private static IPEndPoint Loopback(Socket socket) => new(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Port);

    // A datagram as the connecting side sends it, or as the listening side does.
    private static byte[] Datagram(byte[] packet, byte[]? secret = null, bool fromListener = false)
    {
        byte[] datagram = new byte[Math.Max(packet.Length, Lion.MinSize)];
        int length = UdpTransport1.Seal(UdpTransport1.DeriveKeys(secret ?? Secret)[fromListener ? 1 : 0], packet, datagram);
        Assert.Equal(datagram.Length, length);
        return datagram;
    }

    private static byte[] HelloDatagram(ulong id, ulong? timestamp = null, byte[]? secret = null) =>
        Datagram(Protocol.Packet(UdpTransport1.Hello, BitConverter.GetBytes(id), timestamp), secret);

    // What is in a datagram from the connecting side, before the transport looks at it.
    private static byte[] Deciphered(byte[] datagram)
    {
        byte[] block = datagram.ToArray();
        Lion.Decrypt(UdpTransport1.DeriveKeys(Secret)[0], block);
        return block;
    }

    private static byte[] Enciphered(byte[] block)
    {
        byte[] datagram = block.ToArray();
        Lion.Encrypt(UdpTransport1.DeriveKeys(Secret)[0], datagram);
        return datagram;
    }

    private static UdpClient Client(Socket listener)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(Loopback(listener));
        return client;
    }

    [Fact]
    public async Task CarriesPacketsBothWaysBetweenListenerAndConnector()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var listenerRouting = new Routing();
        var connectorRouting = new Routing();
        var listenerIncoming = Incoming();
        var connectorIncoming = Incoming();
        Task listener = UdpTransport1.ListenAsync(socket, ["wrong"u8.ToArray(), Secret], listenerRouting, listenerIncoming.Writer, Long, stop.Token);
        Task connector = UdpTransport1.RunAsync(Loopback(socket), UdpTransport1.DeriveKeys(Secret), connectorRouting,
            connectorIncoming.Writer, Long, stop.Token);
        try
        {
            // The connector's peer exists from the start, so its first broadcast already goes out.
            byte[] first = Protocol.Packet(0, "connector"u8);
            connectorRouting.Broadcast(first);
            var received = await listenerIncoming.Reader.ReadAsync(stop.Token);
            Assert.Equal(first, received.Packet);
            // Payloads of packets that are filled up (9 to 16 bytes with the header) and of ones that
            // are not, of a full-size packet, and the largest.
            foreach (int length in new[] { 0, 1, 4, 7, 8, 9, 1420, 65507 - 9 })
            {
                byte[] packet = Protocol.Packet(3, new byte[length].Select((_, i) => (byte)i).ToArray());
                received.Peer.Send(packet);
                Assert.Equal(packet, (await connectorIncoming.Reader.ReadAsync(stop.Token)).Packet);
                connectorRouting.Broadcast(packet);
                var echo = await listenerIncoming.Reader.ReadAsync(stop.Token);
                Assert.Equal(packet, echo.Packet);
                Assert.Same(received.Peer, echo.Peer);
            }
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(listener, connector);
        }
    }

    [Fact]
    public async Task RunningMeshNodeRoutesOverUdp()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        var config = new Configuration
        {
            NodeName = "node-under-test", ControlPort = 0,
            Transports = [new() { Type = "udp1_listen", Port = port, Keys = ["mesh-test"] }]
        };
        Task node = new MeshNode(config).RunAsync(stop.Token);
        var routing = new Routing();
        var incoming = Incoming();
        Task connector = UdpTransport1.RunAsync(new IPEndPoint(IPAddress.Loopback, port), UdpTransport1.DeriveKeys("mesh-test"u8.ToArray()),
            routing, incoming.Writer, Long, stop.Token);
        try
        {
            // The node may not be listening yet, so announce until it floods the announcement back.
            byte[] announcement = Protocol.Packet(1, IPAddress.Parse("fd00::1234").GetAddressBytes());
            var received = incoming.Reader.ReadAsync(stop.Token).AsTask();
            while (!received.IsCompleted)
            {
                routing.Broadcast(announcement);
                await Task.Delay(10, stop.Token);
            }
            // It has learned the route from that announcement, so data for the address comes back.
            byte[] ip = new byte[40];
            ip[0] = 0x60;
            IPAddress.Parse("fd00::1234").GetAddressBytes().CopyTo(ip, 24);
            byte[] data = Protocol.Packet(3, ip);
            routing.Broadcast(data);
            for (var item = await received; !item.Packet.SequenceEqual(data); item = await incoming.Reader.ReadAsync(stop.Token)) { }
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node, connector);
        }
    }

    [Fact]
    public async Task DatagramsAreExactlyAsLongAsThePacketAndUnreadable()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var wire = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var routing = new Routing();
        Task connector = UdpTransport1.RunAsync((IPEndPoint)wire.Client.LocalEndPoint!, UdpTransport1.DeriveKeys(Secret), routing,
            Incoming().Writer, Long, stop.Token);
        try
        {
            // A connector says hello first: a 17-byte packet of its own command and a link id.
            byte[] hello = (await wire.ReceiveAsync(stop.Token)).Buffer;
            byte[] helloPacket = new byte[17];
            Assert.True(UdpTransport1.Open(UdpTransport1.DeriveKeys(Secret)[0], hello, helloPacket, out int helloLength, out _));
            Assert.Equal(17, helloLength);
            Assert.Equal(UdpTransport1.Hello, helloPacket[8]);

            foreach (byte[] packet in new[] { Protocol.Packet(0, "ch01"u8), Protocol.Packet(0, "eightlen"u8), Protocol.Packet(3, new byte[1420]) })
            {
                routing.Broadcast(packet);
                byte[] datagram = (await wire.ReceiveAsync(stop.Token)).Buffer;
                // Only a packet under LION's 17 bytes grows, and only to 17.
                Assert.Equal(Math.Max(packet.Length, 17), datagram.Length);
                // Not the packet with something XORed over part of it: no eighth of it survives.
                for (int offset = 0; offset + 8 <= packet.Length; offset += 8)
                    Assert.False(datagram.AsSpan(offset, 8).SequenceEqual(packet.AsSpan(offset, 8)));
                Assert.Equal(Datagram(packet), datagram);
                byte[] opened = new byte[datagram.Length];
                Assert.True(UdpTransport1.Open(UdpTransport1.DeriveKeys(Secret)[0], datagram, opened, out int length, out ulong timestamp));
                Assert.Equal(packet, opened[..length]);
                Assert.Equal(BitConverter.ToUInt64(packet), timestamp);
            }
            // A filled-up packet is the packet with the flag in its command, zeros, and their count.
            byte[] filled = Deciphered(Datagram(Protocol.Packet(2, [192, 0, 2, 7], 5)));
            Assert.Equal((byte[])[5, 0, 0, 0, 0, 0, 0, 0, 0x82, 192, 0, 2, 7, 0, 0, 0, 4], filled);
            // The two directions use different keys: a datagram sent back is not accepted.
            byte[][] keys = UdpTransport1.DeriveKeys(Secret);
            Assert.NotEqual(keys[0], keys[1]);
            Assert.NotEqual(keys[0], UdpTransport1.DeriveKeys("other"u8.ToArray())[0]);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(connector);
        }
    }

    [Fact]
    public async Task ListenerIgnoresDatagramsThatAreNotPresentMeshPackets()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task listener = UdpTransport1.ListenAsync(socket, [Secret], new Routing(), incoming.Writer, Long, stop.Token);
        try
        {
            using UdpClient client = Client(socket);
            await client.SendAsync(HelloDatagram(1), stop.Token);
            byte[] good = Protocol.Packet(0, "node"u8);
            byte[] changed = Datagram(good);
            changed[^1] ^= 1;
            foreach (byte[] datagram in new[]
            {
                good,                                                                   // not enciphered
                Datagram(good, "other"u8.ToArray()),                                    // another key
                changed,                                                                // modified in transit
                Datagram(Protocol.Packet(0, "node"u8, Protocol.Millis - 181_000)),      // too old
                Datagram(Protocol.Packet(0, "node"u8, Protocol.Millis + 181_000)),      // from the future
                Enciphered(Protocol.Packet(7, "a longer name"u8)),                      // no such command
                new byte[16],                                                           // too short for LION
                Enciphered(Flagged([.. Protocol.Packet(0, "node"u8), 0, 0, 0, 0])),     // no count
                Enciphered(Flagged([.. Protocol.Packet(0, "node"u8), 0, 0, 0, 9])),     // count reaches into the header
                Enciphered(Flagged([.. Protocol.Packet(0, "node"u8), 0, 1, 0, 4])),     // filling is not zeros
                Enciphered(Flagged([.. Protocol.Packet(0, "a longer name"u8), 0, 2])),  // flagged, but longer than a filled-up packet
                new byte[1200],
            })
                await client.SendAsync(datagram, stop.Token);
            await client.SendAsync(Datagram(good), stop.Token);
            // Loopback keeps the order, so the first packet to arrive is the only valid one.
            Assert.Equal(good, (await incoming.Reader.ReadAsync(stop.Token)).Packet);
            Assert.False(incoming.Reader.TryRead(out _));

            // Without the flag, bytes that look like filling are part of a 17-byte packet.
            byte[] unflagged = [.. Protocol.Packet(0, "node"u8), 0, 0, 0, 4];
            await client.SendAsync(Enciphered(unflagged), stop.Token);
            Assert.Equal(unflagged, (await incoming.Reader.ReadAsync(stop.Token)).Packet);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(listener);
        }
    }

    private static byte[] Flagged(byte[] block)
    {
        block[8] |= 0x80;
        return block;
    }

    [Fact]
    public async Task NeighboursSharingAKeyGetLinksOfTheirOwn()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task listener = UdpTransport1.ListenAsync(socket, [Secret], new Routing(), incoming.Writer, Long, stop.Token);
        try
        {
            using UdpClient one = Client(socket), two = Client(socket);
            byte[] reply = Protocol.Packet(3, new byte[40]);
            byte[] replyDatagram = Datagram(reply, fromListener: true);

            await one.SendAsync(HelloDatagram(1), stop.Token);
            await two.SendAsync(HelloDatagram(2), stop.Token);
            // Each is answered with a hello of its own, at its own address.
            byte[] answer = (await one.ReceiveAsync(stop.Token)).Buffer;
            Assert.Equal(17, answer.Length);
            Assert.Equal(17, (await two.ReceiveAsync(stop.Token)).Buffer.Length);

            await one.SendAsync(Datagram(Protocol.Packet(0, "one"u8)), stop.Token);
            Peer first = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            await two.SendAsync(Datagram(Protocol.Packet(0, "two"u8)), stop.Token);
            Peer second = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            Assert.NotSame(first, second);
            // Hellos are the transport's and do not reach the mesh.
            Assert.False(incoming.Reader.TryRead(out _));

            first.Send(reply);
            second.Send(reply);
            Assert.Equal(replyDatagram, (await one.ReceiveAsync(stop.Token)).Buffer);
            Assert.Equal(replyDatagram, (await two.ReceiveAsync(stop.Token)).Buffer);
            Assert.Equal(0, one.Available);
            Assert.Equal(0, two.Available);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(listener);
        }
    }

    [Fact]
    public async Task OnlyANewerHelloStartsOrMovesALink()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task listener = UdpTransport1.ListenAsync(socket, [Secret], new Routing(), incoming.Writer, Long, stop.Token);
        try
        {
            using UdpClient home = Client(socket), moved = Client(socket), attacker = Client(socket);
            ulong now = Protocol.Millis;
            byte[] hello = HelloDatagram(7, now), newer = HelloDatagram(7, now + 1);
            byte[] reply = Protocol.Packet(3, new byte[40]);
            byte[] replyDatagram = Datagram(reply, fromListener: true);

            // Data from an address with no link goes nowhere.
            await attacker.SendAsync(Datagram(Protocol.Packet(0, "node"u8)), stop.Token);
            await home.SendAsync(hello, stop.Token);
            await home.ReceiveAsync(stop.Token);
            await home.SendAsync(Datagram(Protocol.Packet(0, "node"u8, now)), stop.Token);
            Peer peer = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            peer.Send(reply);
            Assert.Equal(replyDatagram, (await home.ReceiveAsync(stop.Token)).Buffer);

            // A newer hello from another address moves the link, and the peer stays the same.
            await moved.SendAsync(newer, stop.Token);
            await moved.ReceiveAsync(stop.Token);
            await moved.SendAsync(Datagram(Protocol.Packet(0, "node"u8, now + 2)), stop.Token);
            Assert.Same(peer, (await incoming.Reader.ReadAsync(stop.Token)).Peer);
            peer.Send(reply);
            Assert.Equal(replyDatagram, (await moved.ReceiveAsync(stop.Token)).Buffer);

            // Replayed hellos and data, from a third address, neither move the link nor get through.
            await attacker.SendAsync(hello, stop.Token);
            await attacker.SendAsync(newer, stop.Token);
            await attacker.SendAsync(Datagram(Protocol.Packet(0, "node"u8, now + 2)), stop.Token);
            await moved.SendAsync(Datagram(Protocol.Packet(0, "node"u8, now + 3)), stop.Token);
            Assert.Equal(now + 3, BitConverter.ToUInt64((await incoming.Reader.ReadAsync(stop.Token)).Packet));
            Assert.False(incoming.Reader.TryRead(out _));
            peer.Send(reply);
            Assert.Equal(replyDatagram, (await moved.ReceiveAsync(stop.Token)).Buffer);
            Assert.Equal(0, attacker.Available);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(listener);
        }
    }

    [Fact]
    public async Task SilentLinksAreDropped()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var silence = TimeSpan.FromMilliseconds(300);
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task listener = UdpTransport1.ListenAsync(socket, [Secret], new Routing(), incoming.Writer, silence, stop.Token);
        try
        {
            using UdpClient client = Client(socket);
            ulong now = Protocol.Millis;
            byte[] hello = HelloDatagram(9, now);
            await client.SendAsync(hello, stop.Token);
            await client.SendAsync(Datagram(Protocol.Packet(0, "node"u8, now)), stop.Token);
            Peer peer = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            // Removing a peer from routing completes its queue.
            await peer.Outgoing.Reader.Completion.WaitAsync(stop.Token);

            // The same hello does not bring the link back, even though its link is forgotten; a newer one does, as a new peer.
            await client.SendAsync(hello, stop.Token);
            await client.SendAsync(HelloDatagram(9, now + 1), stop.Token);
            await client.SendAsync(Datagram(Protocol.Packet(0, "node"u8, now + 2)), stop.Token);
            var again = await incoming.Reader.ReadAsync(stop.Token);
            Assert.Equal(now + 2, BitConverter.ToUInt64(again.Packet));
            Assert.NotSame(peer, again.Peer);

            // A connector that never hears its listener gives up, and reports that it heard nothing.
            using var deaf = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var routing = new Routing();
            Assert.False(await UdpTransport1.RunAsync((IPEndPoint)deaf.Client.LocalEndPoint!, UdpTransport1.DeriveKeys(Secret), routing,
                Incoming().Writer, silence, stop.Token));
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(listener);
        }
    }
}
