// This module uses the wintun.dll from the Wireguard project to create a
// virtual network adapter on Windows.

pub fn wintun_start(
  node_name: &str,
) -> std::result::Result<crossbeam::channel::Sender<Vec<u8>>, Box<dyn std::error::Error>> {
  let dll = crate::wintun::WintunDLL::new("./wintun-amd64.dll")?;

  let adapter_name = format!("smolmesh-{}", node_name);
  let adapter = dll.wintun_create_adapter(&adapter_name)?;

  let ipv6 = crate::ipv6_addr::Addr::from_node_name(node_name);

  unsafe {
    let mut row =
      std::mem::MaybeUninit::<windows_sys::Win32::NetworkManagement::IpHelper::MIB_UNICASTIPADDRESS_ROW>::zeroed();
    windows_sys::Win32::NetworkManagement::IpHelper::InitializeUnicastIpAddressEntry(row.assume_init_mut());
    row.assume_init_mut().InterfaceLuid.Value = dll.wintun_get_adapter_luid(&adapter);

    row.assume_init_mut().Address.si_family = windows_sys::Win32::Networking::WinSock::AF_INET6 as u16;
    row.assume_init_mut().OnLinkPrefixLength = 8;

    // Copy the IPv6 address into the row
    let addr = ipv6.to_buf();

    for i in 0..16 {
      row.assume_init_mut().Address.Ipv6.sin6_addr.u.Byte[i] = addr[i];
    }

    row.assume_init_mut().DadState = windows_sys::Win32::Networking::WinSock::IpDadStatePreferred;
    let row = row.assume_init();

    let res = windows_sys::Win32::NetworkManagement::IpHelper::CreateUnicastIpAddressEntry(&row);

    if res != 0 {
      let err = format!("CreateUnicastIpAddressEntry failed with error code {}", res);
      return Err(err.into());
    }
  }

  let session = dll.wintun_start_session(&adapter, 0x4000000)?;
  let dll = std::sync::Arc::new(dll);

  let (sender, receiver) = crossbeam::channel::unbounded::<Vec<u8>>();

  // Thread for network -> tun
  {
    let session = session.clone();
    let dll = dll.clone();

    std::thread::spawn(move || {
      for packet in receiver {
        if let Err(x) = dll.wintun_send_packet(&session, &packet) {
          println!("wintun_send_packet failed: {}", x);
        }
      }
    });
  }

  // Thread for tun -> network
  {
    let session = session.clone();
    let dll = dll.clone();

    std::thread::spawn(move || {
      let wait_event = dll.wintun_get_read_wait_event(&session);

      loop {
        let packet = dll.wintun_receive_packet(&session);

        if packet.is_err() {
          let res = unsafe { windows_sys::Win32::System::Threading::WaitForSingleObject(wait_event, u32::MAX) };

          if res != windows_sys::Win32::Foundation::WAIT_OBJECT_0 {
            println!("WaitForSingleObject failed with error code {}", res);
          }

          continue;
        }

        let packet = packet.unwrap();

        if packet[0] != 0x60 {
          // Only IPv6 packets
          continue;
        }

        let target = crate::ipv6_addr::Addr::from_buf(&packet[24..40]);

        let mut packet_builder = Vec::new();
        packet_builder.extend_from_slice(&crate::millis().to_le_bytes());
        packet_builder.push(3);
        packet_builder.extend_from_slice(&packet);

        crate::all_senders::get().send_to_fastest(target, packet_builder);
      }
    });
  }

  Ok(sender)
}
