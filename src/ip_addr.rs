use std::fmt::Display;

#[derive(Eq, Hash, PartialEq, Debug, Copy, Clone)]
pub(crate) enum IpAddr {
  V4(u8, u8, u8, u8),
  V6(u8, u8, u8, u8, u8, u8, u8, u8, u8, u8, u8, u8, u8, u8, u8, u8),
}

impl IpAddr {
  pub(crate) fn ipv4_from_buf(buf: &[u8]) -> Self {
    assert_eq!(buf.len(), 4);

    let a = buf[0];
    let b = buf[1];
    let c = buf[2];
    let d = buf[3];

    IpAddr::V4(a, b, c, d)
  }

  pub(crate) fn ipv6_from_buf(buf: &[u8]) -> Self {
    assert_eq!(buf.len(), 16);

    let a = buf[0];
    let b = buf[1];
    let c = buf[2];
    let d = buf[3];
    let e = buf[4];
    let g = buf[5];
    let h = buf[6];
    let i = buf[7];
    let j = buf[8];
    let k = buf[9];
    let l = buf[10];
    let m = buf[11];
    let n = buf[12];
    let o = buf[13];
    let p = buf[14];
    let q = buf[15];

    IpAddr::V6(a, b, c, d, e, g, h, i, j, k, l, m, n, o, p, q)
  }

  pub(crate) fn from_node_name(name: &str) -> Self {
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

    let a = a.to_le_bytes();
    let b = b.to_le_bytes();

    IpAddr::V6(
      b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7],
    )
  }
}

impl Display for IpAddr {
  fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
    match self {
      IpAddr::V4(a, b, c, d) => write!(f, "{}.{}.{}.{}", a, b, c, d),
      IpAddr::V6(a, b, c, d, e, g, h, i, j, k, l, m, n, o, p, q) => write!(
        f,
        "{:02x}{:02x}:{:02x}{:02x}:{:02x}{:02x}:{:02x}{:02x}:{:02x}{:02x}:{:02x}{:02x}:{:02x}{:02x}:{:02x}{:02x}",
        a, b, c, d, e, g, h, i, j, k, l, m, n, o, p, q
      ),
    }
  }
}
