use std::collections::HashMap;
use std::sync::LazyLock;

use crate::{info, ip_addr, leo_async};

use bytes::Bytes;
use futures::{
  StreamExt,
  channel::mpsc::{UnboundedReceiver, UnboundedSender, unbounded},
};

enum AllSendersMessage {
  AddSender(UnboundedSender<Bytes>),
  AddFastestTo(u64, ip_addr::IpAddr, UnboundedSender<Bytes>),
  SendToFastest(ip_addr::IpAddr, Bytes),
  SendToAll(Bytes),
  CleanBrokenSenders,
}

async fn all_senders_task(mut receiver: UnboundedReceiver<AllSendersMessage>) {
  let mut senders: Vec<UnboundedSender<Bytes>> = Vec::new();
  let mut fastest_to_address: HashMap<ip_addr::IpAddr, (u64, UnboundedSender<Bytes>)> = HashMap::new();

  while let Some(msg) = receiver.next().await {
    match msg {
      AllSendersMessage::AddSender(sender) => senders.push(sender),
      AllSendersMessage::AddFastestTo(millis, addr, sender) => {
        if let Some((millis_old, _)) = fastest_to_address.get(&addr)
          && millis <= *millis_old
        {
          continue;
        }
        fastest_to_address.insert(addr, (millis, sender));
      }
      AllSendersMessage::SendToFastest(addr, data) => {
        if let Some((_, sender)) = fastest_to_address.get(&addr) {
          _ = sender.unbounded_send(data);
        }
      }
      AllSendersMessage::SendToAll(data) => {
        for sender in senders.iter() {
          _ = sender.unbounded_send(data.clone());
        }
      }
      AllSendersMessage::CleanBrokenSenders => {
        let mut num_cleaned = 0usize;

        senders.retain(|x| match x.unbounded_send(Bytes::new()) {
          Ok(_) => true,
          Err(_) => {
            num_cleaned += 1;
            false
          }
        });

        if num_cleaned > 0 {
          info!("Cleaned {} broken senders", num_cleaned);
        }
      }
    }
  }
}

pub struct AllSenders {
  sender: UnboundedSender<AllSendersMessage>,
}

static ALLSENDERS: LazyLock<AllSenders> = LazyLock::new(|| {
  let (sender, receiver) = unbounded::<AllSendersMessage>();
  leo_async::spawn(all_senders_task(receiver));
  AllSenders { sender }
});

pub fn get() -> &'static AllSenders {
  &ALLSENDERS
}

impl AllSenders {
  pub fn add(&self, sender: UnboundedSender<Bytes>) {
    _ = self.sender.unbounded_send(AllSendersMessage::AddSender(sender));
  }

  pub fn add_fastest_to(&self, millis: u64, addr: crate::ip_addr::IpAddr, sender: UnboundedSender<Bytes>) {
    _ = self.sender.unbounded_send(AllSendersMessage::AddFastestTo(millis, addr, sender));
  }

  pub fn send_to_fastest(&self, addr: crate::ip_addr::IpAddr, data: Bytes) {
    _ = self.sender.unbounded_send(AllSendersMessage::SendToFastest(addr, data));
  }

  pub fn clean_broken_senders(&self) {
    _ = self.sender.unbounded_send(AllSendersMessage::CleanBrokenSenders);
  }

  pub fn send_all(&self, data: Bytes) {
    _ = self.sender.unbounded_send(AllSendersMessage::SendToAll(data));
  }
}
