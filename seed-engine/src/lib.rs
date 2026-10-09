//! Seed search engine for the Slay the Spire 2 mod. Algorithms only: the mod exports the game
//! data as a JSON snapshot (see `data.rs`) and talks to this library through the C ABI below.
//!
//! Functions that return text take a caller-owned buffer and return the full length of the
//! text, so the caller retries with a bigger buffer when the result is larger than `cap`.
//! A negative return value means an error; `sts2_last_error` has the message.

pub mod data;
pub mod gen;
pub mod map;
pub mod rng;
pub mod search;
pub mod sim;

use data::Snapshot;
use search::Search;
use sim::{Cond, Filter};
use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::Arc;

/// Bumped whenever the snapshot, filter or result formats change.
pub const ABI_VERSION: u32 = 2;

pub struct Engine {
    snap: Arc<Snapshot>,
}

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

fn set_error(message: String) {
    LAST_ERROR.with(|e| *e.borrow_mut() = message);
}

fn guard<T>(fallback: T, f: impl FnOnce() -> Result<T, String>) -> T {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(v)) => v,
        Ok(Err(e)) => {
            set_error(e);
            fallback
        }
        Err(_) => {
            set_error("the seed engine panicked".to_string());
            fallback
        }
    }
}

unsafe fn text<'a>(ptr: *const u8, len: usize) -> Result<&'a str, String> {
    if ptr.is_null() {
        return Err("null text".to_string());
    }
    std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).map_err(|e| e.to_string())
}

unsafe fn write_out(s: &str, out: *mut u8, cap: usize) -> i64 {
    if !out.is_null() && s.len() <= cap {
        std::ptr::copy_nonoverlapping(s.as_ptr(), out, s.len());
    }
    s.len() as i64
}

#[no_mangle]
pub extern "C" fn sts2_abi_version() -> u32 {
    ABI_VERSION
}

/// # Safety
/// `out` must be null or point to `cap` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn sts2_last_error(out: *mut u8, cap: usize) -> i64 {
    LAST_ERROR.with(|e| write_out(&e.borrow(), out, cap))
}

/// # Safety
/// `json` must point to `len` bytes of UTF-8.
#[no_mangle]
pub unsafe extern "C" fn sts2_engine_create(json: *const u8, len: usize) -> *mut Engine {
    guard(std::ptr::null_mut(), || {
        let snap: Snapshot = serde_json::from_str(text(json, len)?).map_err(|e| format!("bad snapshot: {e}"))?;
        Ok(Box::into_raw(Box::new(Engine { snap: Arc::new(snap) })))
    })
}

/// # Safety
/// `engine` must come from `sts2_engine_create` and not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn sts2_engine_free(engine: *mut Engine) {
    if !engine.is_null() {
        drop(Box::from_raw(engine));
    }
}

/// Writes the full simulation of one seed as JSON.
///
/// # Safety
/// `engine` must be live, `seed` must point to `seed_len` bytes of UTF-8, `out` as above.
#[no_mangle]
pub unsafe extern "C" fn sts2_engine_simulate(engine: *const Engine, seed: *const u8, seed_len: usize, out: *mut u8, cap: usize) -> i64 {
    guard(-1, || {
        let engine = engine.as_ref().ok_or("null engine")?;
        let result = sim::simulate(&engine.snap, text(seed, seed_len)?);
        let json = serde_json::to_string(&result).map_err(|e| e.to_string())?;
        Ok(write_out(&json, out, cap))
    })
}

/// Starts a search for random seeds matching a JSON array of conditions.
///
/// # Safety
/// `engine` must be live and `filter` must point to `filter_len` bytes of UTF-8.
#[no_mangle]
pub unsafe extern "C" fn sts2_search_start(engine: *const Engine, filter: *const u8, filter_len: usize, threads: u32, max_results: u32) -> *mut Search {
    guard(std::ptr::null_mut(), || {
        let engine = engine.as_ref().ok_or("null engine")?;
        let conds: Vec<Cond> = serde_json::from_str(text(filter, filter_len)?).map_err(|e| format!("bad filter: {e}"))?;
        let filter = Filter::new(conds);
        if filter.is_empty() {
            return Err("empty filter".to_string());
        }
        let search = Search::start(engine.snap.clone(), filter, threads as usize, max_results as usize);
        Ok(Box::into_raw(Box::new(search)))
    })
}

/// Writes the progress of a search as JSON.
///
/// # Safety
/// `search` must be live, `out` as above.
#[no_mangle]
pub unsafe extern "C" fn sts2_search_poll(search: *const Search, out: *mut u8, cap: usize) -> i64 {
    guard(-1, || {
        let search = search.as_ref().ok_or("null search")?;
        let json = serde_json::to_string(&search.poll()).map_err(|e| e.to_string())?;
        Ok(write_out(&json, out, cap))
    })
}

/// Stops the workers and frees the search.
///
/// # Safety
/// `search` must come from `sts2_search_start` and not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn sts2_search_free(search: *mut Search) {
    if !search.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| drop(Box::from_raw(search))));
    }
}
