# smolmesh2 (.NET)

A C# port of the Rust node for .NET 10. It runs on Linux, macOS and Windows,
and works in the same mesh as Rust nodes.

See the [main README](../README.md) for how smolmesh2 works and how to write a
config file.

## Build and run

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet build dotnet/Smolmesh.slnx -c Release
dotnet run --project dotnet/Smolmesh -c Release -- meshnode config.json
```

To create a TUN interface, the node has to run as root, or as Administrator on
Windows. Ctrl+C or SIGTERM stops it. Logs go to stderr and show connections,
disconnects and received announcements.

To publish a build that uses the installed .NET runtime:

```sh
dotnet publish dotnet/Smolmesh -c Release --no-self-contained
```

The output is in `dotnet/Smolmesh/bin/Release/net10.0/publish/`. Run the
`smolmesh` executable there, or `dotnet smolmesh.dll`.

### macOS setuid build

```sh
./dotnet/publish-macos.sh
./dotnet/publish/smolmesh meshnode config.json
```

The script builds a self-contained single-file binary for your Mac at
`dotnet/publish/smolmesh`. It then uses `sudo` to make the binary owned by root
with the setuid bit, so the node can create its TUN interface without `sudo`.
Run the script as your normal user, and again after code changes.
`--publish-only` skips the `sudo` step.

This is a development shortcut, not a safe setuid install. Anyone who can run
the binary can use it to get root, for example by changing `PATH` or setting
.NET environment variables.

## Differences from the Rust version

- `host` can also be a hostname or an IPv6 address.
- `legacy_tcp_listen` accepts IPv4 and IPv6 connections on the same port, or
  only IPv4 if the host has no IPv6. Rust listens on IPv4 only.
- Setting the TUN option for another OS is an error. Rust ignores it.
- If a listener can't bind its port, the node exits with an error. Rust keeps
  retrying.
- On macOS, the `ipv4_addresses` are added to the interface too.

## TUN interface requirements

- **Linux**: root, `/dev/net/tun` and the `ip` command from iproute2. The
  interface is named `smolmesh1`.
- **macOS**: root. The node uses a free `utun` interface and sets it up with
  `ifconfig` and `route`.
- **Windows**: Administrator, `netsh`, and the [Wintun](https://www.wintun.net/)
  DLL for your CPU. Put `wintun.dll` (or `wintun-amd64.dll`, `wintun-arm64.dll`
  or `wintun-x86.dll`) next to the executable or in the working directory. The
  interface is named `smolmesh-<node_name>`.

The node adds its own addresses to the interface, and a route for each address
other nodes announce.

## Tests

```sh
dotnet test dotnet/Smolmesh.slnx
```

To also test against the Rust version, set `SMOLMESH_RUST_BINARY` to a Rust
build. Without it, those tests pass without doing anything.

```sh
cargo build --manifest-path rust/Cargo.toml
SMOLMESH_RUST_BINARY=$PWD/rust/target/debug/smolmesh2 dotnet test dotnet/Smolmesh.Tests
```

The tests don't create real network interfaces, so TUN setup is only tested by
running a node as root.
