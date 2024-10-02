pub(super) const TRACE_ENABLED: bool = false;
pub(super) const DEBUG_ENABLED: bool = true;
pub(super) const INFO_ENABLED: bool = true;
pub(super) const WARN_ENABLED: bool = true;
pub(super) const ERROR_ENABLED: bool = true;

// Generalized logging macro
#[macro_export]
macro_rules! log_level {
    ($level_enabled:expr, $level_str:expr, $($arg:tt)*) => {
        if $level_enabled {
            let now = chrono::Local::now();
            let timestamp = now.format("%H:%M:%S");
            eprintln!("[{}] [{}] {}", timestamp, $level_str, format_args!($($arg)*));
        }
    };
}

// Specific log level macros using the generalized macro
#[macro_export]
macro_rules! trace {
    ($($arg:tt)*) => {
        $crate::log_level!($crate::log::TRACE_ENABLED, "TRC", $($arg)*);
    };
}

#[macro_export]
macro_rules! debug {
    ($($arg:tt)*) => {
        $crate::log_level!($crate::log::DEBUG_ENABLED, "DBG", $($arg)*);
    };
}

#[macro_export]
macro_rules! info {
    ($($arg:tt)*) => {
        $crate::log_level!($crate::log::INFO_ENABLED, "INF", $($arg)*);
    };
}

#[macro_export]
macro_rules! warn {
    ($($arg:tt)*) => {
        $crate::log_level!($crate::log::WARN_ENABLED, "WRN", $($arg)*);
    };
}

#[macro_export]
macro_rules! error {
    ($($arg:tt)*) => {
        $crate::log_level!($crate::log::ERROR_ENABLED, "ERR", $($arg)*);
    };
}

#[macro_export]
macro_rules! log {
    ($($arg:tt)*) => {
        $crate::debug!($($arg)*);
    };
}
