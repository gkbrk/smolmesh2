pub struct SeenPackets {
  buckets: Vec<Vec<u8>>,
  key_high: u64,
  key_low: u64,
  n: usize,
}

impl SeenPackets {
  pub fn new(n: usize) -> Self {
    let buckets = {
      let mut res = Vec::new();

      for _ in 0..n {
        res.push(Vec::new());
      }

      res
    };

    Self {
      buckets,
      key_high: crate::rng::u64(),
      key_low: crate::rng::u64(),
      n,
    }
  }

  fn hash(&self, buf: &[u8]) -> u64 {
    let mut a = self.key_high;
    let mut b = self.key_low;

    // Length prefix
    (a, b) = crate::raw_speck::ernd(a, b, buf.len() as u64);
    (a, b) = crate::raw_speck::ernd(a, b, 0);

    for c in buf {
      (a, b) = crate::raw_speck::ernd(a, b, *c as u64);
      (a, b) = crate::raw_speck::ernd(a, b, 0);
    }

    a ^ b
  }

  pub fn add(&mut self, packet: &[u8]) {
    let hash = self.hash(packet);
    let bucket = hash as usize % self.n;
    self.buckets[bucket] = packet.to_vec();
  }

  pub fn contains(&self, packet: &[u8]) -> bool {
    let hash = self.hash(packet);
    let bucket = hash as usize % self.n;
    self.buckets[bucket] == packet
  }
}
