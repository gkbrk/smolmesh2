#[macro_use]
extern crate lazy_static;

use std::{collections::VecDeque, io::Write};

mod admin_server;
mod all_senders;
mod ipv6_addr;
mod legacy_tcp;
#[cfg(unix)]
mod linux_tuntap;
mod raw_speck;
mod recently_seen_nodes;
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

fn run_meshnode(args: &mut VecDeque<String>) {
  let config_path = args.pop_front().unwrap();
  let config_file = std::fs::read_to_string(config_path).unwrap();
  let config = json::parse(&config_file).unwrap();
  let all_senders = all_senders::get();

  // Admin server

  std::thread::spawn(|| loop {
    all_senders.clean_broken_senders();

    let delay = 5.0 + rng::uniform() * 5.0;
    std::thread::sleep(std::time::Duration::from_secs_f64(delay));
  });

  let node_name = config["node_name"].as_str().unwrap().to_owned();
  let node_ip = ipv6_addr::Addr::from_node_name(&node_name);

  admin_server::start_admin_server(node_name.clone(), 8765);

  let mut seen_packets = seen_packets::SeenPackets::new(8128);

  let (sender, receiver) = crossbeam::channel::unbounded();

  // Thread to broadcast our node. This allows all nodes in the network to
  // learn the path to us.
  {
    let node_name = node_name.clone();
    std::thread::spawn(move || loop {
      let mut packet = Vec::new();
      packet.write_all(&millis().to_le_bytes()).unwrap();
      packet.push(0);
      packet.write_all(node_name.as_bytes()).unwrap();

      all_senders.send_all(packet);

      let delay = 5.0 + rng::uniform() * 5.0;
      std::thread::sleep(std::time::Duration::from_secs_f64(delay));
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

  let mut tun_senders: Vec<crossbeam::channel::Sender<Vec<u8>>> = Vec::new();

  #[cfg(unix)]
  if let Some(true) = config["linux_tuntap"].as_bool() {
    let interface_name = "smolmesh1";
    let tun = linux_tuntap::TunInterface::open(interface_name);
    tun.set_ip6(&node_ip, 8);
    let tun_sender = tun.run();
    tun_senders.push(tun_sender);
  }

  #[cfg(windows)]
  if let Some(true) = config["windows_tuntap"].as_bool() {
    if let Ok(_s) = windows_tuntap::wintun_start(&node_name) {
      println!("Started Wintun adapter");
      tun_senders.push(_s);
    }
  }

  loop {
    let (data, _sender) = receiver.recv().unwrap();
    if seen_packets.contains(&data) {
      continue;
    }
    seen_packets.add(&data);
    let orig_data = data.clone();

    // First 8 bytes are the milliseconds since epoch
    let ms = u64::from_le_bytes([data[0], data[1], data[2], data[3], data[4], data[5], data[6], data[7]]);

    // TODO: Reject packets that are older than 3 minutes

    let cmd = &data[8];
    let data = &data[9..];

    let recent = recently_seen_nodes::get();

    match cmd {
      0 => {
        if let Ok(packet_node_name) = std::str::from_utf8(data) {
          let addr = ipv6_addr::Addr::from_node_name(packet_node_name);
          all_senders.add_fastest_to(ms, addr, _sender);
          recent.set_alive(packet_node_name);
        }

        all_senders.send_all(orig_data.clone());
      }
      3 => {
        // IPv6 packet
        let target_addr = ipv6_addr::Addr::from_buf(&data[24..40]);

        for tun_sender in &tun_senders {
          tun_sender.send(data.to_vec()).unwrap();
        }

        if target_addr != node_ip {
          all_senders.send_to_fastest(target_addr, orig_data.to_vec());
        }
      }
      5 => {
        // Traceroute packet
        let target_len = data[0] as usize;
        let target = &data[1..1 + target_len];
        let target = std::str::from_utf8(target).unwrap_or("tgt");
        let target_addr = ipv6_addr::Addr::from_node_name(target);

        let src_len = data[1 + target_len] as usize;
        let src = &data[1 + target_len + 1..1 + target_len + 1 + src_len];
        let src = std::str::from_utf8(src).unwrap_or("src");
        let src_addr = ipv6_addr::Addr::from_node_name(src);

        let mut path = Vec::new();
        path.extend_from_slice(&data[1 + target_len + 1 + src_len..]);
        path.extend_from_slice(format!("{} ", node_name).as_bytes());

        if target_addr == node_ip {
          // Send back traceroute response
          let mut resp = Vec::new();
          resp.write_all(&millis().to_le_bytes()).unwrap();
          resp.push(6);
          resp.push(src_len as u8);
          resp.write_all(src.as_bytes()).unwrap();
          resp.push(target_len as u8);
          resp.write_all(target.as_bytes()).unwrap();
          resp.extend_from_slice(&path);

          all_senders.send_to_fastest(src_addr, resp);
        } else {
          let mut resp = Vec::new();
          resp.extend_from_slice(&orig_data);
          resp.extend_from_slice(format!("{} ", node_name).as_bytes());

          all_senders.send_to_fastest(target_addr, resp);
        }
      }
      6 => {
        // Traceroute response
        let src_len = data[0] as usize;
        let src = &data[1..1 + src_len];
        let src = std::str::from_utf8(src).unwrap_or("src");
        let src_addr = ipv6_addr::Addr::from_node_name(src);

        let target_len = data[1 + src_len] as usize;
        let target = &data[1 + src_len + 1..1 + src_len + 1 + target_len];
        let target = std::str::from_utf8(target).unwrap_or("tgt");

        let path = &data[1 + src_len + 1 + target_len..];

        if src_addr == node_ip {
          recently_seen_nodes::get().set_traceroute(target, std::str::from_utf8(path).unwrap_or(""));
        } else {
          all_senders.send_to_fastest(src_addr, orig_data.to_vec());
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
  let ipv6 = ipv6_addr::Addr::from_node_name(&node_name);

  println!("{}", ipv6.to_ipv6());
}

fn main() {
  rng::init_rng();

  let mut args: VecDeque<String> = std::env::args().collect();

  // Ignore executable
  let _ = args.pop_front();

  if args.is_empty() {
    args.push_back("meshnode".to_owned());
    args.push_back("config.json".to_owned());
  }

  match args.pop_front().unwrap().as_str() {
    "meshnode" => {
      run_meshnode(&mut args);
    }
    "name-to-ipv6" => {
      run_name_to_ipv6(&mut args);
    }
    x => {
      println!("Unknown command '{}'", x);
    }
  }
}
