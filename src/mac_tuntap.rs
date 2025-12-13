use std::os::fd::AsRawFd;

use bytes::{BufMut, Bytes, BytesMut};
use tun_rs::{DeviceBuilder, SyncDevice};

use crate::{DSSResult, leo_async::{self, ArcFd}};

pub(crate) struct TunInterface {
  _device: SyncDevice,
  fd: ArcFd,
  name: String,
}

impl TunInterface {
  fn make_nonblocking(raw_fd: i32) {
    unsafe {
      let flags = libc::fcntl(raw_fd, libc::F_GETFL);
      if flags == -1 {
        crate::warn!("fcntl(F_GETFL) failed for utun fd");
        return;
      }

      if libc::fcntl(raw_fd, libc::F_SETFL, flags | libc::O_NONBLOCK) == -1 {
        crate::warn!("fcntl(F_SETFL) failed for utun fd");
      }
    }
  }

  fn open_sync_device() -> DSSResult<SyncDevice> {
    for i in 0..100 {
      let name = format!("utun{}", i);
      let device = DeviceBuilder::new()
        .name(&name)
        .build_sync();

      match device {
        Ok(device) => return Ok(device),
        Err(e) => crate::debug!("Failed to create TUN device `{}`: {}", name, e),
      }
    }

    Err("Failed to create TUN device after 100 attempts".into())
  }

  pub(crate) fn open() -> Self {
    let device = Self::open_sync_device().expect("Failed to create TUN device");
    let ifname = device.name().expect("Failed to get TUN device name");

    // Get the raw fd from the device
    let raw_fd = device.as_raw_fd();
    // Make the FD non-blocking like the Linux implementation so a read on the
    // utun device cannot stall the single-threaded executor.
    Self::make_nonblocking(raw_fd);
    let fd = ArcFd::from_raw_fd(raw_fd);

    Self {
      _device: device,
      fd,
      name: ifname,
    }
  }

  pub(crate) async fn set_ip6(&self, addr: &crate::ip_addr::IpAddr, prefix_len: u8) {
    let name = self.name.clone();
    let addr_str = format!("{}", addr);

    leo_async::fn_thread_future(move || {
      // On macOS, use ifconfig to add IPv6 address
      let cmd = format!("ifconfig {} inet6 {}/{} alias", name, addr_str, prefix_len);
      let mut proc = std::process::Command::new("sh");
      proc.arg("-c");
      proc.arg(cmd);
      
      if let Err(e) = proc.spawn().and_then(|mut c| c.wait()) {
        crate::warn!("Failed to set IPv6 address: {}", e);
      }
    })
    .await;
  }

  pub(crate) fn run(&self) -> leo_async::mpsc::Sender<Bytes> {
    let read_fd = self.fd.dup().unwrap();
    let write_fd = self.fd.dup().unwrap();

    Self::make_nonblocking(read_fd.as_raw_fd());
    Self::make_nonblocking(write_fd.as_raw_fd());

    // tun -> network
    {
      leo_async::spawn(async move {
        let all_senders = crate::all_senders::get();
        let mut netpacket = BytesMut::new();

        loop {
          let mut buf = [0; 2048];
          let amount = match leo_async::read_fd(&read_fd, &mut buf).await {
            Ok(0) => break,
            Ok(x) => x,
            Err(e) => {
              crate::warn!("TUN read error: {}", e);
              continue;
            }
          };

          // On macOS, utun devices prepend a 4-byte protocol family header
          // We need to skip it to get to the actual IP packet
          if amount < 4 {
            crate::warn!("Packet too short (only {} bytes)", amount);
            continue;
          }

          let packet = &buf[4..amount]; // Skip 4-byte header

          if packet.is_empty() {
            continue;
          }

          let ip_version = (packet[0] & 0b11110000) >> 4;

          let target_addr = match (ip_version, packet.len()) {
            (4, 20..) => crate::ip_addr::IpAddr::ipv4_from_buf(&packet[16..20]),
            (6, 40..) => crate::ip_addr::IpAddr::ipv6_from_buf(&packet[24..40]),
            _ => {
              crate::warn!("Invalid IP version and packet length: {} {}", ip_version, packet.len());
              continue;
            }
          };

          netpacket.reserve(8 + 1 + packet.len());

          netpacket.extend_from_slice(&crate::millis().to_le_bytes());
          netpacket.put_u8(3);
          netpacket.extend_from_slice(packet);
          let to_send = netpacket.split_to(netpacket.len()).freeze();
          all_senders.send_to_fastest(target_addr, to_send);
        }
      });
    }

    // network -> tun
    let (sender, receiver) = leo_async::mpsc::channel::<Bytes>();

    {
      leo_async::spawn(async move {
        let mut write_buf = BytesMut::with_capacity(2048);
        
        loop {
          if let Some(packet) = receiver.recv().await {
            // On macOS, we need to prepend the 4-byte protocol family header
            // Determine protocol from IP version
            let ip_version = (packet[0] & 0b11110000) >> 4;

            // Protocol family: 2 = AF_INET (IPv4), 30 = AF_INET6 (IPv6) on macOS
            let proto_family: [u8; 4] = match ip_version {
              4 => [0x00, 0x00, 0x00, 0x02], // AF_INET in network byte order
              6 => [0x00, 0x00, 0x00, 0x1e], // AF_INET6 (30) in network byte order
              _ => {
                crate::warn!("Unknown IP version when writing: {}", ip_version);
                continue;
              }
            };

            // Build packet with header
            write_buf.clear();
            write_buf.extend_from_slice(&proto_family);
            write_buf.extend_from_slice(&packet);

            // Write the complete packet with header
            if let Err(e) = leo_async::write_fd(&write_fd, &write_buf).await {
              crate::warn!("TUN write error: {}", e);
            }
          }
        }
      });
    }

    sender
  }

  pub(crate) fn route_creator(&self) -> leo_async::mpsc::Sender<crate::ip_addr::IpAddr> {
    let (sender, receiver) = leo_async::mpsc::channel::<crate::ip_addr::IpAddr>();
    let name = self.name.clone();

    let mut already_added = std::collections::HashSet::new();

    leo_async::spawn(async move {
      while let Some(addr) = receiver.recv().await {
        if already_added.contains(&addr) {
          continue;
        }
        already_added.insert(addr);

        let inet_flag = match addr {
          crate::ip_addr::IpAddr::V4(..) => "inet",
          crate::ip_addr::IpAddr::V6(..) => "inet6",
        };

        let name = name.clone();
        let addr_str = format!("{}", addr);

        leo_async::fn_thread_future(move || {
          // On macOS, use route command with -interface flag
          let cmd = format!("route add -{} {} -interface {}", inet_flag, addr_str, name);
          let mut proc = std::process::Command::new("sh");
          proc.arg("-c");
          proc.arg(cmd);
          proc.stdout(std::process::Stdio::null());
          proc.stderr(std::process::Stdio::null());
          
          if let Err(e) = proc.spawn().and_then(|mut c| c.wait()) {
            crate::trace!("Failed to add route (may already exist): {}", e);
          }
        })
        .await;
      }
    });

    sender
  }
}
