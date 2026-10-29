using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Smolmesh;
using Xunit;

public class MeshNodeTests
{
    [Fact]
    public void ConstructorValidatesConfiguration()
    {
        Assert.Throws<InvalidDataException>(() => new MeshNode(new Configuration()));
        Assert.Throws<InvalidDataException>(() => new MeshNode(new Configuration { NodeName = "test", IPv4Addresses = ["not-an-ip"] }));
        Assert.Throws<InvalidDataException>(() => new MeshNode(new Configuration { NodeName = "test", IPv4Addresses = null! }));
        _ = new MeshNode(new Configuration { NodeName = "test" });
    }

    [Fact]
    public async Task MalformedMeshPacketsDoNotReachTun()
    {
        var node = new MeshNode(new Configuration { NodeName = "test" });
        var peer = new Peer();
        var tun = new FakeTun();
        await node.ProcessPacketAsync([], peer, tun, default);
        await node.ProcessPacketAsync(Protocol.Packet(0, [0xff]), peer, tun, default);
        await node.ProcessPacketAsync(Protocol.Packet(1, [1, 2]), peer, tun, default);
        await node.ProcessPacketAsync(Protocol.Packet(2, [1, 2]), peer, tun, default);
        for (int i = 0; i < 40; i++)
            await node.ProcessPacketAsync(Protocol.Packet(3, new byte[i]), peer, tun, default);
        Assert.Empty(tun.Written);
        byte[] ip = new byte[40];
        ip[0] = 0x60;
        Protocol.NodeAddress("test").GetAddressBytes().CopyTo(ip, 24);
        byte[] valid = Protocol.Packet(3, ip);
        await node.ProcessPacketAsync(valid, peer, tun, default);
        await node.ProcessPacketAsync(valid, peer, tun, default);
        Assert.Single(tun.Written);
        Assert.Equal(ip, tun.Written[0]);
    }

    [Fact]
    public async Task TracerLogsIncludeNamesAndAddressesButSkipDuplicatesAndInvalidPackets()
    {
        var node = new MeshNode(new Configuration { NodeName = "local" });
        var peer = new Peer();
        using var output = new StringWriter();
        Logging.Configure();
        TextWriter previous = Console.Error;
        Console.SetError(output);
        try
        {
            byte[] name = Protocol.Packet(0, "remote\nnode"u8);
            await node.ProcessPacketAsync(name, peer, null, default);
            await node.ProcessPacketAsync(name, peer, null, default);
            await node.ProcessPacketAsync(Protocol.Packet(1, IPAddress.Parse("fd00::1234").GetAddressBytes()), peer, null, default);
            await node.ProcessPacketAsync(Protocol.Packet(2, IPAddress.Parse("192.0.2.7").GetAddressBytes()), peer, null, default);
            await node.ProcessPacketAsync(Protocol.Packet(0, "stale"u8, 0), peer, null, default);
            await node.ProcessPacketAsync(Protocol.Packet(0, [0xff]), peer, null, default);
            await node.ProcessPacketAsync(Protocol.Packet(1, [1]), peer, null, default);
            await node.ProcessPacketAsync(Protocol.Packet(2, [2]), peer, null, default);
            byte[] ip = new byte[40];
            ip[0] = 0x60;
            Protocol.NodeAddress("local").GetAddressBytes().CopyTo(ip, 24);
            await node.ProcessPacketAsync(Protocol.Packet(3, ip), peer, null, default);
        }
        finally { Console.SetError(previous); }
        string logs = output.ToString();
        Assert.Equal(3, logs.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("Received node tracer: \"remote\\nnode\"", logs);
        Assert.Contains(Protocol.FormatAddress(Protocol.NodeAddress("remote\nnode")), logs);
        Assert.Contains("Received IPv6 tracer: fd00:0000:0000:0000:0000:0000:0000:1234", logs);
        Assert.Contains("Received IPv4 tracer: 192.0.2.7", logs);
    }

    [Fact]
    public async Task RunningMeshNodeLearnsRoutesAndForwardsIpData()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int port = FreePort();
        var config = new Configuration
        {
            NodeName = "node-under-test",
            Transports = [new() { Type = "legacy_tcp_listen", Port = port, Keys = ["mesh-test"] }]
        };
        Task node = new MeshNode(config).RunAsync(stop.Token);
        using var client = new TcpClient();
        Task? connection = null;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
            var routing = new Routing();
            var incoming = Channel.CreateUnbounded<(byte[] Packet, Peer Peer)>();
            connection = LegacyTcp.RunAsync(client, ["mesh-test"u8.ToArray()], true, routing, incoming.Writer, stop.Token);
            byte[] flood = Protocol.Packet(0, "remote-peer"u8);
            var received = incoming.Reader.ReadAsync(stop.Token).AsTask();
            Peer? peer = null;
            while (peer is null)
            {
                routing.Broadcast(flood);
                await Task.Delay(10, stop.Token);
                if (!received.IsCompleted) continue;
                var item = await received;
                if (item.Packet.SequenceEqual(flood)) peer = item.Peer;
                else received = incoming.Reader.ReadAsync(stop.Token).AsTask();
            }
            foreach (var address in new[] { IPAddress.Parse("fd00::1234"), IPAddress.Parse("192.0.2.7") })
            {
                bool v4 = address.AddressFamily == AddressFamily.InterNetwork;
                byte[] announcement = Protocol.Packet(v4 ? (byte)2 : (byte)1, address.GetAddressBytes());
                peer.Send(announcement);
                await WaitFor(announcement);
                byte[] ip = new byte[v4 ? 20 : 40];
                ip[0] = v4 ? (byte)0x45 : (byte)0x60;
                address.GetAddressBytes().CopyTo(ip, v4 ? 16 : 24);
                byte[] data = Protocol.Packet(3, ip);
                peer.Send(data);
                await WaitFor(data);
            }

            async Task WaitFor(byte[] expected)
            {
                while (true)
                {
                    var item = await incoming.Reader.ReadAsync(stop.Token);
                    if (item.Packet.SequenceEqual(expected)) return;
                }
            }
        }
        finally
        {
            stop.Cancel();
            await TestSupport.IgnoreCancellation(node);
            if (connection is not null) await TestSupport.IgnoreCancellation(connection);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
