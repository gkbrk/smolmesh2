use std::{
  future::Future,
  os::fd::BorrowedFd,
  pin::Pin,
  sync::{Arc, LazyLock, Mutex, OnceLock, RwLock},
  task::{Poll, Waker},
  time::Instant,
};

use crate::{error, trace};

fn noisytimer(s: impl ToString, milliseconds: u64) -> impl Drop {
  struct X(String, std::time::Instant, std::time::Duration);

  impl Drop for X {
    fn drop(&mut self) {
      let now = std::time::Instant::now();
      let dur = now - self.1;

      if dur > self.2 {
        crate::warn!("NoisyTimer: {} took too long ({:?})", self.0, dur);
      }
    }
  }

  X(
    s.to_string(),
    std::time::Instant::now(),
    std::time::Duration::from_millis(milliseconds),
  )
}

pub(super) type DSSResult<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

struct Task {
  future: Mutex<Option<Pin<Box<dyn Future<Output = ()> + Send + 'static>>>>,
  sender: std::sync::mpsc::Sender<Arc<Task>>,
}

impl std::task::Wake for Task {
  fn wake(self: Arc<Self>) {
    let sender = self.sender.clone();
    sender.send(self).unwrap();
  }

  fn wake_by_ref(self: &Arc<Self>) {
    self.sender.send(self.clone()).unwrap();
  }
}

static TASK_SENDER: OnceLock<RwLock<Option<std::sync::mpsc::Sender<Arc<Task>>>>> = OnceLock::new();

static EPOLL_REGISTER: LazyLock<mpsc::Sender<(i32, epoll::PollType, Waker)>> = LazyLock::new(epoll::epoll_task);

static SLEEP_REGISTER: LazyLock<std::sync::mpsc::Sender<(Instant, Box<Waker>)>> = LazyLock::new(|| {
  let (sender, receiver) = std::sync::mpsc::channel();

  std::thread::Builder::new()
    .name("sleep-task".to_string())
    .spawn(|| sleep::sleep_task(receiver))
    .expect("Failed to spawn sleep task");

  sender
});

fn check_fd_nonblocking(fd: i32) {
  let flags = unsafe { libc::fcntl(fd, libc::F_GETFL) };
  if flags & libc::O_NONBLOCK == 0 {
    error!("File descriptor is not non-blocking");
    std::process::exit(1);
  }
}

pub(super) fn read_fd(fd: i32, buf: &mut [u8]) -> impl Future<Output = DSSResult<usize>> + '_ {
  std::future::poll_fn(move |cx| {
    let _timer = noisytimer(format!("read_fd({}, {})", fd, buf.len()), 1);
    check_fd_nonblocking(fd);
    match nix::unistd::read(fd, buf) {
      Ok(n) => Poll::Ready(Ok(n)),
      Err(nix::errno::Errno::EAGAIN) => {
        EPOLL_REGISTER
          .send((fd, epoll::PollType::Read, cx.waker().clone()))
          .unwrap();
        Poll::Pending
      }
      Err(e) => Poll::Ready(Err(e.into())),
    }
  })
}

pub(super) fn write_fd(fd: i32, buf: &[u8]) -> impl Future<Output = DSSResult<usize>> + '_ {
  std::future::poll_fn(move |cx| {
    let _timer = noisytimer(format!("write_fd({}, {})", fd, buf.len()), 1);
    check_fd_nonblocking(fd);
    let rawfd = fd;
    let fd = unsafe { BorrowedFd::borrow_raw(fd) };
    match nix::unistd::write(fd, buf) {
      Ok(n) => Poll::Ready(Ok(n)),
      Err(nix::errno::Errno::EAGAIN) => {
        EPOLL_REGISTER
          .send((rawfd, epoll::PollType::Write, cx.waker().clone()))
          .unwrap();
        Poll::Pending
      }
      Err(e) => Poll::Ready(Err(e.into())),
    }
  })
}

pub(super) fn spawn<F, T>(future: F)
where
  F: Future<Output = T> + Send + 'static,
{
  let sender = TASK_SENDER.get().unwrap().read().unwrap().as_ref().unwrap().clone();

  let future = async {
    _ = future.await;
  };

  let task = Arc::new(Task {
    future: Mutex::new(Some(Box::pin(future))),
    sender: sender.clone(),
  });

  sender.send(task).unwrap();
}

pub(super) fn run_main<F, T>(future: F) -> T
where
  F: Future<Output = T> + Send + 'static,
  T: Send + 'static,
{
  let (task_sender, task_receiver) = std::sync::mpsc::channel();
  TASK_SENDER.set(RwLock::new(Some(task_sender))).unwrap();
  let t = std::thread::spawn(|| run_forever(task_receiver));

  let (result_sender, result_receiver) = std::sync::mpsc::channel();

  spawn(async move {
    let res = future.await;
    result_sender.send(res).unwrap();
  });

  let res = result_receiver.recv().unwrap();
  TASK_SENDER.get().unwrap().write().unwrap().take();
  t.join().unwrap();

  res
}

pub(super) fn fn_thread_future<T>(f: impl FnOnce() -> T + Send + Sync + 'static) -> impl Future<Output = T>
where
  T: Send + 'static,
{
  let result = Arc::new(Mutex::new(None));
  let waker = Arc::new(Mutex::new(None));

  let pollfn = {
    let result = result.clone();
    let waker = waker.clone();
    std::future::poll_fn(move |ctx| {
      let _timer = noisytimer("fn_thread_future", 1);
      waker.lock().unwrap().replace(ctx.waker().clone());

      match result.lock().unwrap().take() {
        Some(res) => Poll::Ready(res),
        None => Poll::Pending,
      }
    })
  };

  std::thread::spawn(move || {
    let res = f();
    result.lock().unwrap().replace(res);

    loop {
      match waker.lock().unwrap().take() {
        Some(waker) => {
          waker.wake();
          break;
        }
        None => std::thread::yield_now(),
      }
    }
  });

  pollfn
}

fn run_forever(task_receiver: std::sync::mpsc::Receiver<Arc<Task>>) {
  for task in task_receiver {
    let mut future_slot = task.future.lock().unwrap();

    if let Some(mut future) = future_slot.take() {
      let _timer = noisytimer("polling future", 1);
      let waker = std::task::Waker::from(task.clone());
      let context = &mut std::task::Context::from_waker(&waker);

      unsafe {
        // Polling something should not take more than 5 seconds, otherwise it's a bug
        // libc::alarm(5);
      }

      if future.as_mut().poll(context).is_pending() {
        // Not done, put it back
        *future_slot = Some(future);
      }

      unsafe {
        // libc::alarm(0);
      }
    }
  }
}

pub(super) fn sleep_seconds(seconds: impl Into<f64>) -> impl Future<Output = ()> {
  let seconds = seconds.into();
  let target = std::time::Instant::now() + std::time::Duration::from_secs_f64(seconds);

  std::future::poll_fn(move |cx| {
    let _timer = noisytimer(format!("sleep_seconds({})", seconds), 1);
    let now = std::time::Instant::now();

    if now >= target {
      Poll::Ready(())
    } else {
      // Register a sleep waker
      SLEEP_REGISTER.send((target, Box::new(cx.waker().clone()))).unwrap();
      Poll::Pending
    }
  })
}

pub(super) mod mpsc {
  use std::{
    collections::VecDeque,
    future::Future,
    sync::{Arc, Mutex},
    task::{Poll, Waker},
  };

  struct Inner<T> {
    q: VecDeque<T>,
    waker: Option<Waker>,
    sender_count: usize,
    receiver_there: bool,
  }

  pub struct Receiver<T> {
    inner: Arc<Mutex<Inner<T>>>,
  }

  pub struct Sender<T> {
    inner: Arc<Mutex<Inner<T>>>,
  }

  impl<T> Sender<T> {
    pub fn send(&self, value: T) -> Result<(), ()> {
      let _timer = super::noisytimer("mpsc::Sender::send", 1);
      let mut inner = self.inner.lock().unwrap();

      if inner.receiver_there {
        inner.q.push_back(value);
        if let Some(waker) = inner.waker.take() {
          waker.wake();
        }
        Ok(())
      } else {
        Err(())
      }
    }
  }

  impl<T> Drop for Sender<T> {
    fn drop(&mut self) {
      let mut inner = self.inner.lock().unwrap();

      match inner.sender_count {
        0 => panic!("Dropped below zero"),
        1 => {
          if let Some(waker) = inner.waker.take() {
            waker.wake();
          }
        }
        _ => {}
      }

      inner.sender_count -= 1;
    }
  }

  impl<T> Clone for Sender<T> {
    fn clone(&self) -> Self {
      let mut inner = self.inner.lock().unwrap();
      inner.sender_count += 1;

      Sender {
        inner: self.inner.clone(),
      }
    }
  }

  impl<T> Receiver<T> {
    pub fn recv(&self) -> impl Future<Output = Option<T>> + '_ {
      std::future::poll_fn(|ctx| {
        let _timer = super::noisytimer("mpsc::Receiver::recv", 1);
        let mut inner = self.inner.lock().unwrap();

        if let Some(value) = inner.q.pop_front() {
          Poll::Ready(Some(value))
        } else if inner.sender_count > 0 {
          inner.waker = Some(ctx.waker().clone());
          Poll::Pending
        } else {
          Poll::Ready(None)
        }
      })
    }
  }

  impl<T> Drop for Receiver<T> {
    fn drop(&mut self) {
      let mut inner = self.inner.lock().unwrap();
      inner.receiver_there = false;
    }
  }

  pub fn channel<T>() -> (Sender<T>, Receiver<T>) {
    let inner = Arc::new(Mutex::new(Inner {
      q: VecDeque::new(),
      waker: None,
      sender_count: 1,
      receiver_there: true,
    }));

    let sender = Sender { inner: inner.clone() };
    let receiver = Receiver { inner };

    (sender, receiver)
  }
}

pub(super) fn yield_now() -> impl Future<Output = ()> {
  struct X(bool);

  impl Future for X {
    type Output = ();

    fn poll(mut self: Pin<&mut Self>, cx: &mut std::task::Context) -> Poll<Self::Output> {
      if self.0 {
        Poll::Ready(())
      } else {
        self.0 = true;
        cx.waker().wake_by_ref();
        Poll::Pending
      }
    }
  }

  X(false)
}

pub(super) struct TimeoutFuture<F, T>
where
  F: Future<Output = T>,
{
  future: F,
  timeout_at: std::time::Instant,
}

pub(super) fn timeout_future<F, T>(future: F, timeout: std::time::Duration) -> TimeoutFuture<F, T>
where
  F: Future<Output = T> + Unpin,
{
  TimeoutFuture {
    future,
    timeout_at: std::time::Instant::now() + timeout,
  }
}

impl<F, T> Future for TimeoutFuture<F, T>
where
  F: Future<Output = T> + Unpin,
{
  type Output = DSSResult<T>;

  fn poll(self: std::pin::Pin<&mut Self>, cx: &mut std::task::Context) -> std::task::Poll<Self::Output> {
    let _timer = noisytimer("TimeoutFuture::poll", 1);
    let now = std::time::Instant::now();

    if now >= self.timeout_at {
      trace!("A future timed out");
      return std::task::Poll::Ready(Err("Timeout".into()));
    }

    let timeout_at = self.timeout_at;

    let this = self.get_mut();
    let future = Pin::new(&mut this.future);
    let poll_res = future.poll(cx);

    match poll_res {
      Poll::Ready(output) => Poll::Ready(Ok(output)),
      Poll::Pending => {
        let waker = cx.waker().clone();
        SLEEP_REGISTER.send((timeout_at, Box::new(waker))).unwrap();
        Poll::Pending
      }
    }
  }
}

pub(super) fn select2_noresult<F1: Future, F2: Future>(f1: F1, f2: F2) -> impl Future<Output = ()> {
  struct Select2Future<F1, F2> {
    f1: F1,
    f2: F2,
  }

  impl<F1: Future + Unpin, F2: Future + Unpin> Future for Select2Future<F1, F2> {
    type Output = ();

    fn poll(self: Pin<&mut Self>, cx: &mut std::task::Context) -> std::task::Poll<Self::Output> {
      let _timer = noisytimer("select2_noresult::Select2Future::poll", 1);
      let this = self.get_mut();

      let f1 = Pin::new(&mut this.f1);
      let f2 = Pin::new(&mut this.f2);

      let poll1 = f1.poll(cx);
      let poll2 = f2.poll(cx);

      if poll1.is_ready() || poll2.is_ready() {
        Poll::Ready(())
      } else {
        Poll::Pending
      }
    }
  }

  Select2Future {
    f1: Box::pin(f1),
    f2: Box::pin(f2),
  }
}

// Join

struct Join2Futures<F1, F2, T1, T2>
where
  F1: Future<Output = T1>,
  F2: Future<Output = T2>,
{
  future1: F1,
  future2: F2,
  future1_result: Option<T1>,
  future2_result: Option<T2>,
}

impl<F1, F2, T1, T2> Future for Join2Futures<F1, F2, T1, T2>
where
  F1: Future<Output = T1> + Unpin,
  F2: Future<Output = T2> + Unpin,
  T1: Unpin,
  T2: Unpin,
{
  type Output = (T1, T2);

  fn poll(self: Pin<&mut Self>, cx: &mut std::task::Context) -> std::task::Poll<(T1, T2)> {
    let this = self.get_mut();

    if this.future1_result.is_none() {
      match Pin::new(&mut this.future1).poll(cx) {
        std::task::Poll::Ready(val) => this.future1_result = Some(val),
        std::task::Poll::Pending => {}
      }
    }

    if this.future2_result.is_none() {
      match Pin::new(&mut this.future2).poll(cx) {
        std::task::Poll::Ready(val) => this.future2_result = Some(val),
        std::task::Poll::Pending => {}
      }
    }

    if this.future1_result.is_none() || this.future2_result.is_none() {
      return std::task::Poll::Pending;
    }

    std::task::Poll::Ready((this.future1_result.take().unwrap(), this.future2_result.take().unwrap()))
  }
}

pub(super) fn join2<F1, F2, T1, T2>(future1: F1, future2: F2) -> impl Future<Output = (T1, T2)>
where
  F1: Future<Output = T1>,
  F2: Future<Output = T2>,
  T1: Unpin,
  T2: Unpin,
{
  Join2Futures {
    future1: Box::pin(future1),
    future2: Box::pin(future2),
    future1_result: None,
    future2_result: None,
  }
}

mod epoll {
  use std::{
    collections::HashMap,
    os::fd::BorrowedFd,
    sync::{Arc, Mutex},
    task::Waker,
  };

  use nix::sys::epoll::{Epoll, EpollCreateFlags, EpollEvent, EpollFlags, EpollTimeout};

  #[derive(PartialEq, Eq, Hash, Debug)]
  pub enum PollType {
    Read,
    Write,
  }

  type WakerHashMap = HashMap<i32, Waker>;

  pub fn epoll_task() -> super::mpsc::Sender<(i32, PollType, Waker)> {
    let (sender, receiver) = super::mpsc::channel();
    let epoll = Arc::new(Epoll::new(EpollCreateFlags::empty()).unwrap());
    let waker_hashmap: Arc<Mutex<WakerHashMap>> = Arc::new(Mutex::new(HashMap::new()));

    // Registerer
    super::spawn(epoll_register_task(epoll.clone(), waker_hashmap.clone(), receiver));

    // Waker
    std::thread::spawn(move || epoll_waker_task(epoll.clone(), waker_hashmap.clone()));

    sender
  }

  fn epoll_waker_task(epoll: Arc<Epoll>, waker_hashmap: Arc<Mutex<WakerHashMap>>) {
    loop {
      let mut events = [EpollEvent::empty(); 128];
      let epoll = epoll.clone();

      let n = epoll.wait(&mut events, EpollTimeout::NONE).unwrap();

      let mut waker_hashmap = waker_hashmap.lock().unwrap();

      for event in events.iter().take(n) {
        let fd = event.data() as i32;
        if let Some(waker) = waker_hashmap.remove(&fd) {
          waker.wake();
        }
      }
    }
  }

  async fn epoll_register_task(
    epoll: Arc<Epoll>,
    waker_hashmap: Arc<Mutex<WakerHashMap>>,
    receiver: super::mpsc::Receiver<(i32, PollType, Waker)>,
  ) {
    while let Some((fd, polltype, waker)) = receiver.recv().await {
      let _timer = super::noisytimer("epoll_register_task", 1);
      let flags = match polltype {
        PollType::Read => EpollFlags::EPOLLIN | EpollFlags::EPOLLONESHOT,
        PollType::Write => EpollFlags::EPOLLOUT | EpollFlags::EPOLLONESHOT,
      };

      let _ = waker_hashmap.lock().unwrap().insert(fd, waker);

      let rawfd = fd;
      let fd = unsafe { BorrowedFd::borrow_raw(fd) };

      let mut ev = EpollEvent::new(flags, rawfd as u64);

      match epoll.modify(fd, &mut ev) {
        Ok(_) => {}
        // If the file descriptor is not found in the epoll instance, add it instead of modifying.
        Err(nix::errno::Errno::ENOENT) => epoll.add(fd, ev).unwrap(),
        Err(e) => {
          panic!("epoll modify failed: {:?}", e);
        }
      }
    }
  }
}

mod sleep {
  use std::{collections::BinaryHeap, task::Waker, time::Instant};

  struct InstantAndWaker(Instant, Box<Waker>);

  impl PartialEq for InstantAndWaker {
    fn eq(&self, other: &Self) -> bool {
      self.0 == other.0
    }
  }

  impl Eq for InstantAndWaker {}

  impl PartialOrd for InstantAndWaker {
    fn partial_cmp(&self, other: &Self) -> Option<std::cmp::Ordering> {
      Some(other.0.cmp(&self.0))
    }
  }

  impl Ord for InstantAndWaker {
    fn cmp(&self, other: &Self) -> std::cmp::Ordering {
      other.0.cmp(&self.0)
    }
  }

  pub(super) fn sleep_task(recv: std::sync::mpsc::Receiver<(Instant, Box<Waker>)>) {
    let mut sleep_queue: BinaryHeap<InstantAndWaker> = BinaryHeap::new();

    loop {
      while let Ok((instant, waker)) = recv.try_recv() {
        sleep_queue.push(InstantAndWaker(instant, waker));
      }

      let now = Instant::now();
      while let Some(InstantAndWaker(instant, _)) = sleep_queue.peek() {
        if instant <= &now {
          if let Some(InstantAndWaker(_, waker)) = sleep_queue.pop() {
            waker.wake_by_ref();
          }
        } else {
          break;
        }
      }

      if let Some(InstantAndWaker(instant, _)) = sleep_queue.peek() {
        let timeout = *instant - now;
        match recv.recv_timeout(timeout) {
          Ok((instant, waker)) => {
            sleep_queue.push(InstantAndWaker(instant, waker));
            continue;
          }
          Err(std::sync::mpsc::RecvTimeoutError::Timeout) => continue,
          Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
        }
      }

      if let Ok((instant, waker)) = recv.recv() {
        sleep_queue.push(InstantAndWaker(instant, waker));
      }
    }
  }
}

/// Creates a future that will panic if polled after it has completed.
///
/// Rust futures are supposed to be polled until completion, and not polled after they return `Poll::Ready`. When people
/// write futures, they assume that this contract is upheld. But bugs happen, so we can wrap a future in this function
/// to catch futures that are polled after completion, and panic.
pub(super) fn fused<T>(future: impl Future<Output = T> + Unpin) -> impl Future<Output = T> {
  struct Fused<F, T>
  where
    F: Future<Output = T>,
  {
    future: F,
    resolved: bool,
  }

  impl<F: Future<Output = T> + Unpin, T> Future for Fused<F, T> {
    type Output = T;

    fn poll(self: Pin<&mut Self>, cx: &mut std::task::Context) -> Poll<Self::Output> {
      let this = self.get_mut();

      if this.resolved {
        panic!("Future polled after completion");
      }

      match Pin::new(&mut this.future).poll(cx) {
        Poll::Ready(v) => {
          this.resolved = true;
          Poll::Ready(v)
        }
        Poll::Pending => Poll::Pending,
      }
    }
  }

  Fused {
    future,
    resolved: false,
  }
}
