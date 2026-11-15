using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Smolmesh;

internal interface ITun : IDisposable
{
    Task<byte[]> ReadAsync(CancellationToken token);
    Task WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken token);
    Task AddRouteAsync(IPAddress address, CancellationToken token);
}

internal static class Tun
{
    // As WireGuard: a full-size packet, its mesh header and the UDP and IPv6 headers around it
    // fit a 1500-byte link with room to spare, so UDP transports need not fragment.
    internal const int Mtu = 1420;

    internal static async Task<ITun?> OpenAsync(Configuration config, IPAddress nodeAddress, CancellationToken token)
    {
        ITun? tun = config.LinuxTun || config.MacTun ? UnixTun.Open()
            : config.WindowsTun ? new WindowsTun(config.NodeName) : null;
        if (tun is null) return null;
        try
        {
            IPAddress[] addresses = [nodeAddress, .. config.IPv4Addresses.Select(IPAddress.Parse)];
            if (tun is UnixTun unix) await unix.ConfigureAsync(addresses, token);
            else if (tun is WindowsTun windows) await windows.ConfigureAsync(addresses, token);
            return config.MacTun ? new MacTunLoopback(tun, nodeAddress) : tun;
        }
        catch
        {
            tun.Dispose();
            throw;
        }
    }

    internal static bool IsV4(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork;

    // ArgumentList avoids shell interpolation of names/addresses from configuration.
    internal static async Task CommandAsync(string executable, CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Cannot start {executable}.");
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stderr, stdout); } catch (OperationCanceledException) { }
            throw;
        }
        string error = await stderr;
        string output = await stdout;
        if (process.ExitCode != 0)
            throw new IOException($"{executable} exited {process.ExitCode}: {error.Trim()} {output.Trim()}");
    }
}
