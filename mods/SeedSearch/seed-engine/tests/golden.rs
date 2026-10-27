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

/// A filter on what a relic of Neow's Bones gives finds the seeds whose simulation shows it,
/// and all such filters on one seed must agree on the order the relics are taken in.
#[test]
fn nested_filters_follow_the_simulation() {
    let mut checked = 0;
    for (name, snapshot, cases) in fixtures() {
        for case in cases {
            let seed = case["seed"].as_str().unwrap();
            let offers = case["neow"].as_array().unwrap();
            for (offer, outcome) in offers.iter().zip(case["neow_outcomes"].as_array().unwrap()) {
                let nested = outcome["nested"].as_array().unwrap();
                let cond = |i: usize, order: &str| {
                    let card = nested[i][order]["cards"].as_array().unwrap().first().cloned()?;
                    Some(serde_json::json!({ "kind": "neow_nested", "relic": offer, "via": outcome["relics"][i], "what": "card", "item": card }))
                };
                let matches = |conds: Vec<Value>| {
                    let conds: Vec<sim::Cond> = serde_json::from_value(Value::Array(conds)).unwrap();
                    sim::Filter::new(conds).matches(&snapshot, rng::hash(seed))
                };
                for i in 0..nested.len() {
                    for order in ["first", "second"] {
                        if let Some(c) = cond(i, order) {
                            assert!(matches(vec![c]), "{name}, seed {seed}");
                            checked += 1;
                        }
                    }
                }
                // Both taken first cannot happen, unless they would give those cards second too.
                if nested.len() != 2 {
                    continue;
                }
                if let (Some(a), Some(b)) = (cond(0, "first"), cond(1, "first")) {
                    let has = |i: usize, c: &Value| nested[i]["second"]["cards"].as_array().unwrap().contains(&c["item"]);
                    assert_eq!(matches(vec![a.clone(), b.clone()]), has(0, &a) || has(1, &b), "{name}, seed {seed}");
                }
            }
        }
    }
    assert!(checked > 0, "no fixture has Neow's Bones");
    println!("checked {checked} nested filters");
}
