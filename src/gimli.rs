pub const RATE_BITS: usize = 128;
pub const RATE_BYTES: usize = RATE_BITS / 8;

#[repr(align(4))]
pub(crate) struct GimliState {
  state: [u8; 48],
}

impl GimliState {
  pub(crate) fn new() -> Self {
    Self { state: [0u8; 48] }
  }

  pub(crate) fn permute(&mut self) {
    debug_assert!(
      self.state.as_ptr().align_offset(std::mem::align_of::<u32>()) == 0,
      "Bad alignment"
    );

    // Safety: We know that the state is aligned to a u32 boundary
    let state = unsafe { std::mem::transmute::<&mut [u8; 48], &mut [u32; 12]>(&mut self.state) };
    gimli(state);
  }

  pub(crate) fn buf(&self) -> &[u8; 48] {
    &self.state
  }

  pub(crate) fn buf_mut(&mut self) -> &mut [u8; 48] {
    &mut self.state
  }
}

#[inline(always)]
pub(crate) fn gimli(state: &mut [u32; 12]) {
  for round in (1..25).rev() {
    for col in 0..4 {
      let x = state[col].rotate_left(24);
      let y = state[col + 4].rotate_left(9);
      let z = state[col + 8];

      state[col + 8] = x ^ (z << 1) ^ ((y & z) << 2);
      state[col + 4] = y ^ x ^ ((x | z) << 1);
      state[col] = z ^ y ^ ((x & y) << 3);
    }

    if (round & 3) == 0 {
      // Small swap
      state.swap(0, 1);
      state.swap(2, 3);
    }

    if (round & 3) == 2 {
      // Big swap
      state.swap(0, 2);
      state.swap(1, 3);
    }

    if (round & 3) == 0 {
      // Add constant
      state[0] ^= 0x9e377900 | round as u32;
    }
  }
}

pub(crate) fn gimli_hash(input: &[u8], out: &mut [u8]) {
  let mut state = GimliState::new();

  // --- Absorb phase ---
  for chunk in input.chunks(RATE_BYTES) {
    for i in 0..chunk.len() {
      state.buf_mut()[i] ^= chunk[i];
    }

    if chunk.len() == RATE_BYTES {
      state.permute();
    }
  }

  // --- Padding phase ---
  let last_chunk_len = input.len() % RATE_BYTES;
  state.buf_mut()[last_chunk_len] ^= 0x1F; // First padding byte
  state.buf_mut()[RATE_BYTES - 1] ^= 0x80; // Second padding byte

  state.permute();

  // --- Squeezing phase ---
  let mut out_len = out.len();
  let mut out_offset = 0;

  while out_len > 0 {
    let block_size = std::cmp::min(out_len, RATE_BYTES);
    out[out_offset..out_offset + block_size].copy_from_slice(&state.buf()[..block_size]);

    out_offset += block_size;
    out_len -= block_size;

    if out_len > 0 {
      state.permute();
    }
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  // Test vectors from:
  // https://crypto.stackexchange.com/questions/51025/doubt-about-published-test-vectors-for-gimli-hash

  fn gimli_hash_assert(input: &[u8], expected_hash: &str) {
    let hash = {
      let mut hash = [0u8; 32];
      gimli_hash(input, &mut hash);
      hash
    };

    let hash_hex = {
      let mut hash_hex = String::new();
      for b in hash.iter() {
        hash_hex.push_str(&format!("{:02x}", b));
      }
      hash_hex
    };

    assert_eq!(hash_hex, expected_hash);
  }

  #[test]
  fn test_gimlihash_1() {
    gimli_hash_assert(b"", "b0634b2c0b082aedc5c0a2fe4ee3adcfc989ec05de6f00addb04b3aaac271f67");
  }

  #[test]
  fn test_gimlihash_2() {
    gimli_hash_assert(
      b"There's plenty for the both of us, may the best Dwarf win.",
      "4afb3ff784c7ad6943d49cf5da79facfa7c4434e1ce44f5dd4b28f91a84d22c8",
    );
  }
}
