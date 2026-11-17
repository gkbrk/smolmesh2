using Serilog;
using Smolmesh.Crypto;
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
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    // Size of the receive buffer, and how much the send loop gathers before one socket write.
    internal const int BufferSize = 64 * 1024;
    // Each frame is the 2-byte length and the packet, encrypted together, then the stream's tag.
    private const int FrameOverhead = 2 + AsconStream.TagSize;
    // Kernel auto-tuning stops at 4 MB with common defaults (Linux net.ipv4.tcp_wmem, macOS
    // net.inet.tcp.autorcvbufmax): about 200 Mbit/s at the 160 ms of a loaded long link.
    // 7 MB stays under macOS's default kern.ipc.maxsockbuf of 8 MB less mbuf overhead.
    internal const int SocketBufferSize = 7 * 1024 * 1024;

    internal static async Task ConnectLoopAsync(TransportConfiguration config, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                SizeBuffers(client.Client);
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
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(token); }
                catch (SocketException ex)
                {
                    // Out of file handles, or a connection reset before accept: wait, don't exit the node.
                    Log.Warning("Accept on port {Port}: {Message}", config.Port, ex.Message);
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }
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
                SizeBuffers(listener.Server);
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
        SizeBuffers(ipv4.Server);
        ipv4.Start(128);
        return ipv4;
    }

    // Before connecting or listening: the receive window's scale is fixed at the handshake, and
    // accepted sockets inherit the listener's buffers. A fixed size turns auto-tuning off.
    internal static void SizeBuffers(Socket socket)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // Plain SO_SNDBUF/SO_RCVBUF are capped at net.core.[rw]mem_max, often 208 KB, far below
                // auto-tuning; SO_SNDBUFFORCE/SO_RCVBUFFORCE need root, which the TUN already does.
                byte[] size = BitConverter.GetBytes(SocketBufferSize);
                socket.SetRawSocketOption(1, 32, size);
                socket.SetRawSocketOption(1, 33, size);
            }
            else if (OperatingSystem.IsMacOS())
            {
                socket.SendBufferSize = SocketBufferSize;
                socket.ReceiveBufferSize = SocketBufferSize;
            }
        }
        catch (SocketException ex)
        {
            Log.Warning("Socket buffers left to the system: {Message}", ex.Message);
        }
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
        // Short, so connections that never authenticate can't hold sockets for the full IoTimeout.
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            handshake.CancelAfter(HandshakeTimeout);
            if (initiator)
            {
                key = keys[0];
                await WriteHandshakeAsync(stream, key, sendIv, handshake.Token);
                await ReadAsync(reader, receiveIv, handshake.Token);
                await ReadAsync(reader, keyfinder, handshake.Token);
                if (!CryptographicOperations.FixedTimeEquals(Derive(key, receiveIv, "keyfinder"), keyfinder))
                    throw new IOException("Incorrect keyfinder.");
            }
            else
            {
                await ReadAsync(reader, receiveIv, handshake.Token);
                await ReadAsync(reader, keyfinder, handshake.Token);
                key = keys.FirstOrDefault(k => CryptographicOperations.FixedTimeEquals(Derive(k, receiveIv, "keyfinder"), keyfinder))
                    ?? throw new IOException("Incorrect keyfinder.");
                await WriteHandshakeAsync(stream, key, sendIv, handshake.Token);
            }
        }

        var peer = new Peer();
        string remote = Describe(client.Client.RemoteEndPoint);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        routing.Add(peer);
        Log.Information("Authenticated {Direction} connection to {Remote}", initiator ? "outbound" : "inbound", remote);
        Task send = SendAsync(stream, peer, new AsconStream(StreamKey(key, sendIv, receiveIv)), stop.Token);
        Task receive = ReceiveAsync(reader, peer, incoming, new AsconStream(StreamKey(key, receiveIv, sendIv)), stop.Token);
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

    internal static byte[] Derive(byte[] key, byte[] iv, string label) => Ascon.Multi(key, iv, Encoding.UTF8.GetBytes(label));

    // Each direction's key depends on both IVs, sender's first, so replaying an old handshake
    // can't bring back an old stream.
    internal static byte[] StreamKey(byte[] key, byte[] senderIv, byte[] receiverIv) =>
        Ascon.Multi(key, senderIv, receiverIv, "stream"u8.ToArray());

    private static Task WriteHandshakeAsync(Stream stream, byte[] key, byte[] iv, CancellationToken token) =>
        WriteAsync(stream, (byte[])[.. iv, .. Derive(key, iv, "keyfinder")], token);

    internal static byte[] Encode(byte[] packet, AsconStream cipher)
    {
        var frame = new ArrayBufferWriter<byte>(packet.Length + FrameOverhead);
        Encode(packet, cipher, frame);
        return frame.WrittenSpan.ToArray();
    }

    internal static void Encode(byte[] packet, AsconStream cipher, ArrayBufferWriter<byte> output)
    {
        if (packet.Length > ushort.MaxValue) throw new InvalidDataException("Packet exceeds legacy framing limit.");
        int size = packet.Length + FrameOverhead;
        Span<byte> frame = output.GetSpan(size)[..size];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)packet.Length);
        packet.CopyTo(frame[2..]);
        cipher.Seal(frame[..(2 + packet.Length)], frame[(2 + packet.Length)..]);
        output.Advance(size);
    }

    // Encodes every packet already queued, up to BufferSize, and sends them in one write.
    // The byte stream is the same as sending frames one by one, so peers can't tell.
    private static async Task SendAsync(Stream stream, Peer peer, AsconStream cipher, CancellationToken token)
    {
        var batch = new ArrayBufferWriter<byte>(BufferSize + ushort.MaxValue + FrameOverhead);
        ChannelReader<byte[]> reader = peer.Outgoing.Reader;
        while (await reader.WaitToReadAsync(token))
        {
            while (batch.WrittenCount < BufferSize && reader.TryRead(out byte[]? packet))
                if (packet.Length != 0) Encode(packet, cipher, batch);
            if (batch.WrittenCount == 0) continue;
            await WriteAsync(stream, batch.WrittenMemory, token);
            batch.ResetWrittenCount();
        }
    }

    private static async Task ReceiveAsync(Stream stream, Peer peer, ChannelWriter<(byte[] Packet, Peer Peer)> incoming,
        AsconStream cipher, CancellationToken token)
    {
        byte[] frame = new byte[ushort.MaxValue + FrameOverhead];
        byte[] keystream = new byte[2];
        while (true)
        {
            await ReadAsync(stream, frame.AsMemory(0, 2), token);
            cipher.PeekKeystream(keystream);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(frame) ^ BinaryPrimitives.ReadUInt16LittleEndian(keystream);
            await ReadAsync(stream, frame.AsMemory(2, count + AsconStream.TagSize), token);
            if (!cipher.Open(frame.AsSpan(0, 2 + count), frame.AsSpan(2 + count, AsconStream.TagSize)))
                throw new IOException("MAC verification failed.");
            await incoming.WriteAsync((frame[2..(2 + count)], peer), token);
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
