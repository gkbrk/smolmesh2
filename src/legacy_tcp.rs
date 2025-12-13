use std::os::fd::AsRawFd;

use bytes::{Bytes, BytesMut};
use futures::{AsyncReadExt, AsyncWriteExt};

use crate::leo_async::{self, ArcFd};
use crate::{DSSResult, all_senders, gimli, info, log};

struct KeystreamGen {
  gimli_state: gimli::GimliState,
  n: u8,
}

impl KeystreamGen {
  fn new(key: &[u8]) -> Self {
    let mut state = gimli::GimliState::new();
    state.buf_mut()[0..8].copy_from_slice(&key.len().to_le_bytes());
    state.permute();

    for chunk in key.chunks(gimli::RATE_BYTES) {
      for (i, c) in chunk.iter().enumerate() {
        state.buf_mut()[i] ^= *c;
      }
      state.permute();
    }

    Self {
      gimli_state: state,
      n: 0,
    }
  }

  #[inline(always)]
  fn next(&mut self) -> u8 {
    let byte = self.gimli_state.buf()[self.n as usize];
    self.n += 1;
    if self.n == 16 {
      self.gimli_state.permute();
      self.n = 0;
    }
    byte
  }
}

async fn asyncread_readexact_timeout<R: futures::AsyncRead + ?Sized + Unpin>(
  reader: &mut R,
  buf: &mut [u8],
  timeout_ms: usize,
) -> DSSResult<()> {
  let read_task = async { reader.read_exact(buf).await };

  let read_task = std::pin::pin!(read_task);
  leo_async::timeout_future(read_task, std::time::Duration::from_millis(timeout_ms as u64)).await??;

  Ok(())
}

async fn asyncwrite_writeall_timeout<W: futures::AsyncWrite + ?Sized + Unpin>(
  writer: &mut W,
  buf: &[u8],
  timeout_ms: usize,
) -> DSSResult<()> {
  let write_task = async { writer.write_all(buf).await };

  let write_task = std::pin::pin!(write_task);
  leo_async::timeout_future(write_task, std::time::Duration::from_millis(timeout_ms as u64)).await??;
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

  {
    let conn_res = leo_async::socket::connect(&sock, addr);
    let conn_res = std::pin::pin!(conn_res);
    leo_async::timeout_future(conn_res, timeout).await??;
  }

  fd_make_nonblocking(&sock)?;

  Ok(sock)
}

fn multigimli3(x: &[u8], y: &[u8], z: &[u8]) -> [u8; 16] {
  let mut state = gimli::GimliState::new();

  state.buf_mut()[0..8].copy_from_slice(&x.len().to_le_bytes());
  state.permute();
  state.buf_mut()[0..8].copy_from_slice(&y.len().to_le_bytes());
  state.permute();
  state.buf_mut()[0..8].copy_from_slice(&z.len().to_le_bytes());
  state.permute();

  for buf in [x, y, z].iter() {
    for chunk in buf.chunks(gimli::RATE_BYTES) {
      for (i, c) in chunk.iter().enumerate() {
        state.buf_mut()[i] ^= *c;
      }
      state.permute();
    }
  }

  let mut res = [0u8; 16];
  res.copy_from_slice(&state.buf()[0..16]);
  res
}

fn multigimli2(x: &[u8], y: &[u8]) -> [u8; 16] {
  let mut state = gimli::GimliState::new();

  state.buf_mut()[0..8].copy_from_slice(&x.len().to_le_bytes());
  state.permute();
  state.buf_mut()[0..8].copy_from_slice(&y.len().to_le_bytes());
  state.permute();

  for buf in [x, y].iter() {
    for chunk in buf.chunks(gimli::RATE_BYTES) {
      for (i, c) in chunk.iter().enumerate() {
        state.buf_mut()[i] ^= *c;
      }
      state.permute();
    }
  }

  let mut res = [0u8; 16];
  res.copy_from_slice(&state.buf()[0..16]);
  res
}

async fn connect_impl(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: leo_async::mpsc::Sender<(Bytes, leo_async::mpsc::Sender<Bytes>)>,
) -> DSSResult<()> {
  crate::log!("Connecting to {}:{}", host, port);
  let addr: std::net::SocketAddr = format!("{}:{}", host, port).parse()?;
  let sock = connect_timeout(&addr, std::time::Duration::from_secs(5)).await?;
  crate::log!("Connected to {:?}", addr);
  leo_async::socket::set_nodelay(&sock)?;

  let read_sock = sock.dup()?;
  let mut write_sock = sock.dup()?;
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

  let keyfinder = multigimli3(key, &sender_iv, b"keyfinder");

  asyncwrite_writeall_timeout(&mut write_sock, &sender_iv, 30_000).await?;
  asyncwrite_writeall_timeout(&mut write_sock, &keyfinder, 30_000).await?;
  write_sock.flush().await?;

  let (sender_tx, sender_rx) = leo_async::mpsc::channel();

  all_senders.add(sender_tx.clone());

  let send_enc_key = multigimli3(key, &sender_iv, b"enc_key");
  let send_mac_key = multigimli3(key, &sender_iv, b"mac_key");

  let send_task = {
    async move {
      let mut write_sock_bufwriter = futures::io::BufWriter::new(write_sock);
      let mut keystream = KeystreamGen::new(&send_enc_key);
      let mut packet_data = BytesMut::with_capacity(2048);

      loop {
        let data = sender_rx.recv().await.unwrap();
        leo_async::yield_now().await;

        // Empty data means there is a cleaner checking the channel
        if data.is_empty() {
          continue;
        }

        let mac = multigimli2(&send_mac_key, &data);

        packet_data.reserve(2 + data.len() + 16);

        packet_data.extend_from_slice(&(data.len() as u16).to_le_bytes());
        packet_data.extend_from_slice(&data);
        packet_data.extend_from_slice(&mac);

        for byte in packet_data.iter_mut() {
          *byte ^= keystream.next();
        }

        let to_send = packet_data.split_to(packet_data.len()).freeze();

        match asyncwrite_writeall_timeout(&mut write_sock_bufwriter, &to_send, 30_000).await {
          Ok(_) => {}
          Err(e) => {
            crate::log!("connect_impl send_task write error: {}", e);
            return;
          }
        }
        match write_sock_bufwriter.flush().await {
          Ok(_) => {}
          Err(e) => {
            crate::log!("connect_impl send_task flush error: {}", e);
            return;
          }
        }
      }
    }
  };

  let recv_task = {
    let key = key.to_owned();

    async move {
      let mut read_sock_bufreader = futures::io::BufReader::new(read_sock);
      let key = &key;

      let recv_iv = {
        let mut buf = [0u8; 16];
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut buf, 30000).await?;
        buf
      };

      let expected_keyfinder = multigimli3(key, &recv_iv, b"keyfinder");
      let recv_keyfinder = {
        let mut buf = [0u8; 16];
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut buf, 30000).await?;
        buf
      };

      if expected_keyfinder != recv_keyfinder {
        return Err("Got incorrect keyfinder".into());
      }

      log!("Got correct keyfinder");

      let recv_enc_key = multigimli3(key, &recv_iv, b"enc_key");
      let recv_mac_key = multigimli3(key, &recv_iv, b"mac_key");

      let mut keystream = KeystreamGen::new(&recv_enc_key);
      let mut data = BytesMut::with_capacity(2048);

      loop {
        leo_async::yield_now().await;
        // Read and decrypt length
        let mut len_buf = [0u8; 2];
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut len_buf, 30_000).await?;
        len_buf[0] ^= keystream.next();
        len_buf[1] ^= keystream.next();
        let len = u16::from_le_bytes(len_buf);

        // Read and decrypt data
        data.resize(len as usize, 0);
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut data, 30_000).await?;
        for byte in data.iter_mut() {
          *byte ^= keystream.next();
        }

        // Read and decrypt MAC
        let mut mac = [0u8; 16];
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut mac, 30_000).await?;
        for byte in &mut mac {
          *byte ^= keystream.next();
        }

        // Verify MAC
        let expected_mac = multigimli2(&recv_mac_key, &data);

        if expected_mac != mac {
          return Err("MAC verification failed".into());
        }

        let to_send = data.split_to(data.len()).freeze();

        if let Err(err) = incoming.send((to_send, sender_tx.clone())) {
          log!("Error sending data: {:?}", err);
          break;
        }
      }

      DSSResult::Ok(())
    }
  };

  leo_async::select2_noresult(std::pin::pin!(send_task), std::pin::pin!(recv_task)).await;

  crate::info!("Connection finished");

  Ok(())
}

pub fn create_connection(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: leo_async::mpsc::Sender<(Bytes, leo_async::mpsc::Sender<Bytes>)>,
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
  conn: ArcFd,
  incoming: leo_async::mpsc::Sender<(Bytes, leo_async::mpsc::Sender<Bytes>)>,
) -> DSSResult<()> {
  let all_senders = all_senders::get();
  leo_async::socket::set_nodelay(&conn)?;

  let mut read_sock = conn.dup()?;
  let mut write_sock = conn.dup()?;

  fd_make_nonblocking(&read_sock)?;
  fd_make_nonblocking(&write_sock)?;

  let incoming_iv = {
    let mut buf = [0u8; 16];
    asyncread_readexact_timeout(&mut read_sock, &mut buf, 30_000).await?;
    buf
  };

  let incoming_keyfinder = {
    let mut buf = [0u8; 16];
    asyncread_readexact_timeout(&mut read_sock, &mut buf, 30_000).await?;
    buf
  };

  let mut found_key = None;

  for key in keys {
    let expected_keyfinder = multigimli3(&key, &incoming_iv, b"keyfinder");

    if expected_keyfinder == incoming_keyfinder {
      found_key = Some(key);
      break;
    }
  }

  if found_key.is_none() {
    return Err("Got incorrect keyfinder".into());
  }

  let found_key = found_key.unwrap();

  let recv_enc_key = multigimli3(&found_key, &incoming_iv, b"enc_key");
  let recv_mac_key = multigimli3(&found_key, &incoming_iv, b"mac_key");

  // Send our keyfinder
  let send_iv = {
    let mut iv = Vec::with_capacity(16);
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv.extend_from_slice(&crate::rng::u64().to_le_bytes());
    iv
  };

  let send_keyfinder = multigimli3(&found_key, &send_iv, b"keyfinder");

  asyncwrite_writeall_timeout(&mut write_sock, &send_iv, 30_000).await?;
  asyncwrite_writeall_timeout(&mut write_sock, &send_keyfinder, 30_000).await?;
  write_sock.flush().await?;

  let send_enc_key = multigimli3(&found_key, &send_iv, b"enc_key");
  let send_mac_key = multigimli3(&found_key, &send_iv, b"mac_key");

  let (sender_tx, sender_rx) = leo_async::mpsc::channel();
  all_senders.add(sender_tx.clone());

  let sender_task = {
    async move {
      let mut write_sock_bufwriter = futures::io::BufWriter::new(write_sock);
      let mut keystream = KeystreamGen::new(&send_enc_key);
      let mut packet_data = BytesMut::with_capacity(2048);

      loop {
        leo_async::yield_now().await;
        let packet = sender_rx.recv().await.unwrap();
        // Empty data means there is a cleaner checking the channel
        if packet.is_empty() {
          continue;
        }

        let mac = multigimli2(&send_mac_key, &packet);

        packet_data.reserve(2 + packet.len() + 16);

        packet_data.extend_from_slice(&(packet.len() as u16).to_le_bytes());
        packet_data.extend_from_slice(&packet);
        packet_data.extend_from_slice(&mac);

        for byte in packet_data.iter_mut() {
          *byte ^= keystream.next();
        }

        let to_send = packet_data.split_to(packet_data.len()).freeze();

        match asyncwrite_writeall_timeout(&mut write_sock_bufwriter, &to_send, 30_000).await {
          Ok(_) => {}
          Err(_) => return,
        }

        let should_flush = sender_rx.peek(|x| match x {
          None => true,
          Some(x) if x.is_empty() => true,
          _ => false,
        });

        if should_flush {
          match write_sock_bufwriter.flush().await {
            Ok(_) => {}
            Err(e) => {
              crate::log!("handle_connection sender_task flush error: {}", e);
              return;
            }
          }
        }
      }
    }
  };

  let recv_task = {
    async move {
      let mut read_sock_bufreader = futures::io::BufReader::new(read_sock);
      let mut keystream = KeystreamGen::new(&recv_enc_key);
      let mut data = BytesMut::with_capacity(2048);

      loop {
        leo_async::yield_now().await;
        // Read and decrypt length
        let mut len_buf = [0u8; 2];
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut len_buf, 30_000).await?;
        len_buf[0] ^= keystream.next();
        len_buf[1] ^= keystream.next();
        let len = u16::from_le_bytes(len_buf);

        // Read and decrypt data
        data.resize(len as usize, 0);
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut data, 30_000).await?;
        for byte in data.iter_mut() {
          *byte ^= keystream.next();
        }

        // Read and decrypt MAC
        let mut mac = [0u8; 16];
        asyncread_readexact_timeout(&mut read_sock_bufreader, &mut mac, 30_000).await?;
        for byte in &mut mac {
          *byte ^= keystream.next();
        }

        // Verify MAC
        let expected_mac = multigimli2(&recv_mac_key, &data);

        if expected_mac != mac {
          return Err("MAC verification failed".into());
        }

        let to_send = data.split_to(data.len()).freeze();
        incoming.send((to_send, sender_tx.clone())).unwrap();
      }

      DSSResult::Ok(())
    }
  };

  let res = leo_async::select2_noresult(std::pin::pin!(sender_task), std::pin::pin!(recv_task)).await;

  crate::log!("Connection tasks completed: {:?}", res);

  Ok(())
}

pub async fn listener_impl(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: leo_async::mpsc::Sender<(Bytes, leo_async::mpsc::Sender<Bytes>)>,
) -> DSSResult<()> {
  let socket = leo_async::socket::socket()?;
  fd_make_nonblocking(&socket)?;

  nix::sys::socket::setsockopt(&socket, nix::sys::socket::sockopt::ReuseAddr, &true)?;

  let bind_addr = nix::sys::socket::SockaddrIn::new(0, 0, 0, 0, port);
  nix::sys::socket::bind(socket.as_raw_fd(), &bind_addr)?;
  nix::sys::socket::listen(&socket, nix::sys::socket::Backlog::new(128)?)?;

  loop {
    if let Err(e) = leo_async::fd_wait_readable(&socket).await {
      crate::error!("fd_wait_readable error: {}", e);
      continue;
    }

    match nix::sys::socket::accept(socket.as_raw_fd()) {
      Ok(fd) => {
        let fd = ArcFd::from_raw_fd(fd);
        fd_make_nonblocking(&fd)?;
        leo_async::spawn(handle_connection(keys.clone(), fd, incoming.clone()));
      }
      Err(e) => {
        crate::error!("accept failed: {}", e);
        continue;
      }
    }
  }
}

pub fn listener(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: leo_async::mpsc::Sender<(Bytes, leo_async::mpsc::Sender<Bytes>)>,
) {
  leo_async::spawn(async move {
    loop {
      let incoming = incoming.clone();
      let keys = keys.clone();

      let res = listener_impl(port, keys, incoming.clone()).await;
      info!("Listener impl result: {:?}", res);

      let delay = 5.0 + crate::rng::uniform() * 5.0;
      leo_async::sleep_seconds(delay).await;
    }
  });
}
