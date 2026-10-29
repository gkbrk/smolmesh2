using Serilog;
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
        var listener = new TcpListener(IPAddress.Any, config.Port);
        listener.Start(128);
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

    internal static async Task RunAsync(TcpClient client, byte[][] keys, bool initiator, Routing routing,
        ChannelWriter<(byte[] Packet, Peer Peer)> incoming, CancellationToken token)
    {
        client.NoDelay = true;
        NetworkStream stream = client.GetStream();
        byte[] sendIv = RandomNumberGenerator.GetBytes(16);
        byte[] receiveIv = new byte[16];
        byte[] keyfinder = new byte[16];
        byte[] key;
        if (initiator)
        {
            key = keys[0];
            await WriteHandshakeAsync(stream, key, sendIv, token);
            await ReadAsync(stream, receiveIv, token);
            await ReadAsync(stream, keyfinder, token);
            if (!CryptographicOperations.FixedTimeEquals(Derive(key, receiveIv, "keyfinder"), keyfinder))
                throw new IOException("Incorrect keyfinder.");
        }
        else
        {
            await ReadAsync(stream, receiveIv, token);
            await ReadAsync(stream, keyfinder, token);
            key = keys.FirstOrDefault(k => CryptographicOperations.FixedTimeEquals(Derive(k, receiveIv, "keyfinder"), keyfinder))
                ?? throw new IOException("Incorrect keyfinder.");
            await WriteHandshakeAsync(stream, key, sendIv, token);
        }

        var peer = new Peer();
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown peer";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        routing.Add(peer);
        Log.Information("Authenticated {Direction} connection to {Remote}", initiator ? "outbound" : "inbound", remote);
        Task send = SendAsync(stream, peer, Derive(key, sendIv, "enc_key"), Derive(key, sendIv, "mac_key"), stop.Token);
        Task receive = ReceiveAsync(stream, peer, incoming, Derive(key, receiveIv, "enc_key"), Derive(key, receiveIv, "mac_key"), stop.Token);
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

    private static async Task WriteHandshakeAsync(Stream stream, byte[] key, byte[] iv, CancellationToken token)
    {
        await WriteAsync(stream, iv, token);
        await WriteAsync(stream, Derive(key, iv, "keyfinder"), token);
    }

    internal static byte[] Encode(byte[] packet, byte[] macKey, Keystream keystream)
    {
        if (packet.Length > ushort.MaxValue) throw new InvalidDataException("Packet exceeds legacy framing limit.");
        byte[] frame = new byte[2 + packet.Length + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)packet.Length);
        packet.CopyTo(frame, 2);
        Gimli.Multi(macKey, packet).CopyTo(frame, 2 + packet.Length);
        keystream.Apply(frame);
        return frame;
    }

    private static async Task SendAsync(Stream stream, Peer peer, byte[] encKey, byte[] macKey, CancellationToken token)
    {
        var keystream = new Keystream(encKey);
        await foreach (byte[] packet in peer.Outgoing.Reader.ReadAllAsync(token))
        {
            if (packet.Length == 0) continue;
            await WriteAsync(stream, Encode(packet, macKey, keystream), token);
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
