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

    private static int Port(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    private static IPEndPoint Loopback(Socket socket) => new(IPAddress.Loopback, Port(socket));

    private static UdpTransport1.Neighbour Host(int port, byte[]? secret = null) => new(secret ?? Secret, "127.0.0.1", port);

    private static UdpTransport1.Neighbour Anyone(byte[]? secret = null) => new(secret ?? Secret);

    private static Task Node(Socket socket, UdpTransport1.Neighbour[] neighbours, Routing routing, Channel<(byte[] Packet, Peer Peer)> incoming,
        CancellationToken token, TimeSpan? idle = null, TimeSpan? helloEvery = null) =>
        UdpTransport1.RunAsync(socket, neighbours, routing, incoming.Writer, idle ?? Long, helloEvery ?? Long, token);

    private static byte[] Datagram(byte[] key, byte[] packet)
    {
        byte[] datagram = new byte[Math.Max(packet.Length, Lion.MinSize)];
        int length = UdpTransport1.Seal(key, packet, datagram);
        Assert.Equal(datagram.Length, length);
        return datagram;
    }

    private static byte[] HelloDatagram(ulong id, ulong? timestamp = null, byte[]? secret = null, byte command = UdpTransport1.Hello) =>
        Datagram(UdpTransport1.HelloKey(secret ?? Secret), Protocol.Packet(command, BitConverter.GetBytes(id), timestamp));

    // The link id in a hello or an answer, which has to be one.
    private static ulong HelloId(byte[] datagram, byte command, byte[]? secret = null)
    {
        byte[] packet = new byte[datagram.Length];
        Assert.True(UdpTransport1.Open(UdpTransport1.HelloKey(secret ?? Secret), datagram, packet, out int length, out _));
        Assert.Equal(UdpTransport1.HelloSize, length);
        Assert.Equal(command, packet[8]);
        return BitConverter.ToUInt64(packet, 9);
    }

    private static byte[] Enciphered(byte[] key, byte[] block)
    {
        byte[] datagram = block.ToArray();
        Lion.Encrypt(key, datagram);
        return datagram;
    }

    private static UdpClient Client(Socket node)
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.Connect(Loopback(node));
        return client;
    }

    // Says hello to a node as the neighbour with this id, and gives the id of the node's answer.
    private static async Task<ulong> Greet(UdpClient client, ulong id, CancellationToken token, ulong? timestamp = null)
    {
        await client.SendAsync(HelloDatagram(id, timestamp), token);
        return HelloId((await client.ReceiveAsync(token)).Buffer, UdpTransport1.Answer);
    }

    [Fact]
    public async Task NodesThatKnowEachOtherShareOneLink()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket a = UdpTransport1.Bind(0), b = UdpTransport1.Bind(0);
        Routing aRouting = new(), bRouting = new();
        var aIncoming = Incoming();
        var bIncoming = Incoming();
        // Each says hello to the other, and does so again and again.
        var every = TimeSpan.FromMilliseconds(20);
        Task aNode = Node(a, [Host(Port(b))], aRouting, aIncoming, stop.Token, helloEvery: every);
        Task bNode = Node(b, [Host(Port(a))], bRouting, bIncoming, stop.Token, helloEvery: every);
        try
        {
            byte[] first = Protocol.Packet(0, "a"u8);
            var received = bIncoming.Reader.ReadAsync(stop.Token).AsTask();
            while (!received.IsCompleted)
            {
                aRouting.Broadcast(first);
                await Task.Delay(10, stop.Token);
            }
            await Task.Delay(200, stop.Token);
            while (bIncoming.Reader.TryRead(out _)) { }

            // With two links, every broadcast would arrive twice.
            foreach (int length in new[] { 0, 1, 4, 7, 8, 9, 1420, 65507 - 9 })
            {
                byte[] packet = Protocol.Packet(3, new byte[length].Select((_, i) => (byte)i).ToArray());
                aRouting.Broadcast(packet);
                var there = await bIncoming.Reader.ReadAsync(stop.Token);
                Assert.Equal(packet, there.Packet);
                Assert.Same((await received).Peer, there.Peer);
                bRouting.Broadcast(packet);
                Assert.Equal(packet, (await aIncoming.Reader.ReadAsync(stop.Token)).Packet);
            }
            await Task.Delay(100, stop.Token);
            Assert.False(aIncoming.Reader.TryRead(out _));
            Assert.False(bIncoming.Reader.TryRead(out _));
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(aNode, bNode);
        }
    }

    [Fact]
    public async Task ANodeThatKnowsNoHostIsReachedFromAnyPort()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket server = UdpTransport1.Bind(0), laptop = UdpTransport1.Bind(0);
        Routing serverRouting = new(), laptopRouting = new();
        var serverIncoming = Incoming();
        var laptopIncoming = Incoming();
        Task serverNode = Node(server, [Anyone("wrong"u8.ToArray()), Anyone()], serverRouting, serverIncoming, stop.Token);
        Task laptopNode = Node(laptop, [Host(Port(server))], laptopRouting, laptopIncoming, stop.Token);
        try
        {
            // The answer to the first hello brings the link up on both sides.
            byte[] first = Protocol.Packet(0, "laptop"u8);
            var received = serverIncoming.Reader.ReadAsync(stop.Token).AsTask();
            while (!received.IsCompleted)
            {
                laptopRouting.Broadcast(first);
                await Task.Delay(10, stop.Token);
            }
            byte[] packet = Protocol.Packet(3, new byte[100]);
            (await received).Peer.Send(packet);
            Assert.Equal(packet, (await laptopIncoming.Reader.ReadAsync(stop.Token)).Packet);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(serverNode, laptopNode);
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
            Transports = [new() { Type = "udp1", Port = port, Peers = [new() { Key = "mesh-test" }] }]
        };
        Task node = new MeshNode(config).RunAsync(stop.Token);
        using Socket socket = UdpTransport1.Bind(0);
        var routing = new Routing();
        var incoming = Incoming();
        // The node may not be listening yet, so hellos go out often.
        Task neighbour = Node(socket, [Host(port, "mesh-test"u8.ToArray())], routing, incoming, stop.Token,
            helloEvery: TimeSpan.FromMilliseconds(20));
        try
        {
            // Announce until it floods the announcement back.
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
            await TestSupport.IgnoreCancellation(node, neighbour);
        }
    }

    [Fact]
    public async Task DatagramsAreExactlyAsLongAsThePacketAndUnreadable()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        using var wire = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var routing = new Routing();
        Task node = Node(socket, [Host(((IPEndPoint)wire.Client.LocalEndPoint!).Port)], routing, Incoming(), stop.Token);
        try
        {
            // A node says hello first to a host it knows: a 17-byte packet of its own command and a link id.
            var hello = await wire.ReceiveAsync(stop.Token);
            ulong nodeId = HelloId(hello.Buffer, UdpTransport1.Hello);
            await wire.SendAsync(HelloDatagram(1, command: UdpTransport1.Answer), hello.RemoteEndPoint, stop.Token);
            byte[] key = UdpTransport1.DataKey(Secret, nodeId, 1);
            // The link is up once the answer is in, and its peer gets broadcasts from then on.
            while (wire.Available == 0)
            {
                routing.Broadcast(Protocol.Packet(0, "up"u8));
                await Task.Delay(10, stop.Token);
            }
            await Task.Delay(50, stop.Token);
            while (wire.Available > 0) await wire.ReceiveAsync(stop.Token);

            foreach (byte[] packet in new[] { Protocol.Packet(0, "ch01"u8), Protocol.Packet(0, "eightlen"u8), Protocol.Packet(3, new byte[1420]) })
            {
                routing.Broadcast(packet);
                byte[] datagram = (await wire.ReceiveAsync(stop.Token)).Buffer;
                // Only a packet under LION's 17 bytes grows, and only to 17.
                Assert.Equal(Math.Max(packet.Length, 17), datagram.Length);
                // Not the packet with something XORed over part of it: no eighth of it survives.
                for (int offset = 0; offset + 8 <= packet.Length; offset += 8)
                    Assert.False(datagram.AsSpan(offset, 8).SequenceEqual(packet.AsSpan(offset, 8)));
                Assert.Equal(Datagram(key, packet), datagram);
                byte[] opened = new byte[datagram.Length];
                Assert.True(UdpTransport1.Open(key, datagram, opened, out int length, out ulong timestamp));
                Assert.Equal(packet, opened[..length]);
                Assert.Equal(BitConverter.ToUInt64(packet), timestamp);
            }
            // A filled-up packet is the packet with the flag in its command, zeros, and their count.
            byte[] filled = Datagram(key, Protocol.Packet(2, [192, 0, 2, 7], 5));
            Lion.Decrypt(key, filled);
            Assert.Equal((byte[])[5, 0, 0, 0, 0, 0, 0, 0, 0x82, 192, 0, 2, 7, 0, 0, 0, 4], filled);
            // Each direction of each link, and the hellos of each secret, have a key of their own.
            byte[][] keys = [key, UdpTransport1.DataKey(Secret, 1, nodeId), UdpTransport1.DataKey(Secret, nodeId, 2),
                UdpTransport1.DataKey("other"u8.ToArray(), nodeId, 1), UdpTransport1.HelloKey(Secret), UdpTransport1.HelloKey("other"u8.ToArray())];
            Assert.Equal(keys.Length, keys.Select(Convert.ToHexString).Distinct().Count());
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
        }
    }

    [Fact]
    public async Task IgnoresDatagramsThatAreNotPresentMeshPackets()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task node = Node(socket, [Anyone()], new Routing(), incoming, stop.Token);
        try
        {
            using UdpClient client = Client(socket);
            ulong nodeId = await Greet(client, 1, stop.Token);
            byte[] key = UdpTransport1.DataKey(Secret, 1, nodeId);
            byte[] good = Protocol.Packet(0, "node"u8);
            byte[] changed = Datagram(key, good);
            changed[^1] ^= 1;
            foreach (byte[] datagram in new[]
            {
                good,                                                                       // not enciphered
                Datagram(UdpTransport1.DataKey("other"u8.ToArray(), 1, nodeId), good),      // another secret
                Datagram(UdpTransport1.DataKey(Secret, nodeId, 1), good),                   // the other direction
                Datagram(UdpTransport1.DataKey(Secret, 2, nodeId), good),                   // another link
                Datagram(UdpTransport1.HelloKey(Secret), good),                             // the hello key
                changed,                                                                    // modified in transit
                Datagram(key, Protocol.Packet(0, "node"u8, Protocol.Millis - 181_000)),     // too old
                Datagram(key, Protocol.Packet(0, "node"u8, Protocol.Millis + 181_000)),     // from the future
                Enciphered(key, Protocol.Packet(8, "a longer name"u8)),                     // no such command
                Datagram(key, Protocol.Packet(UdpTransport1.Hello, BitConverter.GetBytes(3ul))), // a hello, but not under the hello key
                new byte[16],                                                               // too short for LION
                Enciphered(key, Flagged([.. Protocol.Packet(0, "node"u8), 0, 0, 0, 0])),    // no count
                Enciphered(key, Flagged([.. Protocol.Packet(0, "node"u8), 0, 0, 0, 9])),    // count reaches into the header
                Enciphered(key, Flagged([.. Protocol.Packet(0, "node"u8), 0, 1, 0, 4])),    // filling is not zeros
                Enciphered(key, Flagged([.. Protocol.Packet(0, "a longer name"u8), 0, 2])), // flagged, but longer than a filled-up packet
                new byte[1200],
            })
                await client.SendAsync(datagram, stop.Token);
            await client.SendAsync(Datagram(key, good), stop.Token);
            // Loopback keeps the order, so the first packet to arrive is the only valid one.
            Assert.Equal(good, (await incoming.Reader.ReadAsync(stop.Token)).Packet);
            Assert.False(incoming.Reader.TryRead(out _));
            Assert.Equal(0, client.Available);

            // Without the flag, bytes that look like filling are part of a 17-byte packet.
            byte[] unflagged = [.. Protocol.Packet(0, "node"u8), 0, 0, 0, 4];
            await client.SendAsync(Enciphered(key, unflagged), stop.Token);
            Assert.Equal(unflagged, (await incoming.Reader.ReadAsync(stop.Token)).Packet);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
        }
    }

    private static byte[] Flagged(byte[] block)
    {
        block[8] |= 0x80;
        return block;
    }

    [Fact]
    public async Task OwnHellosSentBackAreIgnored()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        using var wire = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var incoming = Incoming();
        // It knows itself as a host too, so its first hello also goes to itself.
        Task node = Node(socket, [Host(((IPEndPoint)wire.Client.LocalEndPoint!).Port), Host(Port(socket))], new Routing(), incoming, stop.Token);
        try
        {
            var hello = await wire.ReceiveAsync(stop.Token);
            ulong nodeId = HelloId(hello.Buffer, UdpTransport1.Hello);
            // Sent back, it is not answered; a hello of another id is, so loopback order shows the first was not.
            await wire.SendAsync(hello.Buffer, hello.RemoteEndPoint, stop.Token);
            await wire.SendAsync(HelloDatagram(1), hello.RemoteEndPoint, stop.Token);
            var answer = await wire.ReceiveAsync(stop.Token);
            Assert.Equal(nodeId, HelloId(answer.Buffer, UdpTransport1.Answer));
            Assert.Equal(0, wire.Available);
            // Data under its own sending key, sent back, is not taken for the neighbour's.
            await wire.SendAsync(Datagram(UdpTransport1.DataKey(Secret, nodeId, 1), Protocol.Packet(0, "echo"u8)), hello.RemoteEndPoint, stop.Token);
            byte[] good = Protocol.Packet(0, "node"u8);
            await wire.SendAsync(Datagram(UdpTransport1.DataKey(Secret, 1, nodeId), good), hello.RemoteEndPoint, stop.Token);
            Assert.Equal(good, (await incoming.Reader.ReadAsync(stop.Token)).Packet);
            Assert.False(incoming.Reader.TryRead(out _));
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
        }
    }

    [Fact]
    public async Task NeighboursSharingAKeyGetLinksOfTheirOwn()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task node = Node(socket, [Anyone()], new Routing(), incoming, stop.Token);
        try
        {
            using UdpClient one = Client(socket), two = Client(socket);
            byte[] reply = Protocol.Packet(3, new byte[40]);

            // Each is answered at its own address, by the node's one id.
            ulong nodeId = await Greet(one, 1, stop.Token);
            Assert.Equal(nodeId, await Greet(two, 2, stop.Token));

            await one.SendAsync(Datagram(UdpTransport1.DataKey(Secret, 1, nodeId), Protocol.Packet(0, "one"u8)), stop.Token);
            Peer first = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            await two.SendAsync(Datagram(UdpTransport1.DataKey(Secret, 2, nodeId), Protocol.Packet(0, "two"u8)), stop.Token);
            Peer second = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            Assert.NotSame(first, second);
            // Hellos are the transport's and do not reach the mesh.
            Assert.False(incoming.Reader.TryRead(out _));

            first.Send(reply);
            second.Send(reply);
            Assert.Equal(Datagram(UdpTransport1.DataKey(Secret, nodeId, 1), reply), (await one.ReceiveAsync(stop.Token)).Buffer);
            Assert.Equal(Datagram(UdpTransport1.DataKey(Secret, nodeId, 2), reply), (await two.ReceiveAsync(stop.Token)).Buffer);
            Assert.Equal(0, one.Available);
            Assert.Equal(0, two.Available);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
        }
    }

    [Fact]
    public async Task OnlyANewerHelloStartsOrMovesALink()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task node = Node(socket, [Anyone()], new Routing(), incoming, stop.Token);
        try
        {
            using UdpClient home = Client(socket), moved = Client(socket), attacker = Client(socket);
            ulong now = Protocol.Millis;
            byte[] hello = HelloDatagram(7, now), newer = HelloDatagram(7, now + 1);
            byte[] reply = Protocol.Packet(3, new byte[40]);

            await home.SendAsync(hello, stop.Token);
            ulong nodeId = HelloId((await home.ReceiveAsync(stop.Token)).Buffer, UdpTransport1.Answer);
            byte[] key = UdpTransport1.DataKey(Secret, 7, nodeId);
            byte[] replyDatagram = Datagram(UdpTransport1.DataKey(Secret, nodeId, 7), reply);
            // Data from an address with no link goes nowhere.
            await attacker.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8)), stop.Token);
            await home.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8, now)), stop.Token);
            var first = await incoming.Reader.ReadAsync(stop.Token);
            Assert.Equal(now, BitConverter.ToUInt64(first.Packet));
            Peer peer = first.Peer;
            peer.Send(reply);
            Assert.Equal(replyDatagram, (await home.ReceiveAsync(stop.Token)).Buffer);

            // A newer hello from another address moves the link, and the peer stays the same.
            await moved.SendAsync(newer, stop.Token);
            await moved.ReceiveAsync(stop.Token);
            await moved.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8, now + 2)), stop.Token);
            Assert.Same(peer, (await incoming.Reader.ReadAsync(stop.Token)).Peer);
            peer.Send(reply);
            Assert.Equal(replyDatagram, (await moved.ReceiveAsync(stop.Token)).Buffer);

            // Replayed hellos and data, from a third address, neither move the link nor get through.
            await attacker.SendAsync(hello, stop.Token);
            await attacker.SendAsync(newer, stop.Token);
            await attacker.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8, now + 2)), stop.Token);
            await moved.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8, now + 3)), stop.Token);
            Assert.Equal(now + 3, BitConverter.ToUInt64((await incoming.Reader.ReadAsync(stop.Token)).Packet));
            Assert.False(incoming.Reader.TryRead(out _));
            peer.Send(reply);
            Assert.Equal(replyDatagram, (await moved.ReceiveAsync(stop.Token)).Buffer);
            Assert.Equal(0, attacker.Available);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
        }
    }

    [Fact]
    public async Task SilentLinksAreDropped()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var silence = TimeSpan.FromMilliseconds(300);
        using Socket socket = UdpTransport1.Bind(0);
        var incoming = Incoming();
        Task node = Node(socket, [Anyone()], new Routing(), incoming, stop.Token, idle: silence);
        try
        {
            using UdpClient client = Client(socket);
            ulong now = Protocol.Millis;
            byte[] hello = HelloDatagram(9, now);
            ulong nodeId = await Greet(client, 9, stop.Token, now);
            byte[] key = UdpTransport1.DataKey(Secret, 9, nodeId);
            await client.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8, now)), stop.Token);
            Peer peer = (await incoming.Reader.ReadAsync(stop.Token)).Peer;
            // Removing a peer from routing completes its queue.
            await peer.Outgoing.Reader.Completion.WaitAsync(stop.Token);

            // The same hello does not bring the link back, even though its link is forgotten; a newer one does, as a new peer.
            await client.SendAsync(hello, stop.Token);
            await client.SendAsync(HelloDatagram(9, now + 1), stop.Token);
            await client.SendAsync(Datagram(key, Protocol.Packet(0, "node"u8, now + 2)), stop.Token);
            var again = await incoming.Reader.ReadAsync(stop.Token);
            Assert.Equal(now + 2, BitConverter.ToUInt64(again.Packet));
            Assert.NotSame(peer, again.Peer);
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
        }
    }
}
