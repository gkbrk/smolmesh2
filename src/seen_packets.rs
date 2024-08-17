use std::hash::{DefaultHasher, Hash, Hasher};

pub(crate) struct SeenPackets {
  buckets: Vec<u64>,
}

impl SeenPackets {
  pub(crate) fn new(n: usize) -> Self {
    Self { buckets: vec![0; n] }
  }

  #[inline(always)]
  fn bucket_and_hash(&self, packet: &[u8]) -> (usize, u64) {
    let mut hasher = DefaultHasher::new();
    packet.hash(&mut hasher);
    let hash = hasher.finish();
    (hash as usize % self.buckets.len(), hash)
  }

  pub(crate) fn add(&mut self, packet: &[u8]) {
    let (bucket, hash) = self.bucket_and_hash(packet);
    self.buckets[bucket] = hash;
  }

  pub(crate) fn contains(&self, packet: &[u8]) -> bool {
    let (bucket, hash) = self.bucket_and_hash(packet);
    self.buckets[bucket] == hash
  }
}
