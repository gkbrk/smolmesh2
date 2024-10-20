use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};

use crate::leo_async::{self, ArcFd};
use crate::{all_senders, log};

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

  #[inline(always)]
  fn incr_ctr(&mut self) {
    self.ctr_low = self.ctr_low.wrapping_add(1);
    if self.ctr_low == 0 {
      self.ctr_high = self.ctr_high.wrapping_add(1);
      assert_ne!(self.ctr_high, 0);
    }
  }

  #[inline(always)]
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

async fn fd_readexact(fd: &ArcFd, buf: &mut [u8]) -> DSSResult<()> {
  let mut read = 0;
  let l = buf.len();

  while read < l {
    let res = leo_async::read_fd(fd, &mut buf[read..]).await?;
    read += res;

    if res == 0 {
      return Err("Read failed".into());
    }
  }

  Ok(())
}

async fn fd_readexact_timeout(fd: &ArcFd, buf: &mut [u8], timeout_ms: usize) -> DSSResult<()> {
  let read = fd_readexact(fd, buf);
  let read = std::pin::pin!(read);
  leo_async::timeout_future(read, std::time::Duration::from_millis(timeout_ms as u64)).await??;
  Ok(())
}

async fn fd_writeall(fd: &ArcFd, buf: &[u8]) -> DSSResult<()> {
  let mut written = 0;
  let l = buf.len();

  while written < l {
    let res = leo_async::write_fd(fd, &buf[written..]).await?;
    written += res;

    if res == 0 {
      return Err("Write failed".into());
    }
  }

  Ok(())
}

async fn fd_writeall_timeout(fd: &ArcFd, buf: &[u8], timeout_ms: usize) -> DSSResult<()> {
  let write = fd_writeall(fd, buf);
  let write = std::pin::pin!(write);
  leo_async::timeout_future(write, std::time::Duration::from_millis(timeout_ms as u64)).await??;
  Ok(())
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

async fn connect_timeout(addr: &std::net::SocketAddr, timeout: std::time::Duration) -> DSSResult<ArcFd> {
  let sock = leo_async::socket::socket()?;
  fd_make_nonblocking(&sock)?;

  let conn_res = {
    let sock = sock.clone();
    async move {
      leo_async::socket::connect(sock.clone(), addr).await?;
      DSSResult::Ok(())
    }
  };

  let conn_res = std::pin::pin!(conn_res);

  leo_async::timeout_future(conn_res, timeout).await??;

  fd_make_nonblocking(&sock)?;

  Ok(sock)
}

async fn connect_impl(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: leo_async::mpsc::Sender<(Vec<u8>, leo_async::mpsc::Sender<Vec<u8>>)>,
) -> DSSResult<()> {
  crate::log!("Connecting to {}:{}", host, port);
  let addr: std::net::SocketAddr = format!("{}:{}", host, port).parse()?;
  let sock = connect_timeout(&addr, std::time::Duration::from_secs(5)).await?;
  crate::log!("Connected to {:?}", addr);

  let read_sock = sock.dup()?;
  let write_sock = sock.dup()?;
  crate::log!("Read sock: {}", read_sock.as_raw_fd());
  crate::log!("Write sock: {}", write_sock.as_raw_fd());

  fd_make_nonblocking(&read_sock)?;
  fd_make_nonblocking(&write_sock)?;

  let all_senders = all_senders::get();

  let sender_iv = {
    let mut iv = Vec::with_capacity(16);
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv
  };

  let keyfinder = crate::speck::multispeck3(key, &sender_iv, b"keyfinder");

  fd_writeall_timeout(&write_sock, &sender_iv, 30_000).await?;
  fd_writeall_timeout(&write_sock, &keyfinder, 30_000).await?;

  let (sender_tx, sender_rx) = leo_async::mpsc::channel();

  all_senders.add(sender_tx.clone());

  let send_enc_key = crate::speck::multispeck3(key, &sender_iv, b"enc_key");
  let send_mac_key = crate::speck::multispeck3(key, &sender_iv, b"mac_key");

  let send_task = {
    async move {
      let mut keystream = KeystreamGen::new(&send_enc_key);

      loop {
        let data = sender_rx.recv().await.unwrap();
        leo_async::yield_now().await;

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

        match fd_writeall_timeout(&write_sock, &ciphertext_data, 30_000).await {
          Ok(_) => {}
          Err(e) => {
            crate::log!("connect_impl send_task write error: {}", e);
            return;
          }
        }
      }
    }
  };

  let recv_task = {
    let key = key.to_owned();

    async move {
      let key = &key;

      let recv_iv = {
        let mut buf = [0u8; 16];
        fd_readexact_timeout(&read_sock, &mut buf, 30_000).await?;
        buf
      };

      let expected_keyfinder = crate::speck::multispeck3(key, &recv_iv, b"keyfinder");
      let recv_keyfinder = {
        let mut buf = [0u8; 16];
        fd_readexact_timeout(&read_sock, &mut buf, 30000).await?;
        buf
      };

      assert_eq!(expected_keyfinder, Vec::from(recv_keyfinder));

      log!("Got correct keyfinder");

      let recv_enc_key = crate::speck::multispeck3(key, &recv_iv, b"enc_key");
      let recv_mac_key = crate::speck::multispeck3(key, &recv_iv, b"mac_key");

      let mut keystream = KeystreamGen::new(&recv_enc_key);

      loop {
        leo_async::yield_now().await;
        // Read and decrypt length
        let mut len_buf = [0u8; 2];
        fd_readexact_timeout(&read_sock, &mut len_buf, 30_000).await?;
        len_buf[0] ^= keystream.next();
        len_buf[1] ^= keystream.next();
        let len = u16::from_le_bytes(len_buf);

        // Read and decrypt data
        let mut data = vec![0u8; len as usize];
        fd_readexact_timeout(&read_sock, &mut data, 30_000).await?;
        for byte in &mut data {
          *byte ^= keystream.next();
        }

        // Read and decrypt MAC
        let mut mac = [0u8; 16];
        fd_readexact_timeout(&read_sock, &mut mac, 30_000).await?;
        for byte in &mut mac {
          *byte ^= keystream.next();
        }

        // Verify MAC
        let expected_mac = crate::speck::multispeck2(&recv_mac_key, &data);
        assert_eq!(expected_mac, Vec::from(mac));

        if let Err(err) = incoming.send((data, sender_tx.clone())) {
          log!("Error sending data: {:?}", err);
          break;
        }
      }

      DSSResult::Ok(())
    }
  };

  leo_async::select2_noresult(send_task, recv_task).await;

  crate::info!("Connection finished");

  Ok(())
}

pub fn create_connection(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: leo_async::mpsc::Sender<(Vec<u8>, leo_async::mpsc::Sender<Vec<u8>>)>,
) {
  let host = host.to_owned();
  let key = key.to_owned();

  leo_async::spawn(async move {
    loop {
      let host = host.clone();
      let key = key.clone();
      let incoming = incoming.clone();

      let res = connect_impl(&host, port, &key, incoming.clone()).await;

      println!("{:?}", res);

      let delay = 5.0 + crate::rng::uniform() * 5.0;
      leo_async::sleep_seconds(delay).await;
    }
  });
}

pub async fn handle_connection(
  keys: Vec<Vec<u8>>,
  conn: std::net::TcpStream,
  incoming: leo_async::mpsc::Sender<(Vec<u8>, leo_async::mpsc::Sender<Vec<u8>>)>,
) -> DSSResult<()> {
  let all_senders = all_senders::get();

  let conn = {
    let owned_fd = unsafe { OwnedFd::from_raw_fd(conn.as_raw_fd()) };
    std::mem::forget(conn);
    ArcFd::from_owned_fd(owned_fd)
  };

  fd_make_nonblocking(&conn)?;
  leo_async::socket::set_nodelay(&conn)?;

  let read_sock = conn.dup()?;
  let write_sock = conn.dup()?;

  fd_make_nonblocking(&read_sock)?;
  fd_make_nonblocking(&write_sock)?;

  let incoming_iv = {
    let mut buf = [0u8; 16];
    fd_readexact_timeout(&read_sock, &mut buf, 30_000).await?;
    buf
  };

  let incoming_keyfinder = {
    let mut buf = [0u8; 16];
    fd_readexact_timeout(&read_sock, &mut buf, 30_000).await?;
    buf
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
    return Err("Got incorrect keyfinder".into());
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

  fd_writeall_timeout(&write_sock, &send_iv, 30_000).await?;
  fd_writeall_timeout(&write_sock, &send_keyfinder, 30_000).await?;

  let send_enc_key = crate::speck::multispeck3(&found_key, &send_iv, b"enc_key");
  let send_mac_key = crate::speck::multispeck3(&found_key, &send_iv, b"mac_key");

  let (sender_tx, sender_rx) = leo_async::mpsc::channel();
  all_senders.add(sender_tx.clone());

  let sender_task = {
    async move {
      let mut keystream = KeystreamGen::new(&send_enc_key);

      loop {
        leo_async::yield_now().await;
        let packet = sender_rx.recv().await.unwrap();
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

        match fd_writeall_timeout(&write_sock, &ciphertext_data, 30_000).await {
          Ok(_) => {}
          Err(_) => return,
        }
      }
    }
  };

  let recv_task = {
    async move {
      let mut keystream = KeystreamGen::new(&recv_enc_key);

      loop {
        leo_async::yield_now().await;
        // Read and decrypt length
        let mut len_buf = [0u8; 2];
        fd_readexact_timeout(&read_sock, &mut len_buf, 30_000).await?;
        len_buf[0] ^= keystream.next();
        len_buf[1] ^= keystream.next();
        let len = u16::from_le_bytes(len_buf);

        // Read and decrypt data
        let mut data = vec![0u8; len as usize];
        fd_readexact_timeout(&read_sock, &mut data, 30_000).await?;
        for byte in &mut data {
          *byte ^= keystream.next();
        }

        // Read and decrypt MAC
        let mut mac = [0u8; 16];
        fd_readexact_timeout(&read_sock, &mut mac, 30_000).await?;
        for byte in &mut mac {
          *byte ^= keystream.next();
        }

        // Verify MAC
        let expected_mac = crate::speck::multispeck2(&recv_mac_key, &data);
        assert_eq!(expected_mac, Vec::from(mac));

        incoming.send((data, sender_tx.clone())).unwrap();
      }

      DSSResult::Ok(())
    }
  };

  let res = leo_async::select2_noresult(sender_task, recv_task).await;

  crate::log!("Connection tasks completed: {:?}", res);

  Ok(())
}

pub fn listener_impl(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: leo_async::mpsc::Sender<(Vec<u8>, leo_async::mpsc::Sender<Vec<u8>>)>,
) -> DResult<()> {
  let listener = std::net::TcpListener::bind(("0.0.0.0", port))?;

  for x in listener.incoming() {
    let keys = keys.clone();
    let incoming = incoming.clone();
    let x = x?;

    crate::log!("Incoming connection: {:?}", x);

    leo_async::spawn(handle_connection(keys, x, incoming));
  }

  Ok(())
}

pub fn listener(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: leo_async::mpsc::Sender<(Vec<u8>, leo_async::mpsc::Sender<Vec<u8>>)>,
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
