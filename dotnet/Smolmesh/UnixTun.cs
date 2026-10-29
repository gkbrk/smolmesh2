using Serilog;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Smolmesh;

internal sealed class UnixTun : ITun
{
    private readonly int fd;
    private readonly bool mac;
    private readonly string name;
    private bool disposed;

    private UnixTun(int fd, bool mac, string name) => (this.fd, this.mac, this.name) = (fd, mac, name);

    internal static UnixTun Open()
    {
        bool mac = OperatingSystem.IsMacOS();
        int fd = mac ? Native.socket(32, 2, 2) : Native.open("/dev/net/tun", 2 | 0x800);
        if (fd < 0) throw Error("Opening TUN (administrator/root privileges required)");
        try
        {
            string name;
            if (mac)
            {
                byte[] info = new byte[100];
                Encoding.ASCII.GetBytes("com.apple.net.utun_control").CopyTo(info, 4);
                if (Native.ioctl(fd, 0xc0644e03, info) < 0) throw Error("CTLIOCGINFO");
                byte[] address = new byte[32];
                address[0] = 32;
                address[1] = 32; // AF_SYSTEM
                BinaryPrimitives.WriteUInt16LittleEndian(address.AsSpan(2), 2); // AF_SYS_CONTROL
                info.AsSpan(0, 4).CopyTo(address.AsSpan(4));
                // sc_unit=0 asks the kernel to allocate an available utun.
                if (Native.connect(fd, address, 32) < 0) throw Error("Connecting utun control");
                byte[] interfaceName = new byte[64];
                uint size = (uint)interfaceName.Length;
                if (Native.getsockopt(fd, 2, 2, interfaceName, ref size) < 0) throw Error("UTUN_OPT_IFNAME");
                int end = Array.IndexOf(interfaceName, (byte)0);
                name = Encoding.ASCII.GetString(interfaceName, 0, end < 0 ? (int)size : end);
                int flags = Native.fcntl(fd, 3, 0);
                if (flags < 0 || Native.fcntl(fd, 4, flags | 4) < 0) throw Error("Setting utun nonblocking");
            }
            else
            {
                name = "smolmesh1";
                byte[] request = new byte[40];
                Encoding.ASCII.GetBytes(name).CopyTo(request, 0);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(16), 0x1001); // IFF_TUN | IFF_NO_PI
                if (Native.ioctl(fd, 0x400454ca, request) < 0) throw Error("TUNSETIFF");
            }
            Log.Information("Opened TUN {Name}", name);
            return new UnixTun(fd, mac, name);
        }
        catch
        {
            Native.close(fd);
            throw;
        }
    }

    internal async Task ConfigureAsync(IPAddress[] addresses, CancellationToken token)
    {
        if (mac) await Tun.CommandAsync("ifconfig", token, name, "up");
        else await Tun.CommandAsync("ip", token, "link", "set", "dev", name, "up");
        foreach (var address in addresses)
        {
            bool v4 = Tun.IsV4(address);
            string cidr = $"{address}/{(v4 ? 32 : 128)}";
            if (mac)
            {
                if (v4) await Tun.CommandAsync("ifconfig", token, name, "inet", address.ToString(), address.ToString(), "netmask", "255.255.255.255", "alias");
                else await Tun.CommandAsync("ifconfig", token, name, "inet6", cidr, "alias");
            }
            else await Tun.CommandAsync("ip", token, v4 ? "-4" : "-6", "addr", "add", cidr, "dev", name);
        }
    }

    public Task AddRouteAsync(IPAddress address, CancellationToken token)
    {
        bool v4 = Tun.IsV4(address);
        return mac
            ? Tun.CommandAsync("route", token, "add", v4 ? "-inet" : "-inet6", "-host", address.ToString(), "-interface", name)
            : Tun.CommandAsync("ip", token, v4 ? "-4" : "-6", "route", "replace", $"{address}/{(v4 ? 32 : 128)}", "dev", name);
    }

    public Task<byte[]> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        byte[] buffer = new byte[65539];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var poll = new Native.PollFd { Fd = fd, Events = 1 };
            int ready = Native.poll(ref poll, 1, 200);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == 4) continue; // EINTR
                throw Error("TUN poll");
            }
            if (ready == 0) continue;
            if ((poll.ReturnedEvents & 0x38) != 0) throw new IOException("TUN descriptor closed or failed.");
            nint count = Native.read(fd, buffer, (nuint)buffer.Length);
            if (count < 0)
            {
                if (WouldBlock()) continue;
                throw Error("TUN read");
            }
            if (count == 0) throw new EndOfStreamException("TUN closed.");
            int skip = mac ? 4 : 0;
            if (count <= skip) continue;
            return buffer[skip..(int)count];
        }
    }, token);

    public async Task WriteAsync(byte[] packet, CancellationToken token)
    {
        byte[] buffer = packet;
        if (mac)
        {
            if (Protocol.Destination(packet) is null) return;
            buffer = new byte[4 + packet.Length];
            BinaryPrimitives.WriteUInt32BigEndian(buffer, packet[0] >> 4 == 4 ? 2u : 30u);
            packet.CopyTo(buffer, 4);
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            nint count = Native.write(fd, buffer, (nuint)buffer.Length);
            if (count == buffer.Length) return;
            if (count < 0 && WouldBlock())
            {
                await Task.Delay(2, token);
                continue;
            }
            if (count < 0) throw Error("TUN write");
            throw new IOException("Partial TUN packet write.");
        }
    }

    private static bool WouldBlock() => Marshal.GetLastPInvokeError() is 4 or 11 or 35;
    private static IOException Error(string operation) => new($"{operation}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Native.close(fd);
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct PollFd { internal int Fd; internal short Events; internal short ReturnedEvents; }

        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] internal static extern int socket(int domain, int type, int protocol);
        // Darwin arm64 passes variadic arguments on the stack, unlike fixed arguments.
        // Occupy x2..x7 so the third native argument lands at the stack's start.
        internal static int ioctl(int fd, nuint request, byte[] data) => DarwinArm64
            ? IoctlDarwinArm64(fd, request, 0, 0, 0, 0, 0, 0, data) : Ioctl(fd, request, data);
        internal static int fcntl(int fd, int command, int arg) => DarwinArm64
            ? FcntlDarwinArm64(fd, command, 0, 0, 0, 0, 0, 0, arg) : Fcntl(fd, command, arg);
        private static bool DarwinArm64 => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int Ioctl(int fd, nuint request, byte[] data);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int IoctlDarwinArm64(int fd, nuint request,
            nint pad0, nint pad1, nint pad2, nint pad3, nint pad4, nint pad5, byte[] data);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int Fcntl(int fd, int command, int arg);
        [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)] private static extern int FcntlDarwinArm64(int fd, int command,
            nint pad0, nint pad1, nint pad2, nint pad3, nint pad4, nint pad5, int arg);
        [DllImport("libc", SetLastError = true)] internal static extern int connect(int fd, byte[] address, uint length);
        [DllImport("libc", SetLastError = true)] internal static extern int getsockopt(int fd, int level, int option, byte[] value, ref uint length);
        [DllImport("libc", SetLastError = true)] internal static extern int poll(ref PollFd fds, uint count, int timeout);
        [DllImport("libc", SetLastError = true)] internal static extern nint read(int fd, byte[] data, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern nint write(int fd, byte[] data, nuint count);
    }
}
