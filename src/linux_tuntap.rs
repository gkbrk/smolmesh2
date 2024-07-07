pub(crate) struct TunInterface {
  fd: i32,
}

unsafe fn nix_libc_open(path: &str) -> i32 {
  // Copy to null-terminated string
  let mut path = path.to_owned();
  path.push('\0');

  #[cfg(target_arch = "x86_64")]
  let path = path.as_ptr() as *const i8;

  #[cfg(target_arch = "aarch64")]
  let path = path.as_ptr() as *const u8;

  libc::open(path, libc::O_RDWR)
}

impl TunInterface {
  pub(crate) fn open(ifname: &str) -> Self {
    let fd: i32;

    unsafe {
      fd = nix_libc_open("/dev/net/tun");
    }

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

    Self { fd }
  }

  pub(crate) fn bring_interface_up(&self) {
    let mut cmd = std::process::Command::new("ip");
    cmd.arg("link");
    cmd.arg("set");
    cmd.arg("dev");
    cmd.arg("smolmesh1");
    cmd.arg("up");

    cmd.spawn().unwrap().wait().unwrap();
  }

  pub(crate) fn set_ip6(&self, addr: &crate::ip_addr::IpAddr, prefix_len: u8) {
    // TODO: Get the name smolmesh1 from somewhere
    let cmd = format!("ip -6 addr add {}/{} dev smolmesh1", addr, prefix_len);

    let mut proc = std::process::Command::new("sh");
    proc.arg("-c");
    proc.arg(cmd);

    proc.spawn().unwrap().wait().unwrap();
  }

  pub(crate) fn set_ipv4(&self, addr: &crate::ip_addr::IpAddr) {
    let cmd = format!("ip -4 addr add {}/32 dev smolmesh1", addr);
    let mut proc = std::process::Command::new("sh");
    proc.arg("-c");
    proc.arg(cmd);

    proc.spawn().unwrap().wait().unwrap();
  }

  pub(crate) fn run(&self) -> crossbeam::channel::Sender<Vec<u8>> {
    let fd = self.fd;

    // tun -> network
    std::thread::spawn(move || {
      let all_senders = crate::all_senders::get();
      loop {
        let mut packet = Vec::with_capacity(2048);

        unsafe {
          let amount = libc::read(fd, packet.as_mut_ptr() as *mut libc::c_void, packet.capacity());
          assert!(amount.is_positive());
          packet.set_len(amount as usize);
        }

        let addr = crate::ip_addr::IpAddr::ipv6_from_buf(&packet[24..40]);

        let mut netpacket = Vec::new();
        netpacket.extend_from_slice(&crate::millis().to_le_bytes());
        netpacket.push(3);
        netpacket.extend_from_slice(&packet);
        all_senders.send_to_fastest(addr, netpacket);
      }
    });

    // network -> tun
    let (sender, receiver) = crossbeam::channel::unbounded::<Vec<u8>>();

    std::thread::spawn(move || {
      for packet in receiver {
        unsafe {
          libc::write(fd, packet.as_ptr() as *const libc::c_void, packet.len());
        }
      }
    });

    sender
  }

  pub(crate) fn route_creator(&self) -> crossbeam::channel::Sender<crate::ip_addr::IpAddr> {
    let (sender, receiver) = crossbeam::channel::unbounded::<crate::ip_addr::IpAddr>();

    let mut already_added = std::collections::HashSet::new();

    std::thread::spawn(move || for addr in receiver {
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
      let mut proc = std::process::Command::new("sh");
      proc.arg("-c");
      proc.arg(cmd);
      proc.stdout(std::process::Stdio::null());
      proc.stderr(std::process::Stdio::null());
      proc.spawn().unwrap().wait().unwrap();
    });

    sender
  }
}
