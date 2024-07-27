use std::{
  future::Future,
  num::NonZeroUsize,
  pin::Pin,
  sync::{Arc, LazyLock, Mutex, RwLock},
  task::Poll,
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

pub(super) fn spawn<F>(future: F)
where
  F: Future<Output = ()> + Send + 'static,
{
  let sender = EXECUTOR.task_sender.read().unwrap().clone().unwrap();

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

      if future.as_mut().poll(context).is_pending() {
        // Not done, put it back
        *future_slot = Some(future);
      }
    }
  }
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
