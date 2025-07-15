# Smolmesh2

Welcome to Smolmesh2! 🌐

Smolmesh2 is a powerful mesh networking tool written in Rust that allows nodes to communicate over various transports while maintaining a decentralized and resilient network structure. It supports Linux and Windows operating systems and includes several key features to facilitate seamless communication and network stability.

## Features

- **IPv4 and IPv6 Support:** Handles both IPv4 and IPv6 addresses with automatic route management
- **Multiple Transports:** Supports legacy TCP connections, both for connecting and listening using encrypted channels
- **TUN/TAP Interface:** Integrates with TUN/TAP interfaces on Linux and Wintun on Windows to manage network traffic
- **Packet Routing and Broadcasting:** Efficiently routes packets to their destinations using fastest path discovery and broadcasts node availability
- **Automatic Path Discovery:** Discovers and maintains optimal paths between nodes
- **Encrypted Communications:** All communications are encrypted using the SPECK cipher for lightweight but effective security
- **Duplicate Packet Detection:** Prevents packet loops using efficient packet tracking with hash-based detection
- **Asynchronous Architecture:** Custom async runtime (leo_async) for efficient I/O operations
- **Cross-Platform Support:** Works on both Linux and Windows with platform-specific networking implementations

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
  "linux_tuntap": true,
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
  ]
}
```

Optional fields:
- `ipv4_addresses`: Array of IPv4 addresses this node should handle (e.g., `["192.168.1.1"]`)
- `windows_tuntap`: Boolean to enable Windows TUN/TAP support (default: false)

## Platform-Specific Features

### Linux
- Uses native TUN/TAP interfaces for virtual network adapters
- Automatically configures IP routes and interface settings
- Requires TUN/TAP kernel support

### Windows
- Uses Wintun driver for virtual network adapter functionality
- Automatically configures IPv6 addresses for the virtual adapter
- Requires the Wintun driver (wintun-amd64.dll) in the working directory

## Technical Details

### Network Protocol
- Uses a custom packet format with timestamp, command type, and payload
- Supports node discovery, IP address broadcasting, and data transfer
- Implements duplicate packet detection to prevent loops

### Security
- All transport connections are encrypted using the SPECK cipher
- Uses challenge-response authentication for connection establishment
- Implements MAC (Message Authentication Code) for packet integrity

### Networking
- Custom async networking stack for efficient I/O operations
- Supports non-blocking socket operations
- Implements timeout handling for network operations

## Dependencies

### System Requirements
- **Linux:** TUN/TAP kernel support (usually available by default)
- **Windows:** Wintun driver (wintun-amd64.dll) in the working directory

## Contributing

Contributions are welcome! Please open an issue or submit a pull request on GitHub. For major changes, please discuss them in an issue first to ensure they align with the project's goals.

Enjoy using Smolmesh2 to build decentralized and resilient networks!
