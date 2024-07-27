#[macro_export]
macro_rules! log {
  ($($arg:tt)*) => {
    let now = chrono::Local::now();
    let timestamp = now.format("%Y-%m-%d %H:%M:%S");
    println!("[{}] {}", timestamp, format_args!($($arg)*));
  };
}
