use std::io::Write;

pub struct Rng {
  key: [u64; 32],
  ctr_high: u64,
  ctr_low: u64,
}

impl Rng {
  pub fn new() -> Self {
    let key = {
      let mut key = [0; 32];

      for i in 0..32 {
        key[i] = crate::raw_speck::ernd(69, 420, i as u64).0;
      }

      key
    };

    Rng {
      key,
      ctr_high: 0,
      ctr_low: 0,
    }
  }

  fn incr_ctr(&mut self) {
    self.ctr_low = self.ctr_low.wrapping_add(1);
    if self.ctr_low == 0 {
      self.ctr_high = self.ctr_high.wrapping_add(1);
    }
  }

  pub fn absorb(&mut self, data: u64) {
    self.incr_ctr();

    for i in 0..32 {
      let mut a = self.key[i];
      let mut b = i as u64;

      (a, b) = crate::raw_speck::ernd(a, b, data);
      (a, b) = crate::raw_speck::ernd(a, b, self.ctr_high);
      (a, b) = crate::raw_speck::ernd(a, b, self.ctr_low);

      for j in 0..32 {
        (a, b) = crate::raw_speck::ernd(a, b, self.key[j]);
      }

      (a, _) = crate::raw_speck::ernd(a, b, data);

      self.key[i] = a;
    }

    self.incr_ctr();
  }

  pub fn u64(&mut self) -> u64 {
    let mut a = self.ctr_high;
    let mut b = self.ctr_low;

    for i in 0..32 {
      (a, b) = crate::raw_speck::ernd(a, b, self.key[i]);
    }

    self.incr_ctr();

    a
  }

  pub fn uniform(&mut self) -> f64 {
    self.u64() as f64 / u64::MAX as f64
  }
}

impl Write for Rng {
  fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
    for byte in buf {
      self.absorb(*byte as u64);
    }

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
  RNG.lock().unwrap().absorb(data);
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
      let mut buf = [0; 64];
      match f.read(&mut buf) {
        Ok(_) => {
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

  // Mix a bit
  for i in 0..64 {
    rng.absorb(i);
  }
}
