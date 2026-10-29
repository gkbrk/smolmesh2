using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Smolmesh;

internal sealed class WindowsTun : ITun
{
    private readonly nint adapter;
    private readonly nint session;
    private readonly nint readEvent;
    private readonly string name;
    private bool disposed;

    static WindowsTun()
    {
        NativeLibrary.SetDllImportResolver(typeof(WindowsTun).Assembly, Resolve);
    }

    private static nint Resolve(string library, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (library != "wintun") return 0;
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        if (architecture == "x64") architecture = "amd64";
        foreach (string directory in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Distinct())
            foreach (string filename in new[] { "wintun.dll", $"wintun-{architecture}.dll" })
                if (NativeLibrary.TryLoad(Path.Combine(directory, filename), out nint handle)) return handle;
        throw new DllNotFoundException("Place the matching-architecture Wintun DLL beside smolmesh or in the working directory.");
    }

    internal WindowsTun(string nodeName)
    {
        name = $"smolmesh-{nodeName}";
        adapter = Native.WintunCreateAdapter(name, "Wintun", 0);
        if (adapter == 0) throw Error("WintunCreateAdapter (administrator privileges required)");
        try
        {
            session = Native.WintunStartSession(adapter, 0x4000000);
            if (session == 0) throw Error("WintunStartSession");
            readEvent = Native.WintunGetReadWaitEvent(session);
            if (readEvent == 0) throw Error("WintunGetReadWaitEvent");
        }
        catch
        {
            if (session != 0) Native.WintunEndSession(session);
            Native.WintunCloseAdapter(adapter);
            throw;
        }
    }

    internal async Task ConfigureAsync(IPAddress[] addresses, CancellationToken token)
    {
        foreach (var address in addresses)
        {
            if (Tun.IsV4(address))
                await Tun.CommandAsync("netsh", token, "interface", "ipv4", "add", "address",
                    $"name={name}", $"address={address}", "mask=255.255.255.255", "store=active");
            else
                await Tun.CommandAsync("netsh", token, "interface", "ipv6", "add", "address",
                    $"interface={name}", $"address={address}/128", "store=active");
        }
    }

    public Task AddRouteAsync(IPAddress address, CancellationToken token) => Tun.CommandAsync("netsh", token,
        "interface", Tun.IsV4(address) ? "ipv4" : "ipv6", "add", "route",
        $"prefix={address}/{(Tun.IsV4(address) ? 32 : 128)}", $"interface={name}", "store=active");

    public Task<byte[]> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            nint packet = Native.WintunReceivePacket(session, out uint size);
            if (packet != 0)
            {
                try
                {
                    byte[] result = new byte[checked((int)size)];
                    Marshal.Copy(packet, result, 0, result.Length);
                    return result;
                }
                finally { Native.WintunReleaseReceivePacket(session, packet); }
            }
            if (Marshal.GetLastPInvokeError() != 259) throw Error("WintunReceivePacket");
            uint wait = Native.WaitForSingleObject(readEvent, 200);
            if (wait is not (0 or 258)) throw Error("Waiting for Wintun packets");
        }
    }, token);

    public async Task WriteAsync(byte[] packet, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            nint destination = Native.WintunAllocateSendPacket(session, (uint)packet.Length);
            if (destination != 0)
            {
                Marshal.Copy(packet, 0, destination, packet.Length);
                Native.WintunSendPacket(session, destination);
                return;
            }
            if (Marshal.GetLastPInvokeError() != 111) throw Error("WintunAllocateSendPacket");
            await Task.Delay(2, token);
        }
    }

    private static IOException Error(string operation) => new($"{operation}: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Native.WintunEndSession(session);
        Native.WintunCloseAdapter(adapter);
    }

    private static class Native
    {
        [DllImport("wintun", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint WintunCreateAdapter(string name, string tunnelType, nint requestedGuid);
        [DllImport("wintun")] internal static extern void WintunCloseAdapter(nint adapter);
        [DllImport("wintun", SetLastError = true)] internal static extern nint WintunStartSession(nint adapter, uint capacity);
        [DllImport("wintun")] internal static extern void WintunEndSession(nint session);
        [DllImport("wintun", SetLastError = true)] internal static extern nint WintunGetReadWaitEvent(nint session);
        [DllImport("wintun", SetLastError = true)] internal static extern nint WintunReceivePacket(nint session, out uint size);
        [DllImport("wintun")] internal static extern void WintunReleaseReceivePacket(nint session, nint packet);
        [DllImport("wintun", SetLastError = true)] internal static extern nint WintunAllocateSendPacket(nint session, uint size);
        [DllImport("wintun")] internal static extern void WintunSendPacket(nint session, nint packet);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    }
}
