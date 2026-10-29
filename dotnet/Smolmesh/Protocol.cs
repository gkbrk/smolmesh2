using System.Buffers.Binary;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Smolmesh;

internal static class Protocol
{
    internal static ulong Millis => (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static (ulong X, ulong Y) SpeckRound(ulong x, ulong y, ulong key)
    {
        x = unchecked(BitOperations.RotateRight(x, 8) + y) ^ key;
        y = BitOperations.RotateLeft(y, 3) ^ x;
        return (x, y);
    }

    internal static IPAddress NodeAddress(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        ulong a = 69, b = (ulong)bytes.Length;
        foreach (byte c in bytes)
        {
            (a, b) = SpeckRound(a, b, c);
            for (int i = 0; i < 32; i++) (a, b) = SpeckRound(a, b, 0);
        }
        for (int i = 0; i < 32; i++) (a, b) = SpeckRound(a, b, 0);
        while ((b & 0xffff) != 0x00fd) (a, b) = SpeckRound(a, b, 0);
        byte[] result = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(result, b);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(8), a);
        return new IPAddress(result);
    }

    internal static string FormatAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? address.ToString() : string.Join(":",
            Enumerable.Range(0, 8).Select(i => Convert.ToHexString(bytes.AsSpan(i * 2, 2)).ToLowerInvariant()));
    }

    internal static byte[] Packet(byte command, ReadOnlySpan<byte> payload, ulong? timestamp = null)
    {
        byte[] result = new byte[9 + payload.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(result, timestamp ?? Millis);
        result[8] = command;
        payload.CopyTo(result.AsSpan(9));
        return result;
    }

    internal static IPAddress? Destination(ReadOnlySpan<byte> packet)
    {
        if (packet.Length >= 20 && packet[0] >> 4 == 4 && (packet[0] & 15) >= 5
            && packet.Length >= (packet[0] & 15) * 4) return new IPAddress(packet.Slice(16, 4));
        if (packet.Length >= 40 && packet[0] >> 4 == 6) return new IPAddress(packet.Slice(24, 16));
        return null;
    }

    internal static bool Fresh(ulong timestamp, ulong now) =>
        (timestamp > now ? timestamp - now : now - timestamp) <= 180_000;
}

// Local-only fingerprints need not match Rust's FxHasher on the wire.
internal sealed class SeenPackets(int capacity)
{
    private readonly HashSet<string> hashes = [];
    private readonly Queue<string> order = new();

    internal bool Add(ReadOnlySpan<byte> packet)
    {
        string hash = Convert.ToHexString(SHA256.HashData(packet));
        if (!hashes.Add(hash)) return true;
        order.Enqueue(hash);
        if (order.Count > capacity) hashes.Remove(order.Dequeue());
        return false;
    }
}
