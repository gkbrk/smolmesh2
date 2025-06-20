use crate::gimli;
use std::io::Write;

trait UnwrapUnreachable<T> {
  fn unwrap_unreachable(self) -> T;
}

impl<T> UnwrapUnreachable<T> for Option<T> {
  fn unwrap_unreachable(self) -> T {
    self.unwrap_or_else(|| unsafe { std::hint::unreachable_unchecked() })
  }
}

impl<T, E> UnwrapUnreachable<T> for Result<T, E> {
  fn unwrap_unreachable(self) -> T {
    self.unwrap_or_else(|_| unsafe { std::hint::unreachable_unchecked() })
  }
}

pub struct Rng {
  state: gimli::GimliState,
}

impl Rng {
  pub fn new() -> Self {
    Rng {
      state: gimli::GimliState::new(),
    }
  }

  pub fn absorb(&mut self, data: &[u8]) {
    // Absorb data length
    for (i, c) in data.len().to_le_bytes().iter().enumerate() {
      self.state.buf_mut()[i] ^= *c;
    }
    self.state.permute();

    for chunk in data.chunks(gimli::RATE_BYTES) {
      for (i, c) in chunk.iter().enumerate() {
        self.state.buf_mut()[i] ^= *c;
      }
      self.state.permute();
    }
  }

  pub fn u64(&mut self) -> u64 {
    let res = {
      let a = self.state.buf()[0..8].try_into().unwrap_unreachable();
      u64::from_le_bytes(a)
    };
    self.state.permute();
    res
  }

  pub fn uniform(&mut self) -> f64 {
    self.u64() as f64 / u64::MAX as f64
  }
}

impl Write for Rng {
  fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
    self.absorb(buf);
    Ok(buf.len())
  }

  fn flush(&mut self) -> std::io::Result<()> {
    Ok(())
  }
}

lazy_static! {
  pub static ref RNG: std::sync::Mutex<Rng> = std::sync::Mutex::new(Rng::new());
}

pub fn absorb(data: u64) {
  RNG.lock().unwrap().absorb(&data.to_le_bytes());
}

pub fn u64() -> u64 {
  RNG.lock().unwrap().u64()
}

pub fn uniform() -> f64 {
  RNG.lock().unwrap().uniform()
}

fn read_from_urandom<T: Write>(rng: &mut T) {
  use std::fs::File;
  use std::io::Read;

  match File::open("/dev/urandom") {
    Ok(mut f) => {
      writeln!(rng, "Opened!").unwrap();
      let mut buf = [0u8; 64];
      // Read exactly 64 bytes from /dev/urandom, treating short reads as errors
      match f.read_exact(&mut buf) {
        Ok(()) => {
          writeln!(rng, "urandom buffer: {:?}", buf).unwrap();
        }
        Err(e) => {
          writeln!(rng, "Failed to read from /dev/urandom: {:?}", e).unwrap();
        }
      }
    }
    Err(e) => {
      writeln!(rng, "Failed to open /dev/urandom: {:?}", e).unwrap();
    }
  }
}

pub fn init_rng() {
  let mut rng = RNG.lock().unwrap();

  // Write application specific seed
  writeln!(rng, "Smolmesh2").unwrap();

  // Process ID
  writeln!(rng, "{:?}", std::process::id()).unwrap();

  // Thread ID and name
  writeln!(rng, "{:?}", std::thread::current().id()).unwrap();
  writeln!(rng, "{:?}", std::thread::current().name()).unwrap();

  // Build-time constants for build target
  writeln!(rng, "{:?}", std::env::consts::ARCH).unwrap();
  writeln!(rng, "{:?}", std::env::consts::DLL_EXTENSION).unwrap();
  writeln!(rng, "{:?}", std::env::consts::DLL_PREFIX).unwrap();
  writeln!(rng, "{:?}", std::env::consts::DLL_SUFFIX).unwrap();
  writeln!(rng, "{:?}", std::env::consts::EXE_EXTENSION).unwrap();
  writeln!(rng, "{:?}", std::env::consts::EXE_SUFFIX).unwrap();
  writeln!(rng, "{:?}", std::env::consts::FAMILY).unwrap();
  writeln!(rng, "{:?}", std::env::consts::OS).unwrap();

  // Command line arguments
  writeln!(rng, "Command line arguments").unwrap();

  for arg in std::env::args_os() {
    writeln!(rng, "{:?}", arg).unwrap();
  }

  // Current dir
  writeln!(rng, "Current dir: {:?}", std::env::current_dir()).unwrap();

  // Current executable
  writeln!(rng, "Current exe: {:?}", std::env::current_exe()).unwrap();

  // Temp dir
  writeln!(rng, "Temp dir: {:?}", std::env::temp_dir()).unwrap();

  // Environment variables
  for (key, value) in std::env::vars_os() {
    writeln!(rng, "{:?} -> {:?}", key, value).unwrap();
  }

  // Timestamps
  writeln!(rng, "{:?}", std::time::SystemTime::now()).unwrap();
  writeln!(rng, "{:?}", std::time::Instant::now()).unwrap();

  // Attempt to read from /dev/urandom
  read_from_urandom(&mut *rng);
}
