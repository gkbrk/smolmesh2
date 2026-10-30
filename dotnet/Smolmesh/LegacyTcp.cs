using Serilog;
using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Smolmesh;

internal static class LegacyTcp
{
    internal static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(30);
    // Size of the receive buffer, and how much the send loop gathers before one socket write.
    internal const int BufferSize = 64 * 1024;
    private const int FrameOverhead = 2 + 16;

    internal static async Task ConnectLoopAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(config.Host, config.Port, timeout.Token);
                }
                await RunAsync(client, [Encoding.UTF8.GetBytes(config.Key!)], true, routing, incoming, token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Log.Warning("Connection to {Host}:{Port}: {Message}", config.Host, config.Port, ex.Message);
            }
            await Task.Delay(TimeSpan.FromSeconds(5 + Random.Shared.NextDouble() * 5), token);
        }
    }

    internal static async Task ListenAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        var listener = Listen(config.Port);
        Log.Information("Listening on port {Port}", config.Port);
        var connections = new HashSet<Task>();
        try
        {
            byte[][] keys = config.Keys.Select(Encoding.UTF8.GetBytes).ToArray();
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                connections.RemoveWhere(t => t.IsCompleted);
                connections.Add(HandleAsync(client));
            }

            async Task HandleAsync(TcpClient client)
            {
                using (client)
                {
                    try { await RunAsync(client, keys, false, routing, incoming, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { Log.Warning("Incoming connection: {Message}", ex.Message); }
                }
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(connections);
        }
    }

    // Accepts IPv4 and IPv6 on one dual-mode socket, or IPv4 only if the host has no IPv6.
    internal static TcpListener Listen(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                listener.Start(128);
                return listener;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported
                or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable)
            {
                listener?.Stop();
                Log.Warning("IPv6 unavailable, listening on IPv4 only: {Message}", ex.Message);
            }
        }
        var ipv4 = new TcpListener(IPAddress.Any, port);
        ipv4.Start(128);
        return ipv4;
    }

    // Shows IPv4 peers of a dual-mode listener as a.b.c.d instead of ::ffff:a.b.c.d.
    internal static string Describe(EndPoint? endPoint) => endPoint switch
    {
        IPEndPoint { Address.IsIPv4MappedToIPv6: true } ip => new IPEndPoint(ip.Address.MapToIPv4(), ip.Port).ToString(),
        null => "unknown peer",
        _ => endPoint.ToString() ?? "unknown peer",
    };

    internal static async Task RunAsync(TcpClient client, byte[][] keys, bool initiator, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();
        // All reads, the handshake included, go through one buffer: frames that arrive
        // together with the peer's handshake must not be lost.
        var reader = new BufferedStream(stream, BufferSize);
        byte[] sendIv = RandomNumberGenerator.GetBytes(16);
        byte[] receiveIv = new byte[16];
        byte[] keyfinder = new byte[16];
        byte[] key;
        if (initiator)
        {
            key = keys[0];
            await WriteHandshakeAsync(stream, key, sendIv, token);
            await ReadAsync(reader, receiveIv, token);
            await ReadAsync(reader, keyfinder, token);
            if (!CryptographicOperations.FixedTimeEquals(Derive(key, receiveIv, "keyfinder"), keyfinder))
                throw new IOException("Incorrect keyfinder.");
        }
        else
        {
            await ReadAsync(reader, receiveIv, token);
            await ReadAsync(reader, keyfinder, token);
            key = keys.FirstOrDefault(k => CryptographicOperations.FixedTimeEquals(Derive(k, receiveIv, "keyfinder"), keyfinder))
                ?? throw new IOException("Incorrect keyfinder.");
            await WriteHandshakeAsync(stream, key, sendIv, token);
        }

        var peer = new Peer();
        string remote = Describe(client.Client.RemoteEndPoint);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        routing.Add(peer);
        Log.Information("Authenticated {Direction} connection to {Remote}", initiator ? "outbound" : "inbound", remote);
        Task send = SendAsync(stream, peer, Derive(key, sendIv, "enc_key"), Derive(key, sendIv, "mac_key"), stop.Token);
        Task receive = ReceiveAsync(reader, peer, incoming, Derive(key, receiveIv, "enc_key"), Derive(key, receiveIv, "mac_key"), stop.Token);
        try
        {
            await await Task.WhenAny(send, receive);
        }
        finally
        {
            stop.Cancel();
            routing.Remove(peer);
            Log.Information("Disconnected from {Remote}", remote);
            try { await Task.WhenAll(send, receive); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
    }

    internal static byte[] Derive(byte[] key, byte[] iv, string label) => Gimli.Multi(key, iv, Encoding.UTF8.GetBytes(label));

    private static Task WriteHandshakeAsync(Stream stream, byte[] key, byte[] iv, CancellationToken token) =>
        WriteAsync(stream, (byte[])[.. iv, .. Derive(key, iv, "keyfinder")], token);

    internal static byte[] Encode(byte[] packet, byte[] macKey, Keystream keystream)
    {
        var frame = new ArrayBufferWriter<byte>(packet.Length + FrameOverhead);
        Encode(packet, macKey, keystream, frame);
        return frame.WrittenSpan.ToArray();
    }

    internal static void Encode(byte[] packet, byte[] macKey, Keystream keystream, ArrayBufferWriter<byte> output)
    {
        if (packet.Length > ushort.MaxValue) throw new InvalidDataException("Packet exceeds legacy framing limit.");
        int size = packet.Length + FrameOverhead;
        Span<byte> frame = output.GetSpan(size)[..size];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)packet.Length);
        packet.CopyTo(frame[2..]);
        Gimli.Multi(macKey, packet).CopyTo(frame[(2 + packet.Length)..]);
        keystream.Apply(frame);
        output.Advance(size);
    }

    // Encodes every packet already queued, up to BufferSize, and sends them in one write.
    // The byte stream is the same as sending frames one by one, so peers can't tell.
    private static async Task SendAsync(Stream stream, Peer peer, byte[] encKey, byte[] macKey, CancellationToken token)
    {
        var keystream = new Keystream(encKey);
        var batch = new ArrayBufferWriter<byte>(BufferSize + ushort.MaxValue + FrameOverhead);
        ChannelReader<byte[]> reader = peer.Outgoing.Reader;
        while (await reader.WaitToReadAsync(token))
        {
            while (batch.WrittenCount < BufferSize && reader.TryRead(out byte[]? packet))
                if (packet.Length != 0) Encode(packet, macKey, keystream, batch);
            if (batch.WrittenCount == 0) continue;
            await WriteAsync(stream, batch.WrittenMemory, token);
            batch.ResetWrittenCount();
        }
    }

    private static async Task ReceiveAsync(Stream stream, Peer peer, ChannelWriter<(byte[] Packet, Peer Peer)> incoming,
        byte[] encKey, byte[] macKey, CancellationToken token)
    {
        var keystream = new Keystream(encKey);
        byte[] length = new byte[2];
        while (true)
        {
            await ReadAsync(stream, length, token);
            keystream.Apply(length);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(length);
            byte[] packet = new byte[count];
            await ReadAsync(stream, packet, token);
            keystream.Apply(packet);
            byte[] mac = new byte[16];
            await ReadAsync(stream, mac, token);
            keystream.Apply(mac);
            if (!CryptographicOperations.FixedTimeEquals(Gimli.Multi(macKey, packet), mac))
                throw new IOException("MAC verification failed.");
            await incoming.WriteAsync((packet, peer), token);
        }
    }

    private static async Task ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(IoTimeout);
        await stream.ReadExactlyAsync(buffer, timeout.Token);
    }

    private static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(IoTimeout);
        await stream.WriteAsync(buffer, timeout.Token);
    }
}
