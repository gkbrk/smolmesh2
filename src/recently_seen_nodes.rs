use std::collections::HashMap;
use std::sync::{OnceLock, RwLock};

pub struct RecentlySeen {
  pub node_names: RwLock<HashMap<String, u64>>,
  pub traceroutes: RwLock<HashMap<String, String>>,
}

static S_RECENTLY_SEEN: OnceLock<RecentlySeen> = OnceLock::new();

pub fn get() -> &'static RecentlySeen {
  S_RECENTLY_SEEN.get_or_init(|| RecentlySeen {
    node_names: RwLock::new(HashMap::new()),
    traceroutes: RwLock::new(HashMap::new()),
  })
}

impl RecentlySeen {
  pub fn set_alive(&self, node_name: &str) {
    let mut node_names = self.node_names.write().unwrap();

    node_names.insert(node_name.to_string(), crate::millis());
  }

  pub fn recent_list(&self) -> Vec<(String, u64)> {
    let node_names = self.node_names.read().unwrap();

    let mut node_names: Vec<_> = node_names.iter().collect();
    node_names.sort_by_key(|(_, millis)| *millis);
    node_names.reverse();

    let mut res = Vec::new();
    for (node_name, ms) in node_names {
      res.push((node_name.clone(), *ms));
    }

    res
  }

  pub fn clear_traceroutes(&self) {
    let mut traceroutes = self.traceroutes.write().unwrap();

    traceroutes.clear();
  }

  pub fn set_traceroute(&self, tgt: &str, path: &str) {
    let mut traceroutes = self.traceroutes.write().unwrap();

    traceroutes.insert(tgt.to_string(), path.to_string());
  }

  pub fn get_traceroute(&self, tgt: &str) -> Option<String> {
    let traceroutes = self.traceroutes.read().unwrap();

    traceroutes.get(tgt).cloned()
  }
}
