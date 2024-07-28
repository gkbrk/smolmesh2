use std::{
  future::Future,
  num::NonZeroUsize,
  pin::Pin,
  sync::{Arc, LazyLock, Mutex, RwLock},
  task::{Poll, Waker},
};

use crossbeam::atomic::AtomicCell;

pub(super) type DSSResult<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

pub(super) struct Task {
  pub(super) future: Mutex<Option<Pin<Box<dyn Future<Output = ()> + Send + 'static>>>>,
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

pub(super) struct Executor {
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

pub(super) fn spawn<F, T>(future: F)
where
  F: Future<Output = T> + Send + 'static,
{
  let sender = EXECUTOR.task_sender.read().unwrap().clone().unwrap();

  let future = async {
    _ = future.await;
    ()
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

  // run_forever();

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

      if future.as_mut().poll(context).is_pending() {
        // Not done, put it back
        *future_slot = Some(future);
      }
    }
  }
}

pub(super) fn sleep_seconds(seconds: impl Into<f64>) -> impl Future<Output = ()> {
  let seconds = seconds.into();

  // TODO: Don't spawn 1 thread for each sleep
  fn_thread_future(move || {
    std::thread::sleep(std::time::Duration::from_secs_f64(seconds));
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

fn wake_up_at(waker: Waker, instant: std::time::Instant) {
  std::thread::spawn(move || {
    let now = std::time::Instant::now();

    if instant > now {
      std::thread::sleep(instant - now);
    }

    waker.wake();
  });
}

struct YieldFuture(bool);

impl Future for YieldFuture {
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

pub(super) fn yield_now() -> impl Future<Output = ()> {
  YieldFuture(false)
}

impl<F, T> Future for TimeoutFuture<F, T>
where
  F: Future<Output = T> + Unpin,
{
  type Output = std::result::Result<T, ()>;

  fn poll(self: std::pin::Pin<&mut Self>, cx: &mut std::task::Context) -> std::task::Poll<Self::Output> {
    let now = std::time::Instant::now();

    if now >= self.timeout_at {
      return std::task::Poll::Ready(Err(()));
    }

    let timeout_at = self.timeout_at;

    let this = self.get_mut();
    let future = Pin::new(&mut this.future);
    let poll_res = future.poll(cx);

    match poll_res {
      Poll::Ready(output) => Poll::Ready(Ok(output)),
      Poll::Pending => {
        let waker = cx.waker().clone();
        wake_up_at(waker, timeout_at);
        Poll::Pending
      }
    }
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
