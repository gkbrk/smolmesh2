pub(super) const TRACE_ENABLED: bool = true;
pub(super) const DEBUG_ENABLED: bool = true;
pub(super) const INFO_ENABLED: bool = true;
pub(super) const WARN_ENABLED: bool = true;
pub(super) const ERROR_ENABLED: bool = true;

#[macro_export]
macro_rules! log {
  ($($arg:tt)*) => {
    $crate::debug!($($arg)*);
  };
}

#[macro_export]
macro_rules! trace {
  ($($arg:tt)*) => {
    if $crate::log::TRACE_ENABLED {
      let now = chrono::Local::now();
      let timestamp = now.format("%H:%M:%S");
      eprintln!("[{}] [TRC] {}", timestamp, format_args!($($arg)*));
    }
  };
}

#[macro_export]
macro_rules! debug {
  ($($arg:tt)*) => {
    if $crate::log::DEBUG_ENABLED {
      let now = chrono::Local::now();
      let timestamp = now.format("%H:%M:%S");
      eprintln!("[{}] [DBG] {}", timestamp, format_args!($($arg)*));
    }
  };
}

#[macro_export]
macro_rules! info {
  ($($arg:tt)*) => {
    if $crate::log::INFO_ENABLED {
      let now = chrono::Local::now();
      let timestamp = now.format("%H:%M:%S");
      eprintln!("[{}] [INF] {}", timestamp, format_args!($($arg)*));
    }
  };
}

#[macro_export]
macro_rules! error {
  ($($arg:tt)*) => {
    if $crate::log::ERROR_ENABLED {
      let now = chrono::Local::now();
      let timestamp = now.format("%H:%M:%S");
      eprintln!("[{}] [ERR] {}", timestamp, format_args!($($arg)*));
    }
  };
}

#[macro_export]
macro_rules! warn {
  ($($arg:tt)*) => {
    if $crate::log::WARN_ENABLED {
      let now = chrono::Local::now();
      let timestamp = now.format("%H:%M:%S");
      eprintln!("[{}] [WRN] {}", timestamp, format_args!($($arg)*));
    }
  };
}
