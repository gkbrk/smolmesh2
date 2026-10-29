# smolmesh2

smolmesh2 is a peer-to-peer mesh VPN. You run a node on each machine and
connect the nodes over TCP. Not every pair needs a direct connection: traffic
is relayed through other nodes, so every node can reach every other node by IP
address.

This repo has two implementations. They speak the same protocol and read the
same config, so you can mix them in one network.

- [rust/](rust/) is the original version, for Linux and macOS.
- [dotnet/](dotnet/) is a C# port for .NET 10, for Linux, macOS and Windows.

The README in each folder explains how to build and run that version.

## How it works

Each node has a name, and its IPv6 address (in `fd00::/16`) is derived from
that name. A node can also claim IPv4 addresses.

Nodes are linked by TCP connections. A node can listen for links, connect out
to other nodes, or both. Each link is encrypted and authenticated with a shared
key, and outgoing links reconnect when they drop.

Every few seconds each node announces its name and addresses, and the
announcement is passed on to every node in the mesh. A node remembers which
link each announcement arrived on first, and sends packets for that address
back over the same link. That way traffic takes the fastest path, and routes
update by themselves as links come and go.

With a TUN interface enabled, the mesh shows up as a normal network interface
that any program can use. Without one, the node only relays traffic for others
and doesn't need root.

## Configuration

`meshnode` takes a JSON config file:

```json
{
  "node_name": "laptop",
  "ipv4_addresses": ["10.42.0.1"],
  "linux_tuntap": true,
  "transports": [
    {
      "type": "legacy_tcp_listen",
      "port": 54321,
      "keys": ["long-random-secret"]
    },
    {
      "type": "legacy_tcp_connect",
      "host": "203.0.113.10",
      "port": 54321,
      "key": "long-random-secret"
    }
  ]
}
```

- `node_name` has to be unique in the mesh, because the node's IPv6 address is
  derived from it.
- `ipv4_addresses` is an optional list of IPv4 addresses for this node.
- `linux_tuntap`, `macos_tuntap` and `windows_tuntap` create a TUN interface.
  Set the one for your OS. This needs root, or Administrator on Windows.
- `transports` lists the node's links:
  - `legacy_tcp_listen` accepts links on `port` over IPv4. The other node has
    to use one of the `keys`.
  - `legacy_tcp_connect` connects to `host` and `port` with `key`. The Rust
    version needs an IPv4 address for `host`. The .NET version also accepts
    hostnames and IPv6 addresses.

## Commands

- `meshnode <config-file>` runs a node.
- `name-to-ipv6 <node-name>` prints the IPv6 address for a node name.
- `make-random-ipv4` prints a random IPv4 address.
- `make-random-ipv6` prints a random IPv6 address in `fd00::/16`.

## Security

Encryption is per link, not end to end, so any node that relays your traffic
can read it. Nodes don't prove who they are, so any node in the mesh can claim
another node's name or addresses and receive its traffic.
