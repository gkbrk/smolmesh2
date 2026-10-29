# smolmesh2 (Rust)

The original smolmesh2 node, for Linux and macOS. It uses its own small async
runtime built on `poll()`, in `src/leo_async.rs`.

See the [main README](../README.md) for how smolmesh2 works and how to write a
config file.

## Build

```sh
cargo build --release
```

The binary is `target/release/smolmesh2`. To cross-compile a static Linux
binary, see [doc/cross-platform-linux-build.md](doc/cross-platform-linux-build.md).

Run the tests with `cargo test`.

## Run

```sh
sudo target/release/smolmesh2 meshnode config.json
```

Root is only needed for the TUN interface. Without `linux_tuntap` or
`macos_tuntap`, the node runs as a normal user and only relays traffic.

`host` in a `legacy_tcp_connect` transport has to be an IPv4 address.
Hostnames and IPv6 addresses don't work.

The other commands (`name-to-ipv6`, `make-random-ipv4`, `make-random-ipv6` and
`help`) print their result but don't exit. Stop them with Ctrl+C.

## Platforms

- **Linux** on x86_64 or aarch64. `linux_tuntap` creates an interface named
  `smolmesh1` through `/dev/net/tun` and sets it up with the `ip` command from
  iproute2.
- **macOS**. `macos_tuntap` uses the first free `utun` interface and sets it up
  with `ifconfig` and `route`. The interface only gets the node's IPv6 address.
  `ipv4_addresses` are announced to the mesh but not added to the interface.
- **Windows** isn't supported. The code uses Unix APIs, and
  `src/windows_tuntap.rs` and `src/wintun.rs` are from an older version and
  don't compile. Use the [.NET version](../dotnet/) on Windows.

## Profiling on macOS

Sign the binary with `Entitlements.plist` before profiling it. See
[doc/mac-profiling.md](doc/mac-profiling.md).
