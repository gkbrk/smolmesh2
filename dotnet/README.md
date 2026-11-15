# smolmesh2 (.NET)

A C# port of the Rust node for .NET 11. It runs on Linux, macOS and Windows,
and works in the same mesh as Rust nodes.

See the [main README](../README.md) for how smolmesh2 works and how to write a
config file.

## Build and run

From the repository root, with the .NET 11 SDK installed:

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

The output is in `dotnet/Smolmesh/bin/Release/net11.0/publish/`. Run the
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
- The TUN interface has an MTU of 1420, so that packets fit one UDP datagram.
- The `udp1` transport, below. Rust nodes don't have it.

## UDP transport

`udp1` links .NET nodes over UDP. Both ends of a link are alike: a node has
one socket on `port` and a list of peers, each a secret `key` and maybe a
`host` and `port` to say hello to.

```json
{ "type": "udp1", "port": 6969, "peers": [
    { "key": "secret for rath", "host": "rath.example.com", "port": 6969 },
    { "key": "secret for laptops" }
] }
```

Two nodes that people connect to, such as ch01 and rath, list each other
with a host and the same key, and get one link between them, whichever says
hello first. A node that nobody says hello to, such as a laptop, can set
`port` to 0 and take any free port.

Each mesh packet is one datagram: the packet enciphered with LION
(`Crypto/Lion.cs`) under a key derived from the shared secret. The datagram is
exactly as long as the packet and looks like random bytes. There is no
nonce or tag. LION needs at least 17 bytes, so the few
packets shorter than that (announcements of an IPv4 address or of a name under
8 bytes) are filled up to 17.

- A node says hello (a link-only packet with its random link id, 17 bytes) to
  each of its hosts from its one socket: every 5 seconds until they are
  linked, then every 30 seconds. It answers every hello under one of its keys,
  from any address. Hellos and answers are not passed on to the mesh.
- A node has one link id for as long as it runs, and keeps one link per link
  id it hears, up to 256. Any number of nodes can share a key. A node learns
  the address of a link from its hellos and answers, and one from a new
  address, newer than the last one for that id, moves the link. Data from an
  address without a link is ignored.
- Hellos are enciphered under a key of the secret alone, data under a key of
  the secret and both link ids, a different one for each direction. A node
  ignores its own hellos and data sent back to it, and data from an earlier
  run of either node.
- A link that hears nothing for 3 minutes is dropped.
- A node can have TCP and UDP links to the same neighbour. Routes then follow
  whichever link delivers announcements first, and the other is the fallback.
- A datagram is authenticated only by decrypting to a mesh packet with a
  current timestamp. A captured datagram can be replayed for as long as its
  timestamp is accepted, three minutes, once the mesh has forgotten the packet.
  A replay can't start or move a link: only a hello does that, and it has to be
  newer than the last hello of its link, which the node remembers for as
  long as a replay of it would be accepted.
- The cipher has had no outside review.

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

## Crypto benchmarks

```sh
dotnet run --project dotnet/Smolmesh.Benchmarks -c Release -- --filter '*'
```

BenchmarkDotNet runs a short job at 64, 256, and 1500 bytes, with allocation
measurements. `PacketBenchmarks` compares the existing Gimli MAC + keystream
operations (including the encrypted length prefix) against Bouncy Castle's
standardized Ascon-AEAD128, including per-packet initialization and tag generation
(no AAD or length prefix). Both reuse output buffers; session key derivation is
excluded. This measures encryption/tag generation, not receive-side verification.

`DuplexBenchmarks` compares persistent-state encryption block loops, excluding
initialization, record framing, padding, and tags. Both process 16-byte blocks,
so the 1500-byte case processes 1504 bytes. This estimates steady-state throughput
only: it is **not** an implementation of a secure stateful record protocol.
Ascon uses the library's streaming API; Gimli uses the existing SIMD permutation
with an experimental absorb-and-output loop. No production crypto is changed.

API checks run before benchmarking; run just those with `-- --verify`.

Both Ascon implementations are managed C#: Bouncy Castle does not call native
C/C++ here. `AsconSimplePacket` and `AsconSimpleDuplexBlocks` use `AsconSpec.cs`,
a small scalar implementation based on NIST SP 800-232 using `ulong`, XOR, AND,
and `BitOperations.RotateRight`. It supports encryption with associated data,
but not decryption, and is benchmark-only, not audited production crypto. Before
benchmarking, ciphertext and tags are compared against Bouncy Castle across 140
message/AAD boundary cases, and persistent block output is compared too.
