use std::collections::HashMap;
use std::sync::{OnceLock, RwLock};

use crate::log;

pub struct AllSenders {
  senders: RwLock<Vec<crossbeam::channel::Sender<Vec<u8>>>>,
  fastest_to_address: RwLock<HashMap<crate::ip_addr::IpAddr, (u64, crossbeam::channel::Sender<Vec<u8>>)>>,
}

static S_ALLSENDERS: OnceLock<AllSenders> = OnceLock::new();

pub fn get() -> &'static AllSenders {
  S_ALLSENDERS.get_or_init(|| AllSenders {
    senders: RwLock::new(Vec::new()),
    fastest_to_address: RwLock::new(HashMap::new()),
  })
}

impl AllSenders {
  pub fn add(&self, sender: crossbeam::channel::Sender<Vec<u8>>) {
    let mut senders = self.senders.write().unwrap();
    senders.push(sender);
  }

  pub fn add_fastest_to(&self, millis: u64, addr: crate::ip_addr::IpAddr, sender: crossbeam::channel::Sender<Vec<u8>>) {
    let mut fastest_to_address = self.fastest_to_address.write().unwrap();

    if let Some((millis_old, _)) = fastest_to_address.get(&addr) {
      if millis <= *millis_old {
        return;
      }
    }

    fastest_to_address.insert(addr, (millis, sender));
  }

  pub fn send_to_fastest(&self, addr: crate::ip_addr::IpAddr, data: Vec<u8>) {
    let fastest_to_address = self.fastest_to_address.read().unwrap();

    if let Some((_, sender)) = fastest_to_address.get(&addr) {
      if let Err(x) = sender.send(data) {
        log!("Error sending to fastest: {:?}", x);
      }
    } else {
      self.send_to_random(data);
    }
  }

  pub fn send_to_random(&self, data: Vec<u8>) {
    let senders = self.senders.read().unwrap();

    if senders.is_empty() {
      return;
    }

    let len = senders.len();
    let idx = crate::rng::u64() as usize % len;

    let _ = senders[idx].send(data);
  }

  pub fn clean_broken_senders(&self) {
    let mut senders = self.senders.write().unwrap();

    let len_orig = senders.len();

    senders.retain(|sender| {
      let res = sender.send(Vec::new());
      res.is_ok()
    });

    let len_new = senders.len();

    if len_orig != len_new {
      println!("Cleaned {} broken senders", len_orig - len_new);
    }
  }

  pub fn send_all(&self, data: Vec<u8>) {
    for sender in self.senders.read().unwrap().iter() {
      let _ = sender.send(data.clone());
    }
  }
}
