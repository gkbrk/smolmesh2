# Smolmesh2

Welcome to Smolmesh2! 🌐

Smolmesh2 is a powerful mesh networking tool written in Rust that allows nodes to communicate over various transports while maintaining a decentralized and resilient network structure. It supports Linux and Windows operating systems and includes several key features to facilitate seamless communication and network stability.

## Features

- **IPv4 and IPv6 Support:** Handles both IPv4 and IPv6 addresses with automatic route management
- **Multiple Transports:** Supports legacy TCP connections, both for connecting and listening using encrypted channels
- **TUN/TAP Interface:** Integrates with TUN/TAP interfaces on Linux and Wintun on Windows to manage network traffic
- **Packet Routing and Broadcasting:** Efficiently routes packets to their destinations using fastest path discovery and broadcasts node availability
- **Automatic Path Discovery:** Discovers and maintains optimal paths between nodes
- **Encrypted Communications:** All communications are encrypted using the SPECK cipher
- **Duplicate Packet Detection:** Prevents packet loops using efficient packet tracking

## Commands

- **help, --help:** Show help message with available commands
- **meshnode [config-file]:** Runs the mesh node with the specified configuration file
- **name-to-ipv6 [node-name]:** Converts a node name to its corresponding IPv6 address
- **make-random-ipv4:** Generates a random IPv4 address
- **make-random-ipv6:** Generates a random IPv6 address with fd00::/8 prefix

## Usage

To start a mesh node, run:
```bash
cargo run -- meshnode config.json
```
This will initialize the node using the configuration specified in `config.json`. See the [Configuration](#configuration) section below for details.

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
      "key": "encryption_key_here"
    },
    {
      "type": "legacy_tcp_listen",
      "port": 54321,
      "keys": ["allowed_key1", "allowed_key2"]
    }
  ],
  "linux_tuntap": true,
  "windows_tuntap": false
}
```

Note that on Windows, the Wintun driver (wintun-amd64.dll) must be present in the working directory for TUN/TAP functionality. See the [Dependencies](#dependencies) section for more details.

## Dependencies

- Linux: TUN/TAP kernel support
- Windows: Wintun driver

## Contributing

Contributions are welcome! Please open an issue or submit a pull request on GitHub. For major changes, please discuss them in an issue first to ensure they align with the project's goals.

Enjoy using SmolMesh2 to build decentralized and resilient networks!
