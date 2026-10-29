# smolmesh for .NET

A C#/.NET 10 port of the Rust node. It depends on Serilog (logging) and
System.IO.Hashing (duplicate-packet fingerprints) from NuGet; the test project
additionally uses xunit and Microsoft.NET.Test.Sdk.
The Rust implementation remains unchanged.

## Build and run

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet build dotnet/Smolmesh.slnx -c Release
dotnet run --project dotnet/Smolmesh -c Release -- --help
dotnet run --project dotnet/Smolmesh -c Release -- name-to-ipv6 example_node
dotnet run --project dotnet/Smolmesh -c Release -- meshnode config.json
```

All Rust CLI commands are available: `meshnode`, `name-to-ipv6`,
`make-random-ipv4`, `make-random-ipv6`, `help`, and `--help`.
Ctrl+C and Unix SIGTERM cancel the node and close its sockets/adapters.
Serilog writes timestamped, levelled logs to stderr (info, warning, error). They report authenticated connections, disconnects, and
received node-name/IPv4/IPv6 tracers. Duplicate, stale, or malformed tracers
are not logged, and ordinary data packets stay quiet.

For a framework-dependent deployment:

```sh
dotnet publish dotnet/Smolmesh -c Release --no-self-contained
```

Run the published `smolmesh` executable, or `dotnet smolmesh.dll`.
Self-contained publishing is also possible; NuGet downloads the runtime pack.

### macOS single-file publish with setuid

```sh
./dotnet/publish-macos.sh
./dotnet/publish/smolmesh meshnode config.json
```

Run the script as your normal user, from any working directory. It detects
Apple Silicon or Intel, publishes a self-contained single executable (like
`gkbrk_com`), then uses `sudo` to apply `root:wheel` ownership and mode `4755`.
Rerun it after source changes; it replaces the old binary and reapplies the
permissions. `--publish-only` skips sudo and leaves a normal executable.
Microsoft's runtime/build packs are downloaded from NuGet during publish.

This is the requested development convenience, **not a hardened setuid
installation**: the output directory remains user-writable, the application
resolves OS utilities through `PATH`, and the single-file runtime extracts
native libraries at startup. Only use it where those root-execution risks are
acceptable. Single-file does not mean extraction-free or Native AOT.

## Configuration

The port accepts the existing snake_case JSON configuration:

```json
{
  "node_name": "example_node",
  "ipv4_addresses": ["10.42.0.1"],
  "linux_tuntap": false,
  "macos_tuntap": false,
  "windows_tuntap": false,
  "transports": [
    {
      "type": "legacy_tcp_listen",
      "port": 54321,
      "keys": ["replace-with-a-long-random-shared-secret"]
    },
    {
      "type": "legacy_tcp_connect",
      "host": "127.0.0.1",
      "port": 12345,
      "key": "replace-with-a-long-random-shared-secret"
    }
  ]
}
```

Enable only the adapter flag for your operating system. With no adapter
enabled, the node can still discover peers and relay traffic, without root.
Connectors accept DNS names and IP literals (IPv6 literals without brackets).
Listeners bind all IPv4 interfaces, matching Rust. Outbound connections retry
after a randomized 5–10 second delay; a failed listener bind fails the node
with a diagnostic rather than silently running without that listener.

## Native adapter requirements

- **Linux:** `/dev/net/tun`, root or appropriate capabilities, and `ip`
  (iproute2) on `PATH`. Creates `smolmesh1`, assigns local /128 IPv6 and /32
  IPv4 addresses, and installs discovered host routes.
- **macOS:** root, built-in `ifconfig` and `route` on `PATH`. Opens an
  available native `utun` control socket directly, including its four-byte
  address-family header; no third-party TUN library is needed.
- **Windows:** administrator privileges, `netsh`, and the official Wintun DLL
  matching the process architecture. Place `wintun.dll` (or
  `wintun-amd64.dll` / `wintun-arm64.dll` / `wintun-x86.dll`) beside the
  executable or in the working directory. Creates `smolmesh-<node_name>`,
  configures addresses, and installs discovered host routes. The driver is
  an unavoidable native requirement, not a managed package dependency.

Adapter creation, configuration, and route commands report failures. Only
successfully added routes are cached; failed additions retry on subsequent
announcements. As in Rust, discovered routes are not aged out while the node
runs. Adapter I/O is stopped before native handles are released.

## Compatibility and implementation

- Preserves SPECK-derived node addresses, UTF-8 names, little-endian
  timestamps and frame lengths, discovery commands 0–2, and IP payload
  command 3.
- Preserves the actual Rust TCP protocol: Gimli keyfinder/key derivation,
  a continuous Gimli keystream per direction, and a 16-byte per-packet MAC.
  Despite the root README's description, SPECK is used for node addresses,
  not current TCP encryption.
- Uses `Task`, async sockets, `System.Threading.Channels`, `System.Text.Json`,
  OS-backed cryptographic randomness, and direct native adapter interop.
- Keeps first-arrival routing for each announcement timestamp and the
  three-minute freshness window. Local duplicate fingerprints use 64-bit
  XxHash3 instead of FxHasher; fingerprints are never transmitted.
- Uses unbounded outgoing peer queues, matching Rust, so transfer bursts do
  not disconnect peers. Slow peers can accumulate buffered packets and memory;
  byte-budgeted backpressure is not implemented yet.
- Validates truncated packets before inspecting IP headers, compares MACs
  in constant time, and removes routes through closed
  peers. These are local safety changes, not wire-format changes.

The legacy custom cryptographic protocol is retained for interoperability;
this port does **not** turn it into an audited, modern VPN security protocol.
Use it only with trusted peers and protect the configuration's shared keys.

## Tests

The tests are xunit unit tests, split by area (`CryptoTests`, `ProtocolTests`,
`RoutingTests`, `ConfigurationTests`, `LegacyTcpTests`, `MeshNodeTests`,
`NativeTests`, `RustInteropTests`):

```sh
dotnet test dotnet/Smolmesh.slnx
```

For live interoperability with the original implementation, set
`SMOLMESH_RUST_BINARY` (the Rust tests otherwise do nothing and pass):

```sh
cargo build
SMOLMESH_RUST_BINARY=$PWD/target/debug/smolmesh2 dotnet test dotnet/Smolmesh.Tests
```

Tests cover hash vectors, UTF-8 node addressing, keystream boundaries, framing,
duplicate eviction, timestamps, routing, malformed packets, local delivery,
configuration, bidirectional TCP, multiple keys, rejected keys/tampered MACs,
cancellation, and IPv4/IPv6 forwarding through a running mesh node. The optional
Rust checks exercise both listener/connector
roles and verify encrypted round-trip discovery floods. On macOS, an
unprivileged test also checks the control-socket native ABI without creating
an adapter.

These tests do **not** create privileged network adapters. End-to-end TUN
traffic and route setup on Linux, macOS, and Windows still require validation
on those operating systems with administrator privileges.
