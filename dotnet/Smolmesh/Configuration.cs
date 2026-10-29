using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smolmesh;

internal sealed class Configuration
{
    [JsonPropertyName("node_name")] public string NodeName { get; set; } = "";
    [JsonPropertyName("ipv4_addresses")] public string[] IPv4Addresses { get; set; } = [];
    [JsonPropertyName("linux_tuntap")] public bool LinuxTun { get; set; }
    [JsonPropertyName("macos_tuntap")] public bool MacTun { get; set; }
    [JsonPropertyName("windows_tuntap")] public bool WindowsTun { get; set; }
    [JsonPropertyName("transports")] public TransportConfiguration[] Transports { get; set; } = [];

    internal static Configuration Load(string path)
    {
        // Validation happens once, in the MeshNode constructor.
        return JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Configuration must be a JSON object.");
    }

    internal void Validate()
    {
        if (string.IsNullOrEmpty(NodeName)) throw new InvalidDataException("node_name must not be empty.");
        if (Encoding.UTF8.GetByteCount(NodeName) > ushort.MaxValue - 9)
            throw new InvalidDataException("node_name exceeds the legacy packet size limit.");
        if (WindowsTun && NodeName.Length > 119)
            throw new InvalidDataException("Windows adapter names cannot exceed 128 characters including the smolmesh- prefix.");
        if (IPv4Addresses is null || Transports is null) throw new InvalidDataException("Address and transport arrays cannot be null.");
        foreach (string address in IPv4Addresses)
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidDataException($"Invalid IPv4 address: {address}");
        foreach (var transport in Transports)
        {
            if (transport is null || transport.Port is < 1 or > 65535)
                throw new InvalidDataException("Every transport needs a port between 1 and 65535.");
            switch (transport.Type)
            {
                case "legacy_tcp_connect" when !string.IsNullOrWhiteSpace(transport.Host) && transport.Key is not null:
                    break;
                case "legacy_tcp_listen" when transport.Keys is { Length: > 0 } && transport.Keys.All(k => k is not null):
                    break;
                default:
                    throw new InvalidDataException($"Invalid transport configuration: {transport.Type}");
            }
        }
        if ((LinuxTun && !OperatingSystem.IsLinux()) || (MacTun && !OperatingSystem.IsMacOS())
            || (WindowsTun && !OperatingSystem.IsWindows()))
            throw new PlatformNotSupportedException("The enabled TUN adapter does not match this operating system.");
    }
}

internal sealed class TransportConfiguration
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("host")] public string Host { get; set; } = "";
    [JsonPropertyName("port")] public int Port { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("keys")] public string[] Keys { get; set; } = [];
}
