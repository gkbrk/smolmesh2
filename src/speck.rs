pub fn multispeck2(x: &[u8], y: &[u8]) -> Vec<u8> {
  let mut a: u64 = 69;
  let mut b: u64 = 2;

  (a, b) = crate::raw_speck::ernd(a, b, x.len() as u64);

  for c in x {
    (a, b) = crate::raw_speck::ernd(a, b, *c as u64);
  }

  (a, b) = crate::raw_speck::ernd(a, b, 0);

  (a, b) = crate::raw_speck::ernd(a, b, y.len() as u64);

  for c in y {
    (a, b) = crate::raw_speck::ernd(a, b, *c as u64);
  }

  (a, b) = crate::raw_speck::ernd(a, b, 0);

  let mut res = Vec::new();
  res.extend_from_slice(&a.to_le_bytes());
  res.extend_from_slice(&b.to_le_bytes());
  res
}

pub fn multispeck3(x: &[u8], y: &[u8], z: &[u8]) -> Vec<u8> {
  let mut a: u64 = 69;
  let mut b: u64 = 3;

  (a, b) = crate::raw_speck::ernd(a, b, x.len() as u64);

  for c in x {
    (a, b) = crate::raw_speck::ernd(a, b, *c as u64);
  }

  (a, b) = crate::raw_speck::ernd(a, b, 0);

  (a, b) = crate::raw_speck::ernd(a, b, y.len() as u64);

  for c in y {
    (a, b) = crate::raw_speck::ernd(a, b, *c as u64);
  }

  (a, b) = crate::raw_speck::ernd(a, b, 0);

  (a, b) = crate::raw_speck::ernd(a, b, z.len() as u64);

  for c in z {
    (a, b) = crate::raw_speck::ernd(a, b, *c as u64);
  }

  (a, b) = crate::raw_speck::ernd(a, b, 0);

  let mut res = Vec::new();
  res.extend_from_slice(&a.to_le_bytes());
  res.extend_from_slice(&b.to_le_bytes());
  res
}
