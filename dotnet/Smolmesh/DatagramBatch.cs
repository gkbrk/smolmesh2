using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Smolmesh;

// Up to 32 datagrams per system call: sendmmsg and recvmmsg on Linux, sendmsg_x and recvmsg_x on
// macOS, one call each elsewhere. All the kernel reads or writes is on the pinned object heap, so
// it is passed by address without unsafe code. A SocketAddress holds the system's own sockaddr, so
// addresses are copied as they are.
internal sealed class DatagramBatch
{
    internal const int Capacity = 32;
    // Any UDP datagram fits a slot, and any address (sockaddr_in6 is the largest) a name.
    private const int SlotSize = 65536, NameSize = 28;
    private static readonly bool Linux = OperatingSystem.IsLinux(), Mac = OperatingSystem.IsMacOS();

    private readonly byte[] slots = GC.AllocateArray<byte>(Capacity * SlotSize, pinned: true);
    private readonly byte[] names = GC.AllocateArray<byte>(Capacity * NameSize, pinned: true);
    private readonly Iovec[] iovecs = GC.AllocateArray<Iovec>(Capacity, pinned: true);
    private readonly LinuxHeader[] linux = GC.AllocateArray<LinuxHeader>(Capacity, pinned: true);
    private readonly MacHeader[] mac = GC.AllocateArray<MacHeader>(Capacity, pinned: true);
    private readonly int[] lengths = new int[Capacity];
    private int count;

    internal DatagramBatch()
    {
        for (int i = 0; i < Capacity; i++)
        {
            iovecs[i].Base = Marshal.UnsafeAddrOfPinnedArrayElement(slots, i * SlotSize);
            nint iov = Marshal.UnsafeAddrOfPinnedArrayElement(iovecs, i), name = Marshal.UnsafeAddrOfPinnedArrayElement(names, i * NameSize);
            linux[i].Header = new() { Name = name, Iov = iov, IovLength = 1 };
            mac[i] = new() { Name = name, Iov = iov, IovLength = 1 };
        }
    }

    internal bool Full => count == Capacity;

    // Where the next datagram to send is written. Add then takes it into the batch.
    internal Span<byte> Next => slots.AsSpan(count * SlotSize, SlotSize);

    internal void Add(int length) => lengths[count++] = length;

    // Sends the batch and empties it. A datagram that cannot be sent is one more that was lost.
    internal void Send(Socket socket, SocketAddress address)
    {
        if (Linux || Mac) SendBatched(socket, address);
        else
            for (int i = 0; i < count; i++)
                try { socket.SendTo(slots.AsSpan(i * SlotSize, lengths[i]), SocketFlags.None, address); }
                catch (SocketException) { }
        count = 0;
    }

    private void SendBatched(Socket socket, SocketAddress address)
    {
        for (int i = 0; i < count; i++)
        {
            address.Buffer.Span[..address.Size].CopyTo(names.AsSpan(i * NameSize));
            Prepare(i, lengths[i], address.Size);
        }
        int fd = (int)socket.Handle;
        for (int sent = 0; sent < count;)
        {
            int result = (int)(Linux
                ? sendmmsg(fd, Marshal.UnsafeAddrOfPinnedArrayElement(linux, sent), (uint)(count - sent), 0)
                : sendmsg_x(fd, Marshal.UnsafeAddrOfPinnedArrayElement(mac, sent), (uint)(count - sent), 0));
            if (result > 0) sent += result;
            else if (Marshal.GetLastPInvokeError() is 11 or 35)
            {
                // EAGAIN: .NET makes its sockets nonblocking, so wait for room.
                var poll = new UnixTun.Native.PollFd { Fd = fd, Events = 4 };
                UnixTun.Native.poll(ref poll, 1, 100);
            }
            else if (Marshal.GetLastPInvokeError() != 4) sent++; // Not EINTR: skip the datagram it failed on.
        }
    }

    // Every datagram that arrives, with its sender. Each is valid until the next one is asked for.
    internal async IAsyncEnumerable<(ReadOnlyMemory<byte> Datagram, SocketAddress From)> ReceiveAllAsync(
        Socket socket, [EnumeratorCancellation] CancellationToken token)
    {
        SocketAddress[] from = Enumerable.Range(0, Capacity).Select(_ => new SocketAddress(socket.AddressFamily)).ToArray();
        while (true)
        {
            // .NET waits for the first, so that no thread does. The rest are what is already waiting.
            try { lengths[0] = await socket.ReceiveFromAsync(slots.AsMemory(0, SlotSize), SocketFlags.None, from[0], token); }
            // Windows reports the ICMP errors of earlier sends here.
            catch (SocketException) when (!token.IsCancellationRequested) { continue; }
            int received = 1 + ReceiveWaiting(socket, from);
            for (int i = 0; i < received; i++) yield return (slots.AsMemory(i * SlotSize, lengths[i]), from[i]);
        }
    }

    // Into slots 1 onwards, without waiting. An error is left for the next ReceiveFromAsync to report.
    private int ReceiveWaiting(Socket socket, SocketAddress[] from)
    {
        if (!Linux && !Mac) return 0;
        for (int i = 1; i < Capacity; i++) Prepare(i, SlotSize, NameSize);
        int fd = (int)socket.Handle;
        int received = (int)(Linux // with MSG_DONTWAIT
            ? recvmmsg(fd, Marshal.UnsafeAddrOfPinnedArrayElement(linux, 1), Capacity - 1, 0x40, 0)
            : recvmsg_x(fd, Marshal.UnsafeAddrOfPinnedArrayElement(mac, 1), Capacity - 1, 0x80));
        for (int i = 1; i <= received; i++)
        {
            (lengths[i], int nameLength) = Linux
                ? ((int)linux[i].Length, (int)linux[i].Header.NameLength)
                : ((int)mac[i].DataLength, (int)mac[i].NameLength);
            names.AsSpan(i * NameSize, nameLength).CopyTo(from[i].Buffer.Span);
            from[i].Size = nameLength;
        }
        return Math.Max(received, 0);
    }

    // The kernel writes some fields back, so they are set before every call.
    private void Prepare(int i, int length, int nameLength)
    {
        iovecs[i].Length = (nuint)length;
        linux[i].Header.NameLength = mac[i].NameLength = (uint)nameLength;
        linux[i].Header.Flags = mac[i].Flags = 0;
        mac[i].DataLength = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Iovec { internal nint Base; internal nuint Length; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msghdr
    {
        internal nint Name; internal uint NameLength; internal nint Iov; internal nuint IovLength;
        internal nint Control; internal nuint ControlLength; internal int Flags;
    }

    // struct mmsghdr
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxHeader { internal Msghdr Header; internal uint Length; }

    // struct msghdr_x, from XNU's bsd/sys/socket_private.h
    [StructLayout(LayoutKind.Sequential)]
    private struct MacHeader
    {
        internal nint Name; internal uint NameLength; internal nint Iov; internal int IovLength;
        internal nint Control; internal uint ControlLength; internal int Flags; internal nuint DataLength;
    }

    [DllImport("libc", SetLastError = true)] private static extern int sendmmsg(int fd, nint messages, uint count, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int recvmmsg(int fd, nint messages, uint count, int flags, nint timeout);
    // Private system calls, which Apple may change.
    [DllImport("libc", SetLastError = true)] private static extern nint sendmsg_x(int fd, nint messages, uint count, int flags);
    [DllImport("libc", SetLastError = true)] private static extern nint recvmsg_x(int fd, nint messages, uint count, int flags);
}
