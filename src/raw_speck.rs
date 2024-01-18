#[inline(always)]
pub fn ernd(x: u64, y: u64, k: u64) -> (u64, u64) {
  let mut x = x;
  let mut y = y;
  x = x.rotate_right(8);
  x = x.wrapping_add(y);
  x ^= k;
  y = y.rotate_left(3);
  y ^= x;
  (x, y)
}

#[inline(always)]
pub fn drnd(x: u64, y: u64, k: u64) -> (u64, u64) {
  let mut x = x;
  let mut y = y;
  y ^= x;
  y = y.rotate_right(3);
  x ^= k;
  x = x.wrapping_sub(y);
  x = x.rotate_left(8);
  (x, y)
}

pub fn key_schedule(key: &[u8], rounds: usize) -> Vec<u64> {
  assert_eq!(key.len() % 8, 0);

  let mut res = Vec::new();
  let mut a = Vec::new();

  {
    let mut i = 0;

    while i < key.len() {
      let mut x = 0;

      for j in 0..8 {
        x |= (key[i + j] as u64) << (j * 8);
      }

      a.push(x);
      i += 8;
    }
  }

  let mut b = a.remove(0);
  let len_a = a.len();

  for i in 0..rounds {
    res.push(b);
    let ind = i % len_a;
    (a[ind], b) = ernd(a[ind], b, i as u64);
  }

  res
}

#[cfg(test)]
mod tests {
  use super::*;

  #[test]
  fn test_ernd_basic() {
    assert_eq!(ernd(1, 2, 3), (72057594037927937, 72057594037927953));
  }

  #[test]
  fn test_drnd_basic() {
    assert_eq!(drnd(1, 2, 3), (672, 6917529027641081856));
  }
}
