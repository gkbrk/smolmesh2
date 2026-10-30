using Serilog;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Smolmesh.Tests")]

namespace Smolmesh;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var sigterm = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM,
            context => { context.Cancel = true; stop.Cancel(); });
        Logging.Configure();
        return await RunAsync(args, stop.Token);
    }

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        try
        {
            switch (args.FirstOrDefault() ?? "help")
            {
                case "help":
                case "--help":
                    Console.WriteLine("""
                        smolmesh - peer-to-peer mesh VPN (.NET)

                        meshnode <config-file>       Run a mesh node
                        name-to-ipv6 <node-name>     Derive the node's IPv6 address
                        make-random-ipv4             Generate a random IPv4 address
                        make-random-ipv6             Generate an address with fd00::/16 prefix
                        """);
                    break;
                case "name-to-ipv6" when args.Length == 2:
                    Console.WriteLine(Protocol.FormatAddress(Protocol.NodeAddress(args[1])));
                    break;
                case "make-random-ipv4":
                    Console.WriteLine(new IPAddress(RandomNumberGenerator.GetBytes(4)));
                    break;
                case "make-random-ipv6":
                    byte[] bytes = RandomNumberGenerator.GetBytes(16);
                    bytes[0] = 0xfd;
                    bytes[1] = 0;
                    Console.WriteLine(Protocol.FormatAddress(new IPAddress(bytes)));
                    break;
                case "meshnode" when args.Length == 2:
                    await new MeshNode(Configuration.Load(args[1])).RunAsync(token);
                    break;
                default:
                    throw new ArgumentException($"Unknown command or missing argument: {string.Join(' ', args)}. Use --help.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            Log.Error("{Message}", ex.Message);
            return 1;
        }
    }
}
