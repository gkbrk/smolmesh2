using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using Smolmesh;
using Xunit;

public class LegacyTcpTests
{
    [Fact]
    public void FramingEncryptsLengthPayloadAndMac()
    {
        byte[] key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] packet = "payload"u8.ToArray();
        byte[] frame = LegacyTcp.Encode(packet, key, new Keystream(key));
        new Keystream(key).Apply(frame);
        Assert.Equal((ushort)packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal(packet, frame.AsSpan(2, packet.Length).ToArray());
        Assert.Equal(Gimli.Multi(key, packet), frame.AsSpan(2 + packet.Length).ToArray());
        Assert.Throws<InvalidDataException>(() => LegacyTcp.Encode(new byte[65536], key, new Keystream(key)));
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
                await stream.ReadExactlyAsync(new byte[32], stop.Token);
                byte[] frame = LegacyTcp.Encode("data"u8.ToArray(), LegacyTcp.Derive(key, iv, "mac_key"),
                    new Keystream(LegacyTcp.Derive(key, iv, "enc_key")));
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
