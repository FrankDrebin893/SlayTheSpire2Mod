//! Multi-threaded random seed search.

use crate::data::Snapshot;
use crate::rng::{hash, Rng};
use crate::sim::Filter;
use serde::Serialize;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

/// SeedHelper's alphabet: no O or I, which the game folds into 0 and 1.
const ALPHABET: &[u8] = b"0123456789ABCDEFGHJKLMNPQRSTUVWXYZ";
const SEED_LENGTH: usize = 12;
const BATCH: u64 = 64;

struct Shared {
    stop: AtomicBool,
    tried: AtomicU64,
    running: AtomicUsize,
    results: Mutex<Vec<String>>,
    max_results: usize,
}

pub struct Search {
    shared: Arc<Shared>,
    threads: Vec<JoinHandle<()>>,
    thread_count: usize,
    started: Instant,
    /// Frozen once every worker has finished, so the rate stops decaying.
    finished_ms: Mutex<Option<u64>>,
}

#[derive(Serialize)]
pub struct Progress {
    pub tried: u64,
    pub elapsed_ms: u64,
    pub running: bool,
    pub threads: usize,
    pub results: Vec<String>,
}

fn random_seed(rng: &mut Rng, buf: &mut [u8; SEED_LENGTH]) {
    for b in buf.iter_mut() {
        *b = ALPHABET[rng.next_int(ALPHABET.len() as i32) as usize];
    }
}

fn worker(snap: Arc<Snapshot>, filter: Arc<Filter>, shared: Arc<Shared>, entropy: u64) {
    let mut rng = Rng::new(entropy);
    let mut buf = [0u8; SEED_LENGTH];
    'outer: while !shared.stop.load(Ordering::Relaxed) {
        for _ in 0..BATCH {
            random_seed(&mut rng, &mut buf);
            let text = std::str::from_utf8(&buf).unwrap_or_default();
            if filter.matches(&snap, hash(text)) {
                let mut results = shared.results.lock().unwrap_or_else(|e| e.into_inner());
                if results.len() < shared.max_results && !results.iter().any(|r| r == text) {
                    results.push(text.to_string());
                }
                if results.len() >= shared.max_results {
                    shared.stop.store(true, Ordering::Relaxed);
                    break 'outer;
                }
            }
        }
        shared.tried.fetch_add(BATCH, Ordering::Relaxed);
    }
    shared.running.fetch_sub(1, Ordering::Relaxed);
}

impl Search {
    pub fn start(snap: Arc<Snapshot>, filter: Filter, threads: usize, max_results: usize) -> Search {
        let threads = threads.max(1);
        let shared = Arc::new(Shared {
            stop: AtomicBool::new(false),
            tried: AtomicU64::new(0),
            running: AtomicUsize::new(threads),
            results: Mutex::new(Vec::new()),
            max_results: max_results.max(1),
        });
        let filter = Arc::new(filter);
        let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_nanos() as u64);
        let handles = (0..threads)
            .map(|i| {
                let (snap, filter, shared_for_thread) = (snap.clone(), filter.clone(), shared.clone());
                let entropy = nanos ^ (i as u64 + 1).wrapping_mul(0x9E37_79B9_7F4A_7C15);
                let on_panic = shared.clone();
                std::thread::spawn(move || {
                    // A panic in generation must not leave the search looking alive forever.
                    let run = std::panic::AssertUnwindSafe(|| worker(snap, filter, shared_for_thread, entropy));
                    if std::panic::catch_unwind(run).is_err() {
                        on_panic.running.fetch_sub(1, Ordering::Relaxed);
                    }
                })
            })
            .collect();
        Search { shared, threads: handles, thread_count: threads, started: Instant::now(), finished_ms: Mutex::new(None) }
    }

    pub fn poll(&self) -> Progress {
        let running = self.shared.running.load(Ordering::Relaxed) > 0;
        let now = self.started.elapsed().as_millis() as u64;
        let mut finished = self.finished_ms.lock().unwrap_or_else(|e| e.into_inner());
        if !running && finished.is_none() {
            *finished = Some(now);
        }
        Progress {
            tried: self.shared.tried.load(Ordering::Relaxed),
            elapsed_ms: finished.unwrap_or(now),
            running,
            threads: self.thread_count,
            results: self.shared.results.lock().unwrap_or_else(|e| e.into_inner()).clone(),
        }
    }

    pub fn stop(&mut self) {
        self.shared.stop.store(true, Ordering::Relaxed);
        for t in self.threads.drain(..) {
            let _ = t.join();
        }
    }
}

impl Drop for Search {
    fn drop(&mut self) {
        self.stop();
    }
}
