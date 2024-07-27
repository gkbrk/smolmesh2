# Smolmesh2

Smolmesh2 is a mesh networking tool written in Rust that allows nodes to communicate over various transports while maintaining a decentralized and resilient network structure. It supports Linux and Windows operating systems and includes several key features to facilitate seamless communication and network stability.

## Features

- **IPv4 and IPv6 Support:** Handles both IPv4 and IPv6 addresses.
- **Multiple Transports:** Supports legacy TCP connections, both for connecting and listening.
- **TUN/TAP Interface:** Integrates with TUN/TAP interfaces on both Linux and Windows to manage network traffic.
- **Packet Routing and Broadcasting:** Efficiently routes packets to their destinations and broadcasts node availability.

## Commands

- **meshnode:** Runs the mesh node with the specified configuration.
- **name-to-ipv6:** Converts a node name to its corresponding IPv6 address.
- **make-random-ipv4:** Generates a random IPv4 address.
- **make-random-ipv6:** Generates a random IPv6 address.

## Usage

To start a mesh node, run:
```
cargo run -- meshnode config.json
```
This will initialize the node using the configuration specified in `config.json`.

## Configuration

The configuration file (`config.json`) should include details such as node name, IP addresses, and transport methods. Below is an example configuration:

```json
{
  "node_name": "example_node",
  "ipv4_addresses": ["192.168.1.1"],
  "transports": [
    {
      "type": "legacy_tcp_connect",
      "host": "example.com",
      "port": 12345,
      "key": "your_key_here"
    },
    {
      "type": "legacy_tcp_listen",
      "port": 54321,
      "keys": ["key1", "key2"]
    }
  ],
  "linux_tuntap": true,
  "windows_tuntap": false
}
```

## Contributing

Contributions are welcome! Please open an issue or submit a pull request on GitHub. For major changes, please discuss them in an issue first to ensure they align with the project's goals.

Enjoy using SmolMesh2 to build decentralized and resilient networks!
