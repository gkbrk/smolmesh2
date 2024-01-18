#[derive(Eq, Hash, PartialEq, Debug, Copy, Clone)]
pub struct Addr(u64, u64);

impl Addr {
  pub fn from_buf(buf: &[u8]) -> Self {
    assert_eq!(buf.len(), 16);

    let high = u64::from_le_bytes([buf[0], buf[1], buf[2], buf[3], buf[4], buf[5], buf[6], buf[7]]);
    let low = u64::from_le_bytes([buf[8], buf[9], buf[10], buf[11], buf[12], buf[13], buf[14], buf[15]]);
    Self(low, high)
  }

  pub fn from_node_name(name: &str) -> Self {
    let mut a: u64 = 69;
    let mut b: u64 = name.len() as u64;

    for c in name.as_bytes() {
      (a, b) = crate::raw_speck::ernd(a, b, *c as u64);

      for _ in 0..32 {
        (a, b) = crate::raw_speck::ernd(a, b, 0);
      }
    }

    for _ in 0..32 {
      (a, b) = crate::raw_speck::ernd(a, b, 0);
    }

    loop {
      let buf = b.to_le_bytes();
      if buf[0] == 0xfd && buf[1] == 0x00 {
        break;
      }

      (a, b) = crate::raw_speck::ernd(a, b, 0);
    }

    Self(a, b)
  }

  pub fn to_ipv6(self) -> String {
    let mut res = Vec::new();
    res.extend_from_slice(&self.1.to_le_bytes());
    res.extend_from_slice(&self.0.to_le_bytes());

    // Format as IPv6
    let mut ipv6 = String::new();

    for i in 0..16 {
      if i != 0 && i % 2 == 0 {
        ipv6.push(':');
      }

      ipv6.push_str(&format!("{:02x}", res[i]));
    }

    ipv6
  }

  pub fn to_buf(self) -> [u8; 16] {
    let mut res = [0u8; 16];
    res[0..8].copy_from_slice(&self.1.to_le_bytes());
    res[8..16].copy_from_slice(&self.0.to_le_bytes());
    res
  }
}
