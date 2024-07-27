#[macro_use]
extern crate lazy_static;

use std::{
  collections::{HashSet, VecDeque},
  io::Write,
};

use leo_async::DSSResult;

mod all_senders;
mod ip_addr;
mod legacy_tcp;
mod leo_async;
#[cfg(unix)]
mod linux_tuntap;
mod log;
mod raw_speck;
mod rng;
mod seen_packets;
mod speck;
#[cfg(windows)]
mod windows_tuntap;
#[cfg(windows)]
mod wintun;

fn millis() -> u64 {
  let now = std::time::SystemTime::now();
  let since_epoch = now.duration_since(std::time::UNIX_EPOCH).unwrap();
  since_epoch.as_millis() as u64
}

async fn run_meshnode(args: &mut VecDeque<String>) {
  let config_path = args.pop_front().unwrap();
  let config_file = std::fs::read_to_string(config_path).unwrap();
  let config = json::parse(&config_file).unwrap();
  let all_senders = all_senders::get();

  let mut our_ips = HashSet::new();

  leo_async::spawn(async {
    loop {
      all_senders.clean_broken_senders();

      let delay = 5.0 + rng::uniform() * 5.0;

      leo_async::sleep_seconds(delay).await;
    }
  });

  let node_name = config["node_name"].as_str().unwrap().to_owned();
  let node_ip = crate::ip_addr::IpAddr::from_node_name(&node_name);
  our_ips.insert(node_ip);

  let mut seen_packets = seen_packets::SeenPackets::new(8128);

  let (sender, receiver) = crossbeam::channel::unbounded();

  // Thread to broadcast our node. This allows all nodes in the network to
  // learn the path to us.
  {
    let node_name = node_name.clone();
    leo_async::spawn(async move {
      loop {
        let mut packet = Vec::new();
        packet.write_all(&millis().to_le_bytes()).unwrap();
        packet.push(0);
        packet.write_all(node_name.as_bytes()).unwrap();

        all_senders.send_all(packet);

        let delay = 5.0 + rng::uniform() * 5.0;
        leo_async::sleep_seconds(delay).await;
      }
    });
  }

  for addr in config["ipv4_addresses"].members() {
    let addr = addr.as_str().unwrap();
    let addr = addr.split(".").map(|x| x.parse::<u8>().unwrap()).collect::<Vec<u8>>();

    our_ips.insert(crate::ip_addr::IpAddr::V4(addr[0], addr[1], addr[2], addr[3]));

    leo_async::spawn(async move {
      loop {
        let mut packet = Vec::new();
        packet.write_all(&millis().to_le_bytes()).unwrap();
        packet.push(2);
        packet.extend_from_slice(&addr);

        all_senders.send_all(packet);

        let delay = 5.0 + rng::uniform() * 5.0;
        leo_async::sleep_seconds(delay).await;
      }
    });
  }

  for transport in config["transports"].members() {
    if transport["type"].as_str().unwrap() != "legacy_tcp_connect" {
      continue;
    }

    let host = transport["host"].as_str().unwrap();
    let port = transport["port"].as_u64().unwrap() as u16;
    let key = transport["key"].as_str().unwrap().as_bytes();
    legacy_tcp::create_connection(host, port, key, sender.clone());
  }

  for transport in config["transports"].members() {
    if transport["type"].as_str().unwrap() != "legacy_tcp_listen" {
      continue;
    }

    let port = transport["port"].as_u64().unwrap() as u16;
    let keys = {
      let mut res = Vec::new();

      for key in transport["keys"].members() {
        res.push(key.as_str().unwrap().as_bytes().to_vec());
      }

      res
    };
    legacy_tcp::listener(port, keys, sender.clone());
  }

  let mut tun_sender: Option<leo_async::mpsc::Sender<Vec<u8>>> = None;
  let mut route_adder: Option<leo_async::mpsc::Sender<ip_addr::IpAddr>> = None;

  #[cfg(unix)]
  if let Some(true) = config["linux_tuntap"].as_bool() {
    let interface_name = "smolmesh1";
    let tun = linux_tuntap::TunInterface::open(interface_name);
    tun.bring_interface_up().await;
    tun.set_ip6(&node_ip, 128).await;

    // Handle IPv4 addresses
    for addr in config["ipv4_addresses"].members() {
      let addr = addr.as_str().unwrap();
      let addr = addr.split(".").map(|x| x.parse::<u8>().unwrap()).collect::<Vec<u8>>();
      let addr = crate::ip_addr::IpAddr::V4(addr[0], addr[1], addr[2], addr[3]);
      tun.set_ipv4(&addr)
    }

    let _tun_sender = tun.run();
    tun_sender.replace(_tun_sender);

    let _route_adder = tun.route_creator();
    route_adder.replace(_route_adder);
  }

  #[cfg(windows)]
  if let Some(true) = config["windows_tuntap"].as_bool() {
    if let Ok(_s) = windows_tuntap::wintun_start(&node_name) {
      println!("Started Wintun adapter");
      tun_senders.push(_s);
    }
  }

  loop {
    leo_async::yield_now().await;
    let (data, _sender) = receiver.recv().unwrap();

    // First 8 bytes are the milliseconds since epoch
    let ms = u64::from_le_bytes([data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7]]);

    // Ignore packets older than 3 minutes
    {
      let ignore_after = 1000 * 60 * 3;
      let current = millis();
      if current.abs_diff(ms) > ignore_after {
        continue;
      }
    }

    if seen_packets.contains(&data) {
      continue;
    }
    seen_packets.add(&data);
    let orig_data = data.clone();

    let cmd = &data[8];
    let data = &data[9..];

    match cmd {
      0 => {
        if let Ok(packet_node_name) = std::str::from_utf8(data) {
          let addr = crate::ip_addr::IpAddr::from_node_name(packet_node_name);
          all_senders.add_fastest_to(ms, addr, _sender);

          // Add route to the node
          match &route_adder {
            Some(route_adder) => route_adder.send(addr).unwrap(),
            None => {}
          }
        }

        all_senders.send_all(orig_data.clone());
      }
      1 => {
        // Tracer packet that means "I handle this IPv6 address"
        if data.len() != 16 {
          println!("Invalid IPv6 address length");
          continue;
        }
        let addr = crate::ip_addr::IpAddr::ipv6_from_buf(data);
        all_senders.add_fastest_to(ms, addr, _sender);
        all_senders.send_all(orig_data.clone());

        // Add route to the node
        match &route_adder {
          Some(route_adder) => route_adder.send(addr).unwrap(),
          None => {}
        }
      }
      2 => {
        // Tracer packet that means "I handle this IPv4 address"
        if data.len() != 4 {
          println!("Invalid IPv4 address length");
          continue;
        }
        let addr = crate::ip_addr::IpAddr::ipv4_from_buf(data);
        all_senders.add_fastest_to(ms, addr, _sender);
        all_senders.send_all(orig_data.clone());

        // Add route to the node
        match &route_adder {
          Some(route_adder) => route_adder.send(addr).unwrap(),
          None => {}
        }
      }
      3 => {
        let ip_version = (data[0] & 0b11110000) >> 4;

        let target_addr = match ip_version {
          4 => crate::ip_addr::IpAddr::ipv4_from_buf(&data[16..20]),
          6 => crate::ip_addr::IpAddr::ipv6_from_buf(&data[24..40]),
          _ => {
            println!("Invalid IP version: {}", ip_version);
            continue;
          }
        };

        if our_ips.contains(&target_addr) {
          match &tun_sender {
            Some(sender) => sender.send(data.to_vec()).unwrap(),
            None => {}
          }
        } else {
          all_senders.send_to_fastest(target_addr, orig_data.to_vec());
        }
      }
      _ => {
        println!("Unknown command {}", cmd);
      }
    }
  }
}

fn run_name_to_ipv6(args: &mut VecDeque<String>) {
  let node_name = args.pop_front().unwrap();
  let ipv6 = ip_addr::IpAddr::from_node_name(&node_name);

  println!("{}", ipv6);
}

async fn async_main() -> DSSResult<()> {
  crate::log!("Entered async_main");
  leo_async::fn_thread_future(rng::init_rng).await;

  let mut args: VecDeque<String> = std::env::args().collect();

  // Ignore executable
  let _ = args.pop_front();

  if args.is_empty() {
    args.push_back("meshnode".to_owned());
    args.push_back("config.json".to_owned());
  }

  match args.pop_front().unwrap().as_str() {
    "meshnode" => {
      run_meshnode(&mut args).await;
    }
    "name-to-ipv6" => {
      run_name_to_ipv6(&mut args);
    }
    "make-random-ipv4" => {
      let addr = crate::ip_addr::IpAddr::V4(
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
      );
      println!("{}", addr);
    }
    "make-random-ipv6" => {
      let addr = crate::ip_addr::IpAddr::V6(
        0xfd,
        0x00,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
        (rng::u64() & 0xFF) as u8,
      );

      println!("{}", addr);
    }
    x => {
      println!("Unknown command '{}'", x);
    }
  }

  Ok(())
}

fn main() {
  crate::log!("Entered main");
  let async_main = async_main();
  // let async_main = leo_async::timeout_future(Box::pin(async_main), std::time::Duration::from_secs(15));

  leo_async::run_main(async_main).expect("async_main returned an error");
}
