use std::os::fd::AsRawFd;

use bytes::{Bytes, BytesMut};
use futures::{
  AsyncReadExt, AsyncWriteExt, StreamExt,
  channel::mpsc::{UnboundedSender, unbounded},
};

use crate::ascon::{self, AsconStream};
use crate::leo_async::{self, ArcFd};
use crate::{DSSResult, all_senders, info, log};

// The wire format matches dotnet/Smolmesh/LegacyTcp.cs. Each side sends a 16-byte random IV and a
// keyfinder, multi(key, iv, "keyfinder"), which tells a listener which of its keys is in use. The
// connecting side sends first. Each direction is then one AsconStream keyed with
// multi(key, sender_iv, receiver_iv, "stream"), and every frame is the 2-byte little-endian length
// and the packet, encrypted together, followed by the stream's tag.

const IO_TIMEOUT_MS: usize = 30_000;

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

fn keyfinder(key: &[u8], iv: &[u8; 16]) -> [u8; 16] {
  ascon::multi(&[key, iv, b"keyfinder"])
}

// Each direction's key depends on both IVs, sender's first, so replaying an old handshake can't
// bring back an old stream.
fn stream_key(key: &[u8], sender_iv: &[u8; 16], receiver_iv: &[u8; 16]) -> [u8; 16] {
  ascon::multi(&[key, sender_iv, receiver_iv, b"stream"])
}

fn ct_eq(a: &[u8; 16], b: &[u8; 16]) -> bool {
  a.iter().zip(b).fold(0, |acc, (x, y)| acc | (x ^ y)) == 0
}

fn random_iv() -> [u8; 16] {
  let mut iv = [0u8; 16];
  iv[..8].copy_from_slice(&crate::rng::u64().to_le_bytes());
  iv[8..].copy_from_slice(&crate::rng::u64().to_le_bytes());
  iv
}

fn encode_frame(packet: &[u8], cipher: &mut AsconStream, out: &mut BytesMut) {
  out.reserve(2 + packet.len() + AsconStream::TAG_SIZE);
  let start = out.len();
  out.extend_from_slice(&(packet.len() as u16).to_le_bytes());
  out.extend_from_slice(packet);
  let tag = cipher.seal(&mut out[start..]);
  out.extend_from_slice(&tag);
}

// Runs the handshake and then both directions until either fails. The initiator uses keys[0];
// a listener picks whichever of its keys matches the peer's keyfinder.
async fn run_link(
  conn: &ArcFd,
  keys: &[Vec<u8>],
  initiator: bool,
  incoming: UnboundedSender<(Bytes, UnboundedSender<Bytes>)>,
) -> DSSResult<()> {
  let read_sock = conn.dup()?;
  let mut write_sock = conn.dup()?;
  fd_make_nonblocking(&read_sock)?;
  fd_make_nonblocking(&write_sock)?;

  // All reads, the handshake included, go through one buffer: frames that arrive together with
  // the peer's handshake must not be lost.
  let mut reader = futures::io::BufReader::new(read_sock);
  let send_iv = random_iv();
  let mut recv_iv = [0u8; 16];
  let mut recv_keyfinder = [0u8; 16];

  let key = if initiator {
    let key = keys[0].clone();
    write_handshake(&mut write_sock, &key, &send_iv).await?;
    asyncread_readexact_timeout(&mut reader, &mut recv_iv, IO_TIMEOUT_MS).await?;
    asyncread_readexact_timeout(&mut reader, &mut recv_keyfinder, IO_TIMEOUT_MS).await?;
    if !ct_eq(&keyfinder(&key, &recv_iv), &recv_keyfinder) {
      return Err("Got incorrect keyfinder".into());
    }
    key
  } else {
    asyncread_readexact_timeout(&mut reader, &mut recv_iv, IO_TIMEOUT_MS).await?;
    asyncread_readexact_timeout(&mut reader, &mut recv_keyfinder, IO_TIMEOUT_MS).await?;
    let Some(key) = keys
      .iter()
      .find(|k| ct_eq(&keyfinder(k, &recv_iv), &recv_keyfinder))
      .cloned()
    else {
      return Err("Got incorrect keyfinder".into());
    };
    write_handshake(&mut write_sock, &key, &send_iv).await?;
    key
  };
  log!("Got correct keyfinder");

  let mut send_cipher = AsconStream::new(&stream_key(&key, &send_iv, &recv_iv));
  let mut recv_cipher = AsconStream::new(&stream_key(&key, &recv_iv, &send_iv));

  let (sender_tx, mut sender_rx) = unbounded::<Bytes>();
  all_senders::get().add(sender_tx.clone());

  let send_task = async move {
    let mut writer = futures::io::BufWriter::new(write_sock);
    let mut frame = BytesMut::with_capacity(2048);

    loop {
      leo_async::yield_now().await;
      let Some(packet) = sender_rx.next().await else {
        return;
      };
      // Empty data means there is a cleaner checking the channel
      if packet.is_empty() {
        continue;
      }
      if packet.len() > u16::MAX as usize {
        crate::log!(
          "Dropping packet that exceeds the legacy framing limit ({} bytes)",
          packet.len()
        );
        continue;
      }

      frame.clear();
      encode_frame(&packet, &mut send_cipher, &mut frame);

      if let Err(e) = asyncwrite_writeall_timeout(&mut writer, &frame, IO_TIMEOUT_MS).await {
        crate::log!("legacy_tcp send write error: {}", e);
        return;
      }
      if let Err(e) = writer.flush().await {
        crate::log!("legacy_tcp send flush error: {}", e);
        return;
      }
    }
  };

  let recv_task = async move {
    let mut frame = vec![0u8; 2 + u16::MAX as usize + AsconStream::TAG_SIZE];

    loop {
      leo_async::yield_now().await;
      // Decrypt the length with the next keystream bytes, then read the rest of the frame.
      asyncread_readexact_timeout(&mut reader, &mut frame[..2], IO_TIMEOUT_MS).await?;
      let keystream: [u8; 2] = recv_cipher.peek_keystream();
      let len = u16::from_le_bytes([frame[0] ^ keystream[0], frame[1] ^ keystream[1]]) as usize;
      asyncread_readexact_timeout(
        &mut reader,
        &mut frame[2..2 + len + AsconStream::TAG_SIZE],
        IO_TIMEOUT_MS,
      )
      .await?;

      let (message, tag) = frame[..2 + len + AsconStream::TAG_SIZE].split_at_mut(2 + len);
      if !recv_cipher.open(message, tag) {
        return Err("MAC verification failed".into());
      }

      let packet = Bytes::copy_from_slice(&message[2..]);
      if let Err(err) = incoming.unbounded_send((packet, sender_tx.clone())) {
        log!("Error sending data: {:?}", err);
        break;
      }
    }

    DSSResult::Ok(())
  };

  let res = leo_async::select2_noresult(std::pin::pin!(send_task), std::pin::pin!(recv_task)).await;
  crate::log!("Connection tasks completed: {:?}", res);

  Ok(())
}

async fn write_handshake(write_sock: &mut ArcFd, key: &[u8], iv: &[u8; 16]) -> DSSResult<()> {
  let mut handshake = [0u8; 32];
  handshake[..16].copy_from_slice(iv);
  handshake[16..].copy_from_slice(&keyfinder(key, iv));
  asyncwrite_writeall_timeout(write_sock, &handshake, IO_TIMEOUT_MS).await?;
  write_sock.flush().await?;
  Ok(())
}

async fn connect_impl(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: UnboundedSender<(Bytes, UnboundedSender<Bytes>)>,
) -> DSSResult<()> {
  crate::log!("Connecting to {}:{}", host, port);
  let addr: std::net::SocketAddr = format!("{}:{}", host, port).parse()?;
  let sock = connect_timeout(&addr, std::time::Duration::from_secs(5)).await?;
  crate::log!("Connected to {:?}", addr);
  leo_async::socket::set_nodelay(&sock)?;

  run_link(&sock, &[key.to_owned()], true, incoming).await?;

  crate::info!("Connection finished");

  Ok(())
}

pub fn create_connection(
  host: &str,
  port: u16,
  key: &[u8],
  incoming: UnboundedSender<(Bytes, UnboundedSender<Bytes>)>,
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
  incoming: UnboundedSender<(Bytes, UnboundedSender<Bytes>)>,
) -> DSSResult<()> {
  leo_async::socket::set_nodelay(&conn)?;
  run_link(&conn, &keys, false, incoming).await
}

pub async fn listener_impl(
  port: u16,
  keys: Vec<Vec<u8>>,
  incoming: UnboundedSender<(Bytes, UnboundedSender<Bytes>)>,
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

pub fn listener(port: u16, keys: Vec<Vec<u8>>, incoming: UnboundedSender<(Bytes, UnboundedSender<Bytes>)>) {
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
