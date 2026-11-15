// Ascon-XOF128 from NIST SP 800-232, Multi (a hash of several inputs) and AsconStream, matching
// the .NET version in dotnet/Smolmesh/Crypto byte for byte.
// https://doi.org/10.6028/NIST.SP.800-232

pub const KEY_SIZE: usize = 16;

const HASH_RATE: usize = 8;
const AEAD_RATE: usize = 16;

// The XOF starts from a fixed IV with a zero state, so the first permutation is a constant.
const XOF_START: [u64; 5] = [
  0xda82ce768d9447eb,
  0xcc7ce6c75f1ef969,
  0xe7508fd780085631,
  0x0ee0ea53416b58cc,
  0xe0547524db6f0bde,
];

#[derive(Clone)]
struct State([u64; 5]);

impl State {
  #[inline(always)]
  fn round(&mut self, c: u64) {
    let [mut x0, x1, mut x2, x3, mut x4] = self.0;
    // Constant addition, then the S-box as a Keccak chi wrapped in linear XORs.
    x2 ^= c;
    x0 ^= x4;
    x4 ^= x3;
    x2 ^= x1;
    let mut t0 = x0 ^ (!x1 & x2);
    let mut t1 = x1 ^ (!x2 & x3);
    let t2 = x2 ^ (!x3 & x4);
    let mut t3 = x3 ^ (!x4 & x0);
    let t4 = x4 ^ (!x0 & x1);
    t1 ^= t0;
    t0 ^= t4;
    t3 ^= t2;
    // Linear diffusion; the S-box's final NOT of word 2 commutes with it and is applied last.
    self.0 = [
      t0 ^ t0.rotate_right(19) ^ t0.rotate_right(28),
      t1 ^ t1.rotate_right(61) ^ t1.rotate_right(39),
      !(t2 ^ t2.rotate_right(1) ^ t2.rotate_right(6)),
      t3 ^ t3.rotate_right(10) ^ t3.rotate_right(17),
      t4 ^ t4.rotate_right(7) ^ t4.rotate_right(41),
    ];
  }

  fn permute12(&mut self) {
    for c in [0xf0, 0xe1, 0xd2, 0xc3] {
      self.round(c);
    }
    self.permute8();
  }

  fn permute8(&mut self) {
    for c in [0xb4, 0xa5, 0x96, 0x87, 0x78, 0x69, 0x5a, 0x4b] {
      self.round(c);
    }
  }

  // Byte i of the rate, little-endian within each word.
  #[inline(always)]
  fn rate_byte(&self, i: usize) -> u8 {
    (self.0[i / 8] >> (8 * (i % 8))) as u8
  }

  #[inline(always)]
  fn xor_rate_byte(&mut self, i: usize, b: u8) {
    self.0[i / 8] ^= (b as u64) << (8 * (i % 8));
  }
}

// Ascon-XOF128 absorbing and squeezing, 8 bytes per 12-round permutation.
struct Xof {
  state: State,
  filled: usize,
}

impl Xof {
  fn new() -> Self {
    Self {
      state: State(XOF_START),
      filled: 0,
    }
  }

  fn absorb(&mut self, input: &[u8]) {
    for &b in input {
      self.state.xor_rate_byte(self.filled, b);
      self.filled += 1;
      if self.filled == HASH_RATE {
        self.state.permute12();
        self.filled = 0;
      }
    }
  }

  fn finish(mut self, output: &mut [u8]) {
    self.state.xor_rate_byte(self.filled, 0x01);
    self.state.permute12();
    let mut chunks = output.chunks_mut(HASH_RATE).peekable();
    while let Some(chunk) = chunks.next() {
      chunk.copy_from_slice(&self.state.0[0].to_le_bytes()[..chunk.len()]);
      if chunks.peek().is_some() {
        self.state.permute12();
      }
    }
  }
}

#[cfg(test)]
pub fn xof128(input: &[u8], output: &mut [u8]) {
  let mut xof = Xof::new();
  xof.absorb(input);
  xof.finish(output);
}

// An unambiguous hash of several inputs for key derivation and keyed hashing (a secret first
// input makes it a PRF): Ascon-XOF128 over each input's 8-byte little-endian length followed by
// its bytes.
pub fn multi(inputs: &[&[u8]]) -> [u8; 16] {
  let mut xof = Xof::new();
  for input in inputs {
    xof.absorb(&(input.len() as u64).to_le_bytes());
    xof.absorb(input);
  }
  let mut output = [0u8; 16];
  xof.finish(&mut output);
  output
}

// One direction of a connection as a single Ascon duplex that is never reset, so each message's
// tag authenticates the whole stream so far and replayed, reordered or dropped messages fail.
// Per message: encrypt it like an Ascon-AEAD128 message (16 bytes per 8 rounds, padded final
// block), key the capacity, 12 rounds, tag = first 8 bytes of the rate, then 8 more rounds.
// The key must be unique per connection and direction. After a failed open the stream is
// unusable: its state has absorbed the forgery.
pub struct AsconStream {
  state: State,
  k0: u64,
  k1: u64,
  failed: bool,
}

impl AsconStream {
  pub const TAG_SIZE: usize = 8;

  // Ascon-AEAD128's IV with a different last byte, so the stream never starts from a state a
  // standard AEAD under the same key could reach.
  const STREAM_IV: u64 = 0x00001000808c00ff;

  pub fn new(key: &[u8; KEY_SIZE]) -> Self {
    let k0 = u64::from_le_bytes(key[..8].try_into().unwrap());
    let k1 = u64::from_le_bytes(key[8..].try_into().unwrap());
    let mut state = State([Self::STREAM_IV, k0, k1, 0, 0]);
    state.permute12();
    state.0[3] ^= k0;
    state.0[4] ^= k1;
    Self {
      state,
      k0,
      k1,
      failed: false,
    }
  }

  // The keystream for the next message's first N (up to 16) bytes, without advancing, so a
  // length prefix can be decrypted before the rest of the message arrives.
  pub fn peek_keystream<const N: usize>(&self) -> [u8; N] {
    std::array::from_fn(|i| self.state.rate_byte(i))
  }

  // Encrypts message in place and returns its tag.
  pub fn seal(&mut self, message: &mut [u8]) -> [u8; Self::TAG_SIZE] {
    assert!(!self.failed, "AsconStream failed authentication");
    let mut blocks = message.chunks_exact_mut(AEAD_RATE);
    for block in &mut blocks {
      for (i, b) in block.iter_mut().enumerate() {
        self.state.xor_rate_byte(i, *b);
        *b = self.state.rate_byte(i);
      }
      self.state.permute8();
    }
    let tail = blocks.into_remainder();
    for (i, b) in tail.iter_mut().enumerate() {
      self.state.xor_rate_byte(i, *b);
      *b = self.state.rate_byte(i);
    }
    self.state.xor_rate_byte(tail.len(), 0x01);
    self.tag()
  }

  // Decrypts message in place and returns whether the tag is valid. On failure the message is
  // cleared and the stream can't be used again.
  pub fn open(&mut self, message: &mut [u8], tag: &[u8]) -> bool {
    assert!(!self.failed, "AsconStream failed authentication");
    let mut blocks = message.chunks_exact_mut(AEAD_RATE);
    for block in &mut blocks {
      self.decrypt_block(block);
      self.state.permute8();
    }
    let tail = blocks.into_remainder();
    self.decrypt_block(tail);
    self.state.xor_rate_byte(tail.len(), 0x01);

    let expected = self.tag();
    let diff = expected
      .iter()
      .zip(tag)
      .fold((tag.len() != Self::TAG_SIZE) as u8, |acc, (a, b)| acc | (a ^ b));
    if diff == 0 {
      return true;
    }
    self.failed = true;
    message.fill(0);
    false
  }

  // Absorbing the plaintext leaves the ciphertext in the rate.
  #[inline(always)]
  fn decrypt_block(&mut self, block: &mut [u8]) {
    for (i, b) in block.iter_mut().enumerate() {
      let c = *b;
      *b = c ^ self.state.rate_byte(i);
      self.state.xor_rate_byte(i, *b);
    }
  }

  // Keying the capacity before the tag permutation means that even a recovered state can't
  // produce tags without the key.
  fn tag(&mut self) -> [u8; Self::TAG_SIZE] {
    self.state.0[2] ^= self.k0;
    self.state.0[3] ^= self.k1;
    self.state.permute12();
    let tag = self.state.0[0].to_le_bytes();
    self.state.permute8();
    tag
  }
}

#[cfg(test)]
mod tests {
  use super::*;

  fn sequence(start: usize, len: usize) -> Vec<u8> {
    (start..start + len).map(|i| i as u8).collect()
  }

  fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{:02x}", b)).collect()
  }

  // Vectors from the Ascon reference implementation's KAT files, as in the .NET tests.
  #[test]
  fn xof128_vectors() {
    let cases = [
      (
        0,
        "473d5e6164f58b39dfd84aacdb8ae42ec2d91fed33388ee0d960d9b3993295c6ad77855a5d3b13fe6ad9e6098988373af7d0956d05a8f1665d2c67d1a3ad10ff",
      ),
      (
        1,
        "51430e0438ecdf642b393630d977625f5f337656ba58ab1e960784ac32a16e0d446405551f5469384f8ea283cf12e64fa72c426bfebaea3aa1529e2c4ab23a2f",
      ),
      (
        7,
        "7ae562db37212a9acd2673ecfd5b4f1c5cb2e6f64ebf00aa7f6ef8dc82c448d5fe11cd91f4368c37690d79e5de0ca8ad419e1918ce8dab2d42363e9476638a7b",
      ),
      (
        8,
        "8d1886f5d3ec4af8d15b44bc62b74da6ea91bc28fb82f9c34079b5ed6e38b6c951803d7dfb3c5e512a0ef5e4060062a6fd067f9c73ef9bee527411bda67fc896",
      ),
      (
        9,
        "db3013bfbbd132dc1d3152fd955ed48f7cbb675e9ad2a2fecf92b74c957592e0c89959e81c16fd07ead9eeb8e40359c497aa20258b43d87ec69ad0bb0993fd38",
      ),
      (
        33,
        "fef74b7ebd183ba1d87bf414000b29258d6a2233a2a03ed519c646b351bc008464cb725c2922e77a5e2b71f2d48e8d1ab34b45c3da91f5d46c9c3d9ae9057e02",
      ),
      (
        128,
        "7a3e9b27b9283b7f9ec5463888974d44ee8d270019d9cc914c55efb5659a40556e7c685f572dae31236dab27a74a7c2f661cd8ddd27fd0e4da708fd1226e992c",
      ),
    ];
    for (len, digest) in cases {
      // Shorter outputs are prefixes, including ones that end inside a squeezed word.
      for out_len in [64, 33, 5, 0] {
        let mut output = vec![0u8; out_len];
        xof128(&sequence(0, len), &mut output);
        assert_eq!(hex(&output), digest[..2 * out_len]);
      }
    }
  }

  #[test]
  fn multi_is_xof_over_length_prefixed_inputs() {
    let cases: &[&[usize]] = &[&[], &[0], &[16, 16, 13], &[1, 2, 3, 4, 5, 6, 7], &[7, 0, 9, 33], &[100]];
    for lengths in cases {
      let inputs: Vec<Vec<u8>> = lengths.iter().enumerate().map(|(i, &n)| sequence(i * 50, n)).collect();
      let refs: Vec<&[u8]> = inputs.iter().map(|x| x.as_slice()).collect();
      let encoded: Vec<u8> = inputs
        .iter()
        .flat_map(|x| (x.len() as u64).to_le_bytes().into_iter().chain(x.iter().copied()))
        .collect();
      let mut expected = [0u8; 16];
      xof128(&encoded, &mut expected);
      assert_eq!(multi(&refs), expected);
    }
    assert_ne!(multi(&[b"ab", b"c"]), multi(&[b"a", b"bc"]));
    assert_ne!(multi(&[]), multi(&[b""]));
  }

  // Known answers from the .NET Ascon.Multi.
  #[test]
  fn multi_matches_dotnet() {
    let key = b"interop-secret";
    assert_eq!(hex(&multi(&[])), "473d5e6164f58b39dfd84aacdb8ae42e");
    assert_eq!(hex(&multi(&[b""])), "1c4129312f3b50523e43f1637a75da21");
    assert_eq!(hex(&multi(&[b"ab", b"c"])), "60763b5f4b203cda54101243a7d0b6b5");
    assert_eq!(
      hex(&multi(&[key, &sequence(0, 16), b"keyfinder"])),
      "681ed0ce65f3419e32ad77914f9ed789"
    );
    assert_eq!(hex(&multi(&[&sequence(0, 100)])), "87d9b9137eee51aba5e980387f3d92b0");
    assert_eq!(
      hex(&multi(&[key, &sequence(0, 16), &sequence(0x10, 16), b"stream"])),
      "db21d6f5614a3139a52d406304993d4a"
    );
  }

  // Frames from the .NET LegacyTcp.Encode, all sealed in order on one stream: the 2-byte length
  // and the packet encrypted together, then the tag. The 1500-byte frame is checked by its
  // first 16 bytes and its tag.
  #[test]
  fn stream_frames_match_dotnet() {
    let cases = [
      (0, "a844", "a8448c277dc2afab8be3"),
      (1, "70bb", "71bbc2caf635802e72edc8"),
      (13, "6624", "6b24ad85b6ac9af5866101d45cbecf58a150b3145bed25"),
      (14, "8a08", "840808e9489bd66c3858a13ed6a3b838b84756d301c9f6a6"),
      (15, "95b4", "9ab49b8588c96246bd21907432a592e1a437cce1ccab130e82"),
      (16, "c6fa", "d6fa5f25499e8a2ace81b0aa4d78f0d5ff0e82499fd899a9be2f"),
      (17, "6f98", "7e987ef3aead8e373326339d5fbc49fc8ada945817fbed2a78b335"),
      (
        30,
        "e577",
        "fb77161cb5902a53c746e0e76025c2ed37a92b9f967521c6064b48743a9c6c37b8bf29a53416a4c4",
      ),
      (
        31,
        "d42a",
        "cb2ab026c30fd22bb075a5b336cac0d06110feac89ad5b02107308e259bedf9dbdf99813f98339450c",
      ),
      (
        33,
        "ad2a",
        "8c2a9521c86be2b64c43914065b853b31a1b30d15d7717a8ff7184a17b0a076cbbce93447b7db857c6392a",
      ),
      (
        100,
        "3481",
        "508169f4a0fe4769354f0e3a0e7eebe7cbda230141842472b32837d6e43cd9b1fd498d2fa097079f9f0fe814a96ff90115b0673968a1ceaea96f3cc6e52b197855234b21995ba3a71c651cd13f0a7e71d9391930662129124398827c4f0aaad59cc6b19a7e93707d872ac5103dee",
      ),
    ];
    let key: [u8; 16] = sequence(0, 16).try_into().unwrap();
    let mut tx = AsconStream::new(&key);
    let mut rx = AsconStream::new(&key);
    let mut seal = |len: usize, peek: &str| {
      assert_eq!(hex(&tx.peek_keystream::<2>()), peek);
      let mut frame = (len as u16).to_le_bytes().to_vec();
      frame.extend(sequence(0, len));
      let tag = tx.seal(&mut frame);
      let ciphertext = frame.clone();
      assert!(rx.open(&mut frame, &tag));
      assert_eq!(frame[2..], sequence(0, len));
      [ciphertext, tag.to_vec()].concat()
    };
    for (len, peek, expected) in cases {
      assert_eq!(hex(&seal(len, peek)), expected);
    }
    let frame = seal(1500, "b438");
    assert_eq!(frame.len(), 2 + 1500 + AsconStream::TAG_SIZE);
    assert_eq!(hex(&frame[..16]), "683df75604fc005c795fb4f0d186d15f");
    assert_eq!(hex(&frame[frame.len() - 8..]), "23208f7eb83458e6");
  }

  #[test]
  fn stream_round_trips_and_rejects_reordered_messages() {
    let key: [u8; 16] = sequence(0, 16).try_into().unwrap();
    let mut tx = AsconStream::new(&key);
    let mut rx = AsconStream::new(&key);
    let mut messages = Vec::new();
    for len in [0, 1, 15, 16, 17, 33, 1500] {
      let plain = sequence(len, len);
      let mut message = plain.clone();
      let keystream: [u8; 2] = rx.peek_keystream();
      let tag = tx.seal(&mut message);
      if len >= 2 {
        assert_eq!(plain[0] ^ keystream[0], message[0]);
      }
      messages.push((message.clone(), tag));
      assert!(rx.open(&mut message, &tag));
      assert_eq!(plain, message);
    }

    // Out of order: the second message's tag fails on a fresh receiver.
    let mut late = AsconStream::new(&key);
    let (mut second, tag) = messages[1].clone();
    assert!(!late.open(&mut second, &tag));
    assert!(second.iter().all(|&b| b == 0));
  }
}
