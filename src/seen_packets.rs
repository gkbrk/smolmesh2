use std::hash::{DefaultHasher, Hash, Hasher};

pub(crate) struct SeenPackets {
  buckets: Vec<u64>,
  n: usize,
}

impl SeenPackets {
  pub(crate) fn new(n: usize) -> Self {
    let buckets = vec![0; n];

    Self { buckets, n }
  }

  pub(crate) fn add(&mut self, packet: &[u8]) {
    let mut hasher = DefaultHasher::new();
    packet.hash(&mut hasher);
    let hash = hasher.finish();
    let bucket = hash as usize % self.n;
    self.buckets[bucket] = hash;
  }

  pub(crate) fn contains(&self, packet: &[u8]) -> bool {
    let mut hasher = DefaultHasher::new();
    packet.hash(&mut hasher);
    let hash = hasher.finish();
    let bucket = hash as usize % self.n;
    self.buckets[bucket] == hash
  }
}
