using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Smolmesh;
using Smolmesh.Crypto;
using Xunit;

public class LegacyTcpTests
{
    [Fact]
    public void FramingEncryptsLengthPayloadAndTag()
    {
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] packet = "payload"u8.ToArray();
        byte[] frame = LegacyTcp.Encode(packet, new AsconStream(key));
        Assert.Equal(2 + packet.Length + AsconStream.TagSize, frame.Length);
        Assert.NotEqual(packet, frame.AsSpan(2, packet.Length).ToArray());
        Assert.True(new AsconStream(key).Open(frame.AsSpan(0, 2 + packet.Length), frame.AsSpan(2 + packet.Length)));
        Assert.Equal((ushort)packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal(packet, frame.AsSpan(2, packet.Length).ToArray());
        Assert.Throws<InvalidDataException>(() => LegacyTcp.Encode(new byte[65536], new AsconStream(key)));
    }

    [Fact]
    public void StreamKeysDependOnDirectionAndBothIvs()
    {
        byte[] key = "secret"u8.ToArray(), a = new byte[16], b = new byte[16], c = new byte[16];
        b[0] = 1;
        c[0] = 2;
        Assert.NotEqual(LegacyTcp.StreamKey(key, a, b), LegacyTcp.StreamKey(key, b, a));
        Assert.NotEqual(LegacyTcp.StreamKey(key, a, b), LegacyTcp.StreamKey(key, a, c));
    }

    [Fact]
    public async Task BidirectionalEncryptedFramesMultipleKeysAndCancellation()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            using var server = await listener.AcceptTcpClientAsync(stop.Token);
            var clientRouting = new Routing();
            var serverRouting = new Routing();
            var clientIncoming = Channel.CreateUnbounded<(byte[] Packet, Peer Peer)>();
            var serverIncoming = Channel.CreateUnbounded<(byte[] Packet, Peer Peer)>();
            Task a = LegacyTcp.RunAsync(client, ["secret"u8.ToArray()], true, clientRouting, clientIncoming.Writer, stop.Token);
            Task b = LegacyTcp.RunAsync(server, ["wrong"u8.ToArray(), "secret"u8.ToArray()], false, serverRouting, serverIncoming.Writer, stop.Token);
            try
            {
                // Keep broadcasting until authentication has registered the sender.
                var read = serverIncoming.Reader.ReadAsync(stop.Token).AsTask();
                byte[] first = Protocol.Packet(0, "client"u8);
                while (!read.IsCompleted)
                {
                    clientRouting.Broadcast(first);
                    await Task.Delay(10, stop.Token);
                }
                var received = await read;
                Assert.Equal(first, received.Packet);
                foreach (int length in new[] { 1, 16, 17, 2048, 65535 })
                {
                    byte[] packet = RandomNumberGenerator.GetBytes(length);
                    received.Peer.Send(packet);
                    var reply = await clientIncoming.Reader.ReadAsync(stop.Token);
                    Assert.Equal(packet, reply.Packet);
                }
            }
            finally
            {
                stop.Cancel();
                await TestSupport.IgnoreCancellation(a, b);
            }
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ListenerAcceptsIPv4AndIPv6()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = LegacyTcp.Listen(0);
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var addresses = new List<IPAddress> { IPAddress.Loopback };
            if (Socket.OSSupportsIPv6) addresses.Add(IPAddress.IPv6Loopback);
            foreach (IPAddress address in addresses)
            {
                using var client = new TcpClient(address.AddressFamily);
                await client.ConnectAsync(address, port, stop.Token);
                using var server = await listener.AcceptTcpClientAsync(stop.Token);
                var remote = (IPEndPoint)server.Client.RemoteEndPoint!;
                Assert.Equal(new IPEndPoint(address, ((IPEndPoint)client.Client.LocalEndPoint!).Port).ToString(),
                    LegacyTcp.Describe(remote));
            }
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task ConnectionsUseLargeSocketBuffers()
    {
        // Linux needs root for the FORCE options; other systems keep their own sizing.
        if (!OperatingSystem.IsMacOS() && !(OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess)) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = LegacyTcp.Listen(0);
        try
        {
            using var client = new TcpClient();
            LegacyTcp.SizeBuffers(client.Client);
            await client.ConnectAsync("localhost", ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            using var server = await listener.AcceptTcpClientAsync(stop.Token);
            foreach (Socket socket in new[] { client.Client, server.Client })
            {
                Assert.True(socket.SendBufferSize >= LegacyTcp.SocketBufferSize);
                Assert.True(socket.ReceiveBufferSize >= LegacyTcp.SocketBufferSize);
            }
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsIncorrectKeysAndTamperedMacs(bool tamper)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
            using var server = await listener.AcceptTcpClientAsync(stop.Token);
            var incoming = Channel.CreateUnbounded<(byte[] Packet, Peer Peer)>();
            Task run = LegacyTcp.RunAsync(server, ["secret"u8.ToArray()], false, new Routing(), incoming.Writer, stop.Token);
            NetworkStream stream = client.GetStream();
            byte[] iv = new byte[16];
            byte[] key = (tamper ? "secret"u8 : "wrong"u8).ToArray();
            await stream.WriteAsync(iv, stop.Token);
            await stream.WriteAsync(LegacyTcp.Derive(key, iv, "keyfinder"), stop.Token);
            if (tamper)
            {
                byte[] handshake = new byte[32];
                await stream.ReadExactlyAsync(handshake, stop.Token);
                byte[] frame = LegacyTcp.Encode("data"u8.ToArray(), new AsconStream(LegacyTcp.StreamKey(key, iv, handshake[..16])));
                frame[^1] ^= 1;
                await stream.WriteAsync(frame, stop.Token);
            }
            var ex = await Assert.ThrowsAsync<IOException>(() => run);
            Assert.Contains(tamper ? "MAC" : "keyfinder", ex.Message);
            Assert.False(incoming.Reader.TryRead(out _));
        }
        finally { listener.Stop(); }
    }
}
