use std::hash::{Hash, Hasher};

pub(crate) struct SeenPackets {
  hashset: rustc_hash::FxHashSet<u64>,
  deque: std::collections::VecDeque<u64>,
  n: usize,
}

impl SeenPackets {
  pub(crate) fn new(n: usize) -> Self {
    SeenPackets {
      hashset: rustc_hash::FxHashSet::default(),
      deque: std::collections::VecDeque::with_capacity(n),
      n,
    }
  }

  #[inline(always)]
  fn get_hash(&self, packet: &[u8]) -> u64 {
    let mut hasher = rustc_hash::FxHasher::default();
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

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn test_new_packet_is_not_contained() {
    let seen_packets = SeenPackets::new(5);
    let packet = b"test packet";

    // A newly created SeenPackets should not contain any packets
    assert!(!seen_packets.contains(packet));
  }

  #[test]
  fn test_add_and_contains() {
    let mut seen_packets = SeenPackets::new(5);
    let packet = b"test packet";

    // Initially the packet should not be contained
    assert!(!seen_packets.contains(packet));

    // After adding, it should be contained
    seen_packets.add(packet);
    assert!(seen_packets.contains(packet));

    // Different packet should not be contained
    let different_packet = b"different packet";
    assert!(!seen_packets.contains(different_packet));
  }

  #[test]
  fn test_adding_same_packet_multiple_times() {
    let mut seen_packets = SeenPackets::new(5);
    let packet = b"test packet";

    // Add the packet
    seen_packets.add(packet);
    assert!(seen_packets.contains(packet));

    // Adding the same packet again should not change behavior
    seen_packets.add(packet);
    assert!(seen_packets.contains(packet));
  }

  #[test]
  fn test_capacity_limit() {
    let mut seen_packets = SeenPackets::new(3);

    // Add 3 packets (up to capacity)
    seen_packets.add(b"packet1");
    seen_packets.add(b"packet2");
    seen_packets.add(b"packet3");

    // All three packets should be contained
    assert!(seen_packets.contains(b"packet1"));
    assert!(seen_packets.contains(b"packet2"));
    assert!(seen_packets.contains(b"packet3"));

    // Add a fourth packet, which should push out the oldest one (packet1)
    seen_packets.add(b"packet4");

    // packet1 should no longer be contained
    assert!(!seen_packets.contains(b"packet1"));

    // packet2, packet3, and packet4 should be contained
    assert!(seen_packets.contains(b"packet2"));
    assert!(seen_packets.contains(b"packet3"));
    assert!(seen_packets.contains(b"packet4"));
  }

  #[test]
  fn test_multiple_capacity_limit_exceeding() {
    let mut seen_packets = SeenPackets::new(2);

    // Add packets sequentially, checking each state
    seen_packets.add(b"packet1");
    seen_packets.add(b"packet2");
    assert!(seen_packets.contains(b"packet1"));
    assert!(seen_packets.contains(b"packet2"));

    seen_packets.add(b"packet3");
    assert!(!seen_packets.contains(b"packet1"));
    assert!(seen_packets.contains(b"packet2"));
    assert!(seen_packets.contains(b"packet3"));

    seen_packets.add(b"packet4");
    assert!(!seen_packets.contains(b"packet1"));
    assert!(!seen_packets.contains(b"packet2"));
    assert!(seen_packets.contains(b"packet3"));
    assert!(seen_packets.contains(b"packet4"));
  }

  #[test]
  fn test_edge_cases() {
    // Test with capacity of 0
    let mut seen_packets = SeenPackets::new(0);
    seen_packets.add(b"packet");
    assert!(!seen_packets.contains(b"packet")); // Should be immediately forgotten

    // Test with capacity of 1
    let mut seen_packets = SeenPackets::new(1);
    seen_packets.add(b"packet1");
    assert!(seen_packets.contains(b"packet1"));
    seen_packets.add(b"packet2");
    assert!(!seen_packets.contains(b"packet1")); // Should be forgotten
    assert!(seen_packets.contains(b"packet2"));

    // Test with empty packet
    let mut seen_packets = SeenPackets::new(5);
    seen_packets.add(b"");
    assert!(seen_packets.contains(b""));
  }

  #[test]
  fn test_large_capacity() {
    // Test with a large capacity to ensure it works correctly
    let capacity = 1000;
    let mut seen_packets = SeenPackets::new(capacity);

    // Add many packets
    for i in 0..capacity {
      let packet = format!("packet{}", i).into_bytes();
      seen_packets.add(&packet);
    }

    // Verify all packets are still contained
    for i in 0..capacity {
      let packet = format!("packet{}", i).into_bytes();
      assert!(seen_packets.contains(&packet));
    }

    // Add one more to trigger the removal of the oldest
    let new_packet = format!("packet{}", capacity).into_bytes();
    seen_packets.add(&new_packet);

    // Oldest packet should be gone, newest should be there
    let oldest_packet = format!("packet0").into_bytes();
    assert!(!seen_packets.contains(&oldest_packet));
    assert!(seen_packets.contains(&new_packet));
  }
}
