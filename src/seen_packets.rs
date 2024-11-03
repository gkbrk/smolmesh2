use std::hash::{DefaultHasher, Hash, Hasher};

pub(crate) struct SeenPackets {
  hashset: std::collections::HashSet<u64>,
  deque: std::collections::VecDeque<u64>,
  n: usize,
}

impl SeenPackets {
  pub(crate) fn new(n: usize) -> Self {
    SeenPackets {
      hashset: std::collections::HashSet::new(),
      deque: std::collections::VecDeque::with_capacity(n),
      n,
    }
  }

  #[inline(always)]
  fn get_hash(&self, packet: &[u8]) -> u64 {
    let mut hasher = DefaultHasher::new();
    packet.hash(&mut hasher);
    hasher.finish()
  }

  pub(crate) fn add(&mut self, packet: &[u8]) {
    let hash = self.get_hash(packet);
    if !self.hashset.contains(&hash) {
      self.hashset.insert(hash);
      self.deque.push_back(hash);
      while self.deque.len() > self.n {
        let old_hash = self.deque.pop_front().unwrap();
        self.hashset.remove(&old_hash);
      }
    }
  }

  pub(crate) fn contains(&self, packet: &[u8]) -> bool {
    let hash = self.get_hash(packet);
    self.hashset.contains(&hash)
  }
}
