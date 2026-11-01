using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Smolmesh;
using Xunit;

/// <summary>
/// Opt-in live interoperability tests. Set SMOLMESH_RUST_BINARY to the Rust
/// executable (e.g. target/debug/smolmesh2); otherwise they pass without running.
/// </summary>
public class RustInteropTests
{
    private static string? Executable
    {
        get
        {
            string? path = Environment.GetEnvironmentVariable("SMOLMESH_RUST_BINARY");
            return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
        }
    }

    [Fact]
    public async Task RustCliAddressCompatibilityIncludingUtf8()
    {
        if (Executable is not { } executable) return;
        foreach (string name in new[] { "", "test", "example_node", "smölmesh-🌐", new string('x', 100) })
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("name-to-ipv6");
            start.ArgumentList.Add(name);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                // The Rust executor keeps running even after a CLI command completes.
                string? line;
                do { line = await process.StandardOutput.ReadLineAsync(timeout.Token); }
                while (line is not null && !IPAddress.TryParse(line, out _));
                Assert.Equal(Protocol.FormatAddress(Protocol.NodeAddress(name)), line);
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await stderr;
            }
        }
    }

    [Fact]
    public async Task RustListenerDotNetConnectorInteroperability()
    {
        if (Executable is { } executable) await RustInterop(executable, true);
    }

    [Fact]
    public async Task DotNetListenerRustConnectorInteroperability()
    {
        if (Executable is { } executable) await RustInterop(executable, false);
    }

    private static async Task RustInterop(string executable, bool rustListens)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        string path = Path.Combine(Path.GetTempPath(), $"smolmesh-interop-{Guid.NewGuid():N}.json");
        var rustTransport = rustListens
            ? new TransportConfiguration { Type = "legacy_tcp_listen", Port = port, Keys = ["wrong", "interop-secret"] }
            : new TransportConfiguration { Type = "legacy_tcp_connect", Port = port, Host = "127.0.0.1", Key = "interop-secret" };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Configuration { NodeName = "rust-interop", ControlPort = 0, Transports = [rustTransport] }));
        var incoming = Channel.CreateUnbounded<(byte[] Packet, Peer Peer)>();
        var routing = new Routing();
        Task? transport = null;
        Process? process = null;
        Task<string>? stdout = null, stderr = null;
        TcpClient? client = null;
        try
        {
            if (!rustListens)
                transport = LegacyTcp.ListenAsync(new TransportConfiguration { Port = port, Keys = ["interop-secret"] }, routing, incoming.Writer, stop.Token);
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("meshnode");
            start.ArgumentList.Add(path);
            process = Process.Start(start)!;
            stdout = process.StandardOutput.ReadToEndAsync();
            stderr = process.StandardError.ReadToEndAsync();
            if (rustListens)
            {
                while (true)
                {
                    client = new TcpClient();
                    try { await client.ConnectAsync(IPAddress.Loopback, port, stop.Token); break; }
                    catch (SocketException) { client.Dispose(); await Task.Delay(25, stop.Token); }
                }
                transport = LegacyTcp.RunAsync(client, ["interop-secret"u8.ToArray()], true, routing, incoming.Writer, stop.Token);
            }
            var announcement = await WaitFor(p => p.Length > 9 && p[8] == 0 && Encoding.UTF8.GetString(p, 9, p.Length - 9) == "rust-interop");
            // Rust floods this exact .NET-originated frame back, proving receive/MAC/decrypt and encrypt/send.
            byte[] flood = Protocol.Packet(0, "dotnet-interop-🌐"u8);
            announcement.Peer.Send(flood);
            var echo = await WaitFor(p => p.SequenceEqual(flood));
            Assert.Equal(flood, echo.Packet);

            async Task<(byte[] Packet, Peer Peer)> WaitFor(Func<byte[], bool> predicate)
            {
                while (true)
                {
                    var item = await incoming.Reader.ReadAsync(stop.Token);
                    if (predicate(item.Packet)) return item;
                }
            }
        }
        catch
        {
            if (process is not null && process.HasExited)
                Console.Error.WriteLine($"Rust exited {process.ExitCode}: {await stdout!} {await stderr!}");
            throw;
        }
        finally
        {
            stop.Cancel();
            if (transport is not null) await TestSupport.IgnoreCancellation(transport);
            client?.Dispose();
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                if (stdout is not null && stderr is not null) await Task.WhenAll(stdout, stderr);
                process.Dispose();
            }
            File.Delete(path);
        }
    }
}
