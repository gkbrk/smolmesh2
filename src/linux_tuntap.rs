use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};

use bytes::{BufMut, Bytes, BytesMut};

use crate::leo_async::{self, ArcFd, DSSResult};

pub(crate) struct TunInterface {
  fd: ArcFd,
}

fn fd_make_nonblocking(fd: &ArcFd) -> DSSResult<()> {
  let fd = fd.as_raw_fd();
  let flags = unsafe { libc::fcntl(fd, libc::F_GETFL) };
  if flags == -1 {
    return Err("fcntl failed".into());
  }

  let flags = flags | libc::O_NONBLOCK;
  let res = unsafe { libc::fcntl(fd, libc::F_SETFL, flags) };
  if res == -1 {
    return Err("fcntl failed".into());
  }

  Ok(())
}

fn nix_libc_open(path: &str) -> i32 {
  // Copy to null-terminated string
  let mut path = path.to_owned();
  path.push('\0');

  #[cfg(target_arch = "x86_64")]
  let path = path.as_ptr() as *const i8;

  #[cfg(target_arch = "aarch64")]
  let path = path.as_ptr() as *const u8;

  unsafe { libc::open(path, libc::O_RDWR | libc::O_NONBLOCK) }
}

impl TunInterface {
  pub(crate) fn open(ifname: &str) -> Self {
    let fd = nix_libc_open("/dev/net/tun");

    let flags = nix::net::if_::InterfaceFlags::IFF_TUN | nix::net::if_::InterfaceFlags::IFF_NO_PI;
    let mut ifr = [0u8; 16 + 2];

    let len = ifname.len();
    ifr[..len].copy_from_slice(&ifname.as_bytes()[..len]);

    let flags_encoded = flags.bits().to_le_bytes();

    ifr[16] = flags_encoded[0];
    ifr[17] = flags_encoded[1];

    unsafe {
      let tunsetiff = 0x400454ca;
      libc::ioctl(fd, tunsetiff, ifr.as_mut_ptr());
    }

    Self {
      fd: ArcFd::from_owned_fd(unsafe { OwnedFd::from_raw_fd(fd) }),
    }
  }

  pub(crate) async fn bring_interface_up(&self) {
    leo_async::fn_thread_future(|| {
      let mut cmd = std::process::Command::new("ip");
      cmd.arg("link");
      cmd.arg("set");
      cmd.arg("dev");
      cmd.arg("smolmesh1");
      cmd.arg("up");

      cmd.spawn().unwrap().wait().unwrap();
    })
    .await;
  }

  pub(crate) async fn set_ip6(&self, addr: &crate::ip_addr::IpAddr, prefix_len: u8) {
    // TODO: Get the name smolmesh1 from somewhere
    let cmd = format!("ip -6 addr add {}/{} dev smolmesh1", addr, prefix_len);

    leo_async::fn_thread_future(|| {
      let mut proc = std::process::Command::new("sh");
      proc.arg("-c");
      proc.arg(cmd);

      proc.spawn().unwrap().wait().unwrap();
    })
    .await;
  }

  pub(crate) fn set_ipv4(&self, addr: &crate::ip_addr::IpAddr) {
    let cmd = format!("ip -4 addr add {}/32 dev smolmesh1", addr);
    let mut proc = std::process::Command::new("sh");
    proc.arg("-c");
    proc.arg(cmd);

    proc.spawn().unwrap().wait().unwrap();
  }

  pub(crate) fn run(&self) -> leo_async::mpsc::Sender<Bytes> {
    let read_fd = self.fd.dup().unwrap();
    let write_fd = self.fd.dup().unwrap();

    fd_make_nonblocking(&read_fd).unwrap();
    fd_make_nonblocking(&write_fd).unwrap();

    // tun -> network
    {
      leo_async::spawn(async move {
        let all_senders = crate::all_senders::get();
        let mut packet = vec![0; 2048];
        let mut netpacket = BytesMut::new();

        loop {
          packet.resize(2048, 0);
          let amount = match leo_async::read_fd(&read_fd, &mut packet).await {
            Ok(0) => break,
            Ok(x) => x,
            Err(e) => {
              crate::warn!("TUN read error: {}", e);
              continue;
            }
          };
          leo_async::yield_now().await;
          packet.truncate(amount);

          let ip_version = (packet[0] & 0b11110000) >> 4;

          let target_addr = match (ip_version, packet.len()) {
            (4, 20..) => crate::ip_addr::IpAddr::ipv4_from_buf(&packet[16..20]),
            (6, 40..) => crate::ip_addr::IpAddr::ipv6_from_buf(&packet[24..40]),
            _ => {
              crate::warn!("Invalid IP version and packet length: {} {}", ip_version, packet.len());
              continue;
            }
          };

          netpacket.clear();
          netpacket.extend_from_slice(&crate::millis().to_le_bytes());
          netpacket.put_u8(3);
          netpacket.extend_from_slice(&packet);
          all_senders.send_to_fastest(target_addr, netpacket.clone().freeze());
        }
      });
    }

    // network -> tun
    let (sender, receiver) = leo_async::mpsc::channel::<Bytes>();

    {
      leo_async::spawn(async move {
        loop {
          if let Some(packet) = receiver.recv().await {
            leo_async::yield_now().await;
            leo_async::write_fd(&write_fd, &packet).await.unwrap();
          }
        }
      });
    }

    sender
  }

  pub(crate) fn route_creator(&self) -> leo_async::mpsc::Sender<crate::ip_addr::IpAddr> {
    let (sender, receiver) = leo_async::mpsc::channel::<crate::ip_addr::IpAddr>();

    let mut already_added = std::collections::HashSet::new();

    leo_async::spawn(async move {
      while let Some(addr) = receiver.recv().await {
        if already_added.contains(&addr) {
          continue;
        }
        already_added.insert(addr);

        let version = match addr {
          crate::ip_addr::IpAddr::V4(..) => 4,
          crate::ip_addr::IpAddr::V6(..) => 6,
        };

        let prefix_len = match addr {
          crate::ip_addr::IpAddr::V4(..) => 32,
          crate::ip_addr::IpAddr::V6(..) => 128,
        };

        let cmd = format!("ip -{} route add {}/{} dev smolmesh1", version, addr, prefix_len);
        leo_async::fn_thread_future(move || {
          let mut proc = std::process::Command::new("sh");
          proc.arg("-c");
          proc.arg(cmd);
          proc.stdout(std::process::Stdio::null());
          proc.stderr(std::process::Stdio::null());
          proc.spawn().unwrap().wait().unwrap();
        })
        .await;
      }
    });

    sender
  }
}
