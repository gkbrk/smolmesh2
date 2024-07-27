use std::{
  future::Future,
  num::NonZeroUsize,
  pin::Pin,
  sync::{Arc, LazyLock, Mutex, OnceLock, RwLock},
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
