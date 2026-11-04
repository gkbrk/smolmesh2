using System.Buffers.Binary;
using System.Net;

namespace Smolmesh;

/// <summary>
/// macOS does not loop packets for one of our own addresses back inside the kernel: it
/// pushes them into the utun with an unfinished transport checksum, where the mesh would
/// only echo them around. This wrapper hands them straight back to the local stack with
/// the checksum completed, so the rest of the node never sees them.
/// </summary>
internal sealed class MacTunLoopback(ITun inner, IPAddress ownAddress) : ITun
{
    public async Task<byte[]> ReadAsync(CancellationToken token)
    {
        while (true)
        {
            byte[] packet = await inner.ReadAsync(token);
            var destination = Protocol.Destination(packet);
            if (destination is null || !destination.Equals(ownAddress)) return packet;
            FixTransportChecksum(packet);
            await inner.WriteAsync(packet, token);
        }
    }

    public Task WriteAsync(byte[] packet, CancellationToken token) => inner.WriteAsync(packet, token);

    public Task AddRouteAsync(IPAddress address, CancellationToken token) => inner.AddRouteAsync(address, token);

    public void Dispose() => inner.Dispose();

    /// <summary>Recomputes the IPv6 transport checksum. Returns false for packets that cannot be checked.</summary>
    internal static bool FixTransportChecksum(byte[] packet) =>
        packet.Length >= 44 && packet[0] >> 4 == 6 && FixIPv6Checksum(packet);

    private static bool FixIPv6Checksum(byte[] packet)
    {
        int next = packet[6];
        int offset = 40;
        while (next is 0 or 43 or 60)
        {
            // The pseudo-header destination of a packet with a routing header is the
            // final destination, which is not the address in the packet.
            if (next == 43) return false;
            if (offset + 2 > packet.Length) return false;
            int length = (packet[offset + 1] + 1) * 8;
            if (offset + length > packet.Length) return false;
            next = packet[offset];
            offset += length;
        }
        if (next == 44)
        {
            if (offset + 8 > packet.Length) return false;
            int fragment = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2));
            if (fragment >> 3 != 0 || (fragment & 1) != 0) return false;
            next = packet[offset];
            offset += 8;
        }
        int totalLength = 40 + BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4));
        if (totalLength > packet.Length || offset + 4 > totalLength) return false;
        Span<byte> pseudo = stackalloc byte[40];
        pseudo.Clear();
        packet.AsSpan(8, 32).CopyTo(pseudo);
        BinaryPrimitives.WriteUInt32BigEndian(pseudo[32..], (uint)(totalLength - offset));
        pseudo[39] = (byte)next;
        return FixSegment(packet, offset, totalLength, next, pseudo);
    }

    private static bool FixSegment(byte[] packet, int offset, int end, int protocol, ReadOnlySpan<byte> pseudo)
    {
        int field = protocol switch { 58 => 2, 6 => 16, 17 => 6, _ => -1 };
        if (field < 0 || end - offset < field + 2) return false;
        ReadOnlySpan<byte> segment = packet.AsSpan(offset, end - offset);
        Span<byte> stored = packet.AsSpan(offset + field, 2);
        stored.Clear();
        uint sum = Sum(segment) + Sum(pseudo);
        ushort checksum = Finish(sum);
        // A UDP checksum of zero means "not computed", so it is sent as all ones.
        BinaryPrimitives.WriteUInt16BigEndian(stored, checksum == 0 && protocol == 17 ? (ushort)0xffff : checksum);
        return true;
    }

    private static uint Sum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (i < data.Length) sum += (uint)(data[i] << 8);
        return sum;
    }

    private static ushort Finish(uint sum)
    {
        while (sum > 0xffff) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
