use std::io::{Read, Write};
use std::os::fd::AsRawFd;

use crate::all_senders;

type DResult<T> = std::result::Result<T, Box<dyn std::error::Error>>;
type DSSResult<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

struct KeystreamGen {
  round_keys: [u64; 32],
  ctr_high: u64,
  ctr_low: u64,
  n: u64,
  a: u64,
  b: u64,
}

impl KeystreamGen {
  fn new(key: &[u8]) -> Self {
    let rk = {
      let mut rk = [0u64; 32];
      let round_keys = crate::raw_speck::key_schedule(key, 32);

      rk[..32].copy_from_slice(&round_keys[..32]);

      rk
    };

    KeystreamGen {
      round_keys: rk,
      ctr_high: 0,
      ctr_low: 0,
      n: 0,
      a: 0,
      b: 0,
    }
  }

  fn incr_ctr(&mut self) {
    self.ctr_low = self.ctr_low.wrapping_add(1);
    if self.ctr_low == 0 {
      self.ctr_high = self.ctr_high.wrapping_add(1);
      assert_ne!(self.ctr_high, 0);
    }
  }

  fn iter_block(&mut self) {
    self.incr_ctr();

    let mut a = self.ctr_high;
    let mut b = self.ctr_low;

    for i in 0..32 {
      (a, b) = crate::raw_speck::ernd(a, b, self.round_keys[i]);
    }

    self.a = a;
    self.b = b;
  }

  #[inline(always)]
  fn next(&mut self) -> u8 {
    let nb = self.n % 16;

    if nb == 0 {
      self.iter_block();
    }

    if nb < 8 {
      let res = self.a & 0xff;
      self.a >>= 8;
      self.n += 1;
      res as u8
    } else {
      let res = self.b & 0xff;
      self.b >>= 8;
      self.n += 1;
      res as u8
    }
  }
}

fn poll_readable(fd: libc::c_int, timeout_ms: usize) -> DSSResult<()> {
  let read_or_error = nix::poll::PollFlags::POLLIN
    | nix::poll::PollFlags::POLLERR
    | nix::poll::PollFlags::POLLHUP
    | nix::poll::PollFlags::POLLNVAL;

  let fd = unsafe { std::os::fd::BorrowedFd::borrow_raw(fd) };
  let pollfd = nix::poll::PollFd::new(&fd, read_or_error);
  let res = nix::poll::poll(&mut [pollfd], timeout_ms as i32)?;

  if res <= 0 {
    return Err("poll failed".into());
  }

  Ok(())
}

fn readexact_timeout(sock: &mut socket2::Socket, buf: &mut [u8], timeout_ms: usize) -> DSSResult<()> {
  let mut read = 0;

  let fd = sock.as_raw_fd();

  while read < buf.len() {
    poll_readable(fd, timeout_ms)?;
    let res = sock.read(&mut buf[read..])?;
    read += res;

    if res == 0 {
      return Err("read failed".into());
    }
  }

  Ok(())
}

fn connect_impl(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: crossbeam::channel::Sender<(Vec<u8>, crossbeam::channel::Sender<Vec<u8>>)>,
) -> DResult<()> {
  let mut sock = socket2::Socket::new(
    socket2::Domain::IPV4,
    socket2::Type::STREAM,
    Some(socket2::Protocol::TCP),
  )?;

  let addr: std::net::SocketAddr = format!("{}:{}", host, port).parse()?;

  sock.connect_timeout(&addr.into(), std::time::Duration::from_secs(5))?;

  sock.set_write_timeout(Some(std::time::Duration::from_secs(5)))?;
  sock.set_read_timeout(Some(std::time::Duration::from_secs(30)))?;

  sock.set_nodelay(true)?;

  let all_senders = all_senders::get();

  let sender_iv = {
    let mut iv = Vec::with_capacity(16);
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv
  };

  let keyfinder = crate::speck::multispeck3(key, &sender_iv, b"keyfinder");

  sock.write_all(&sender_iv)?;
  sock.write_all(&keyfinder)?;

  let (sender_tx, sender_rx) = crossbeam::channel::unbounded();

  all_senders.add(sender_tx.clone());

  let send_enc_key = crate::speck::multispeck3(key, &sender_iv, b"enc_key");
  let send_mac_key = crate::speck::multispeck3(key, &sender_iv, b"mac_key");

  crossbeam::thread::scope(|s| {
    // Sender
    let _ = s.spawn(|_| -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
      let sender_rx = sender_rx;
      let mut keystream = KeystreamGen::new(&send_enc_key);

      loop {
        let data = sender_rx.recv().unwrap();

        // Empty data means there is a cleaner checking the channel
        if data.is_empty() {
          continue;
        }

        let mac = crate::speck::multispeck2(&send_mac_key, &data);

        let mut plaintext_data = Vec::with_capacity(data.len() + 2 + 16);
        plaintext_data.extend_from_slice(&(data.len() as u16).to_le_bytes());
        plaintext_data.extend_from_slice(&data);
        plaintext_data.extend_from_slice(&mac);

        let mut ciphertext_data = Vec::with_capacity(plaintext_data.len());

        for byte in plaintext_data {
          let keystream_byte = keystream.next();
          ciphertext_data.push(byte ^ keystream_byte);
        }

        {
          let mut pos = 0;

          while pos < ciphertext_data.len() {
            let amt = sock.send(&ciphertext_data[pos..]);

            if let Ok(0) = amt {
              sock.shutdown(std::net::Shutdown::Both)?;
              return Err("Could not write".into());
            } else if let Ok(amt) = amt {
              pos += amt;
            } else {
              sock.shutdown(std::net::Shutdown::Both)?;
              return Err("Could not write".into());
            }
          }
        }
      }
    });

    // Receiver
    let _ = s.spawn(|_| -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
      let mut sock = sock.try_clone()?;

      let recv_iv = {
        let mut iv = [0u8; 16];
        readexact_timeout(&mut sock, &mut iv, 30000)?;
        iv
      };

      let expected_keyfinder = crate::speck::multispeck3(key, &recv_iv, b"keyfinder");
      let recv_keyfinder = {
        let mut keyfinder = [0u8; 16];
        readexact_timeout(&mut sock, &mut keyfinder, 30000)?;
        keyfinder
      };

      assert_eq!(expected_keyfinder, Vec::from(recv_keyfinder));

      println!("Got correct keyfinder");

      let recv_enc_key = crate::speck::multispeck3(key, &recv_iv, b"enc_key");
      let recv_mac_key = crate::speck::multispeck3(key, &recv_iv, b"mac_key");

      let mut keystream = KeystreamGen::new(&recv_enc_key);

      let mut read_exact = |buf: &mut [u8]| -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        if let Err(_) = readexact_timeout(&mut sock, buf, 30000) {
          sock.shutdown(std::net::Shutdown::Both)?;
          return Err("read_exact failed".into());
        }

        Ok(())
      };

      loop {
        // Read and decrypt length
        let mut len_buf = [0u8; 2];
        read_exact(&mut len_buf)?;
        len_buf[0] ^= keystream.next();
        len_buf[1] ^= keystream.next();
        let len = u16::from_le_bytes(len_buf);

        // Read and decrypt data
        let mut data = vec![0u8; len as usize];
        read_exact(&mut data)?;
        for byte in &mut data {
          *byte ^= keystream.next();
        }

        // Read and decrypt MAC
        let mut mac = [0u8; 16];
        read_exact(&mut mac)?;
        for byte in &mut mac {
          *byte ^= keystream.next();
        }

        // Verify MAC
        let expected_mac = crate::speck::multispeck2(&recv_mac_key, &data);
        assert_eq!(expected_mac, Vec::from(mac));

        incoming.send((data, sender_tx.clone())).unwrap();
      }
    });
  })
  .map_err(|_| "thread panicked")?;

  Ok(())
}

pub fn create_connection(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: crossbeam::channel::Sender<(Vec<u8>, crossbeam::channel::Sender<Vec<u8>>)>,
) {
  let host = host.to_owned();
  let key = key.to_owned();

  std::thread::spawn(move || loop {
    let host = host.clone();
    let key = key.clone();
    let incoming = incoming.clone();

    let res = connect_impl(&host, port, &key, incoming.clone());

    println!("{:?}", res);

    let delay = 5.0 + crate::rng::uniform() * 5.0;

    std::thread::sleep(std::time::Duration::from_secs_f64(delay));
  });
}

pub fn handle_connection(
  keys: Vec<Vec<u8>>,
  conn: std::net::TcpStream,
  incoming: crossbeam::channel::Sender<(Vec<u8>, crossbeam::channel::Sender<Vec<u8>>)>,
) {
  let all_senders = all_senders::get();
  let mut conn = conn;
  conn.set_write_timeout(Some(std::time::Duration::from_secs(5))).unwrap();
  conn.set_read_timeout(Some(std::time::Duration::from_secs(30))).unwrap();
  conn.set_nodelay(true).unwrap();

  let incoming_iv = {
    let mut iv = [0u8; 16];
    conn.read_exact(&mut iv).unwrap();
    iv
  };

  let incoming_keyfinder = {
    let mut keyfinder = [0u8; 16];
    conn.read_exact(&mut keyfinder).unwrap();
    keyfinder
  };

  let mut found_key = None;

  for key in keys {
    let expected_keyfinder = crate::speck::multispeck3(&key, &incoming_iv, b"keyfinder");

    if expected_keyfinder == incoming_keyfinder {
      found_key = Some(key);
      break;
    }
  }

  if found_key.is_none() {
    println!("Got incorrect keyfinder");
    return;
  }

  let found_key = found_key.unwrap();

  let recv_enc_key = crate::speck::multispeck3(&found_key, &incoming_iv, b"enc_key");
  let recv_mac_key = crate::speck::multispeck3(&found_key, &incoming_iv, b"mac_key");

  // Send our keyfinder
  let send_iv = {
    let mut iv = Vec::with_capacity(16);
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv
  };

  let send_keyfinder = crate::speck::multispeck3(&found_key, &send_iv, b"keyfinder");

  conn.write_all(&send_iv).unwrap();
  conn.write_all(&send_keyfinder).unwrap();

  let send_enc_key = crate::speck::multispeck3(&found_key, &send_iv, b"enc_key");
  let send_mac_key = crate::speck::multispeck3(&found_key, &send_iv, b"mac_key");

  let (sender_tx, sender_rx) = crossbeam::channel::unbounded();
  all_senders.add(sender_tx.clone());

  crossbeam::thread::scope(move |s| {
    let mut sender_sock = conn.try_clone().unwrap();

    // Sender
    s.spawn(move |_| {
      let mut keystream = KeystreamGen::new(&send_enc_key);

      for packet in sender_rx {
        // Empty data means there is a cleaner checking the channel
        if packet.is_empty() {
          continue;
        }

        let mac = crate::speck::multispeck2(&send_mac_key, &packet);

        let mut plaintext_data = Vec::with_capacity(packet.len() + 2 + 16);
        plaintext_data.extend_from_slice(&(packet.len() as u16).to_le_bytes());
        plaintext_data.extend_from_slice(&packet);
        plaintext_data.extend_from_slice(&mac);

        let mut ciphertext_data = Vec::with_capacity(plaintext_data.len());

        for byte in plaintext_data {
          let keystream_byte = keystream.next();
          ciphertext_data.push(byte ^ keystream_byte);
        }

        sender_sock.write_all(&ciphertext_data).unwrap();
        sender_sock.flush().unwrap();
      }
    });

    let mut receiver_sock = conn.try_clone().unwrap();

    // Receiver
    s.spawn(move |_| {
      let mut keystream = KeystreamGen::new(&recv_enc_key);

      loop {
        // Read and decrypt length
        let mut len_buf = [0u8; 2];
        receiver_sock.read_exact(&mut len_buf).unwrap();
        len_buf[0] ^= keystream.next();
        len_buf[1] ^= keystream.next();
        let len = u16::from_le_bytes(len_buf);

        // Read and decrypt data
        let mut data = vec![0u8; len as usize];
        receiver_sock.read_exact(&mut data).unwrap();
        for byte in &mut data {
          *byte ^= keystream.next();
        }

        // Read and decrypt MAC
        let mut mac = [0u8; 16];
        receiver_sock.read_exact(&mut mac).unwrap();
        for byte in &mut mac {
          *byte ^= keystream.next();
        }

        // Verify MAC
        let expected_mac = crate::speck::multispeck2(&recv_mac_key, &data);
        assert_eq!(expected_mac, Vec::from(mac));

        incoming.send((data, sender_tx.clone())).unwrap();
      }
    });
  })
  .unwrap();
}

pub fn listener_impl(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: crossbeam::channel::Sender<(Vec<u8>, crossbeam::channel::Sender<Vec<u8>>)>,
) -> DResult<()> {
  let listener = std::net::TcpListener::bind(("0.0.0.0", port))?;

  for x in listener.incoming() {
    let keys = keys.clone();
    let incoming = incoming.clone();
    let x = x?;

    std::thread::spawn(move || {
      handle_connection(keys, x, incoming);
    });
  }

  Ok(())
}

pub fn listener(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: crossbeam::channel::Sender<(Vec<u8>, crossbeam::channel::Sender<Vec<u8>>)>,
) {
  std::thread::spawn(move || loop {
    let incoming = incoming.clone();
    let keys = keys.clone();

    let res = listener_impl(port, keys, incoming.clone());

    println!("{:?}", res);

    let delay = 5.0 + crate::rng::uniform() * 5.0;

    std::thread::sleep(std::time::Duration::from_secs_f64(delay));
  });
}
