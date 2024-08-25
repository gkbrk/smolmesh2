use std::{
  future::Future,
  num::NonZeroUsize,
  os::fd::BorrowedFd,
  pin::Pin,
  sync::{Arc, LazyLock, Mutex, RwLock},
  task::{Poll, Waker},
  time::Instant,
};

use crossbeam::atomic::AtomicCell;

pub(super) type DSSResult<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

struct Task {
  future: Mutex<Option<Pin<Box<dyn Future<Output = ()> + Send + 'static>>>>,
  sender: crossbeam::channel::Sender<Arc<Task>>,
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

struct Executor {
  task_receiver: crossbeam::channel::Receiver<Arc<Task>>,
  task_sender: RwLock<Option<crossbeam::channel::Sender<Arc<Task>>>>,
}

static EXECUTOR: LazyLock<Executor> = LazyLock::new(|| {
  let (task_sender, task_receiver) = crossbeam::channel::unbounded();
  Executor {
    task_receiver,
    task_sender: RwLock::new(Some(task_sender)),
  }
});

static EPOLL_REGISTER: LazyLock<mpsc::Sender<(i32, epoll::PollType, Waker)>> = LazyLock::new(epoll::epoll_task);

static SLEEP_REGISTER: LazyLock<std::sync::mpsc::Sender<(Instant, Box<Waker>)>> = LazyLock::new(|| {
  let (sender, receiver) = std::sync::mpsc::channel();

  std::thread::Builder::new()
    .name("sleep-task".to_string())
    .spawn(|| sleep::sleep_task(receiver))
    .expect("Failed to spawn sleep task");

  sender
});

pub(super) fn read_fd(fd: i32, buf: &mut [u8]) -> impl Future<Output = DSSResult<usize>> + '_ {
  std::future::poll_fn(move |cx| match nix::unistd::read(fd, buf) {
    Ok(n) => Poll::Ready(Ok(n)),
    Err(nix::errno::Errno::EAGAIN) => {
      EPOLL_REGISTER
        .send((fd, epoll::PollType::Read, cx.waker().clone()))
        .unwrap();
      Poll::Pending
    }
    Err(e) => Poll::Ready(Err(e.into())),
  })
}

pub(super) fn write_fd(fd: i32, buf: &[u8]) -> impl Future<Output = DSSResult<usize>> + '_ {
  std::future::poll_fn(move |cx| {
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
  let sender = EXECUTOR.task_sender.read().unwrap().clone().unwrap();

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
  let (result_sender, result_receiver) = std::sync::mpsc::channel();

  spawn(async move {
    let res = future.await;
    result_sender.send(res).unwrap();
  });

  let runners = {
    let mut runners = Vec::new();

    let parallelism = std::thread::available_parallelism()
      .unwrap_or(NonZeroUsize::new(8).unwrap())
      .get();

    for _ in 0..parallelism {
      let t = std::thread::spawn(run_forever);
      runners.push(t);
    }

    runners
  };

  let res = result_receiver.recv().unwrap();

  EXECUTOR.task_sender.write().unwrap().take();

  for r in runners {
    r.join().unwrap();
  }

  res
}

pub(super) fn fn_thread_future<T>(f: impl FnOnce() -> T + Send + Sync + 'static) -> impl Future<Output = T>
where
  T: Send + 'static,
{
  let result = Arc::new(AtomicCell::new(None));
  let waker = Arc::new(AtomicCell::new(None));

  let pollfn = {
    let result = result.clone();
    let waker = waker.clone();
    std::future::poll_fn(move |ctx| {
      waker.store(Some(ctx.waker().clone()));

      match result.take() {
        Some(res) => Poll::Ready(res),
        None => Poll::Pending,
      }
    })
  };

  std::thread::spawn(move || {
    let res = f();
    result.store(Some(res));

    loop {
      match waker.take() {
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

fn run_forever() {
  let task_receiver = EXECUTOR.task_receiver.clone();

  for task in task_receiver {
    let mut future_slot = task.future.lock().unwrap();

    if let Some(mut future) = future_slot.take() {
      let waker = std::task::Waker::from(task.clone());
      let context = &mut std::task::Context::from_waker(&waker);

      let start = std::time::Instant::now();

      if future.as_mut().poll(context).is_pending() {
        // Not done, put it back
        *future_slot = Some(future);
      }

      let elapsed = start.elapsed();

      if elapsed > std::time::Duration::from_millis(500) {
        panic!("Task took too long: {:?}", elapsed);
      }
    }
  }
}

pub(super) fn sleep_seconds(seconds: impl Into<f64>) -> impl Future<Output = ()> {
  let seconds = seconds.into();
  let target = std::time::Instant::now() + std::time::Duration::from_secs_f64(seconds);

  std::future::poll_fn(move |cx| {
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
    sync::{
      atomic::{AtomicBool, AtomicUsize, Ordering},
      Arc, Mutex,
    },
    task::{Poll, Waker},
  };

  pub struct Receiver<T> {
    q: Arc<Mutex<VecDeque<T>>>,
    waker: Arc<Mutex<Option<Waker>>>,
    sender_count: Arc<AtomicUsize>,
    receiver_there: Arc<AtomicBool>,
  }

  pub struct Sender<T> {
    q: Arc<Mutex<VecDeque<T>>>,
    waker: Arc<Mutex<Option<Waker>>>,
    sender_count: Arc<AtomicUsize>,
    receiver_there: Arc<AtomicBool>,
  }

  impl<T> Sender<T> {
    pub fn send(&self, value: T) -> Result<(), ()> {
      if self.receiver_there.load(Ordering::SeqCst) {
        let mut q = self.q.lock().unwrap();
        q.push_back(value);

        if let Some(waker) = self.waker.lock().unwrap().take() {
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
      let count = self.sender_count.fetch_sub(1, Ordering::SeqCst);
      match count {
        0 => panic!("Dropped below zero"),
        1 => {
          if let Some(waker) = self.waker.lock().unwrap().take() {
            waker.wake();
          }
        }
        _ => {}
      }
    }
  }

  impl<T> Clone for Sender<T> {
    fn clone(&self) -> Self {
      self.sender_count.fetch_add(1, Ordering::SeqCst);

      Sender {
        q: self.q.clone(),
        waker: self.waker.clone(),
        sender_count: self.sender_count.clone(),
        receiver_there: self.receiver_there.clone(),
      }
    }
  }

  impl<T> Receiver<T> {
    pub fn recv(&self) -> impl Future<Output = Option<T>> + '_ {
      std::future::poll_fn(|ctx| {
        let mut q = self.q.lock().unwrap();
        if let Some(value) = q.pop_front() {
          Poll::Ready(Some(value))
        } else if self.sender_count.load(Ordering::SeqCst) > 0 {
          let waker = ctx.waker().clone();
          *self.waker.lock().unwrap() = Some(waker);
          Poll::Pending
        } else {
          Poll::Ready(None)
        }
      })
    }
  }

  impl<T> Drop for Receiver<T> {
    fn drop(&mut self) {
      self.receiver_there.store(false, Ordering::SeqCst);
    }
  }

  pub fn channel<T>() -> (Sender<T>, Receiver<T>) {
    let q = Arc::new(Mutex::new(VecDeque::new()));
    let waker = Arc::new(Mutex::new(None));
    let sender_count = Arc::new(AtomicUsize::new(1));
    let receiver_there = Arc::new(AtomicBool::new(true));

    let sender = Sender {
      q: q.clone(),
      waker: waker.clone(),
      sender_count: sender_count.clone(),
      receiver_there: receiver_there.clone(),
    };

    let receiver = Receiver {
      q,
      waker,
      sender_count,
      receiver_there,
    };

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
    let now = std::time::Instant::now();

    if now >= self.timeout_at {
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

  type WakerHashMap = HashMap<(i32, PollType), Waker>;

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
        let event = event.events();

        if event.contains(EpollFlags::EPOLLIN) {
          if let Some(waker) = waker_hashmap.remove(&(fd, PollType::Read)) {
            waker.wake();
          }
        }

        if event.contains(EpollFlags::EPOLLOUT) {
          if let Some(waker) = waker_hashmap.remove(&(fd, PollType::Write)) {
            waker.wake();
          }
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
      let flags = match polltype {
        PollType::Read => nix::sys::epoll::EpollFlags::EPOLLIN | nix::sys::epoll::EpollFlags::EPOLLONESHOT,
        PollType::Write => nix::sys::epoll::EpollFlags::EPOLLOUT | nix::sys::epoll::EpollFlags::EPOLLONESHOT,
      };

      let _ = waker_hashmap.lock().unwrap().insert((fd, polltype), waker);

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
