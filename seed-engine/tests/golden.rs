//! Replays fixtures dumped from the running game (see DevSelfCheck.cs in the mod): each file
//! holds a data snapshot and the simulations the game itself produced for a set of seeds.

use serde_json::Value;
use std::time::Instant;
use sts2_seed_engine::data::Snapshot;
use sts2_seed_engine::{map, rng, sim};

fn fixtures() -> Vec<(String, Snapshot, Vec<Value>)> {
    let dir = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures");
    let mut out = Vec::new();
    for entry in std::fs::read_dir(dir).expect("fixtures folder") {
        let path = entry.unwrap().path();
        if path.extension().is_some_and(|e| e == "json") {
            let text = std::fs::read_to_string(&path).unwrap();
            let mut root: Value = serde_json::from_str(&text).unwrap();
            let snapshot: Snapshot = serde_json::from_value(root["snapshot"].take()).unwrap();
            let cases = root["cases"].as_array().unwrap().clone();
            out.push((path.file_name().unwrap().to_string_lossy().into_owned(), snapshot, cases));
        }
    }
    assert!(!out.is_empty(), "no fixtures found");
    out
}

#[test]
fn matches_the_game() {
    let mut checked = 0;
    for (name, snapshot, cases) in fixtures() {
        for case in cases {
            let seed = case["seed"].as_str().unwrap();
            let ours = serde_json::to_value(sim::simulate(&snapshot, seed)).unwrap();
            assert_eq!(ours, case, "{name}, seed {seed}");
            checked += 1;
        }
    }
    println!("checked {checked} seeds");
}

#[test]
fn map_generation_speed() {
    let (_, snapshot, _) = fixtures().remove(0);
    let act = &snapshot.acts[0];
    let start = Instant::now();
    let mut slowest = (0.0f64, String::new());
    let n = 300;
    for i in 0..n {
        let seed = format!("SPEED{i}");
        let one = Instant::now();
        let _ = map::generate(&snapshot, rng::hash(&seed), 0, act);
        let ms = one.elapsed().as_secs_f64() * 1000.0;
        if ms > slowest.0 {
            slowest = (ms, seed);
        }
    }
    println!("{:.3} ms per map on average, slowest {:.1} ms ({})", start.elapsed().as_secs_f64() * 1000.0 / n as f64, slowest.0, slowest.1);
}
