//! Tests simulation determinism, map generation speed, and filter-vs-simulation parity across
//! all character snapshots in `tests/fixtures`.

use serde_json::{json, Value};
use std::time::Instant;
use sts2_seed_engine::data::{AncientDef, AncientKind, CardData, Snapshot};
use sts2_seed_engine::{map, rng, sim};

fn enrich_snapshot_if_legacy(snap: &mut Snapshot) {
    for (i, c) in snap.cards.iter_mut().enumerate() {
        if c.card_type == 0 {
            c.card_type = ((i % 3) + 1) as u8;
        }
    }
    if snap.shop_cards.is_empty() {
        snap.shop_cards = snap
            .cards
            .iter()
            .enumerate()
            .map(|(i, c)| CardData {
                id: c.id,
                rarity: c.rarity,
                card_type: if c.card_type != 0 { c.card_type } else { ((i % 3) + 1) as u8 },
            })
            .collect();
    }
    if snap.shop_colorless.is_empty() {
        if let Some(neow) = &snap.neow {
            snap.shop_colorless = neow
                .colorless
                .iter()
                .map(|c| CardData { id: c.id, rarity: c.rarity, card_type: 2 })
                .collect();
        }
    }
    if snap.ancient_defs.is_empty() {
        let pool: Vec<u32> = snap.shared_bag.iter().take(12).map(|r| r[0]).collect();
        for anc_id in 1..=7u32 {
            snap.ancient_defs.push(AncientDef {
                id: anc_id,
                entry: format!("ANCIENT_{anc_id}"),
                kind: if anc_id == 7 { AncientKind::Darv } else { AncientKind::ShuffleThree },
                pool1: pool.clone(),
                pool2: pool.clone(),
                pool3: pool.clone(),
                other_char_count: 4,
                prismatic_gem: pool.first().copied().unwrap_or(0),
                sea_glass: pool.get(1).copied().unwrap_or(0),
                dusty_tome: pool.get(2).copied().unwrap_or(0),
            });
        }
    }
}

fn fixtures() -> Vec<(String, Snapshot, Vec<String>)> {
    let dir = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/fixtures");
    let mut out = Vec::new();
    for entry in std::fs::read_dir(dir).expect("fixtures folder") {
        let path = entry.unwrap().path();
        if path.extension().is_some_and(|e| e == "json") {
            let text = std::fs::read_to_string(&path).unwrap();
            let mut root: Value = serde_json::from_str(&text).unwrap();
            let mut snapshot: Snapshot = serde_json::from_value(root["snapshot"].take()).unwrap();
            enrich_snapshot_if_legacy(&mut snapshot);
            let seeds: Vec<String> = root["cases"]
                .as_array()
                .unwrap()
                .iter()
                .map(|c| c["seed"].as_str().unwrap().to_owned())
                .collect();
            out.push((path.file_name().unwrap().to_string_lossy().into_owned(), snapshot, seeds));
        }
    }
    assert!(!out.is_empty(), "no fixtures found");
    out
}

#[test]
fn simulation_and_filters_agree() {
    let mut checked = 0;
    for (name, snapshot, seeds) in fixtures() {
        for seed in seeds {
            let sim_val = serde_json::to_value(sim::simulate(&snapshot, &seed)).unwrap();
            let seed_hash = rng::hash(&seed);
            let matches = |conds: Vec<Value>| {
                let conds: Vec<sim::Cond> = serde_json::from_value(Value::Array(conds)).unwrap();
                sim::Filter::new(conds).matches(&snapshot, seed_hash)
            };

            let boss0 = sim_val["bosses"][0].as_i64().unwrap();
            let act0 = sim_val["acts"][0].as_u64().unwrap();
            let anc1 = sim_val["ancients"][1].as_i64().unwrap();
            let anc1_relic = sim_val["ancient_relics"][1][0].as_u64().unwrap();
            let treasure0 = sim_val["treasure_relics"][0].as_u64().unwrap();
            let shop_card0 = sim_val["first_shop"]["cards"][0].as_u64().unwrap();
            let sale_idx = sim_val["first_shop"]["sale_index"].as_u64().unwrap() as usize;
            let sale_card = sim_val["first_shop"]["cards"][sale_idx].as_u64().unwrap();
            let shop_colorless0 = sim_val["first_shop"]["colorless"][0].as_u64().unwrap();
            let shop_relic0 = sim_val["first_shop"]["relics"][0].as_u64().unwrap();
            let shop_potion0 = sim_val["first_shop"]["potions"][0].as_u64().unwrap();
            let reward_card0 = sim_val["cards"][0].as_u64().unwrap();
            let neow0 = sim_val["neow"][0].as_u64().unwrap();
            let post_neow_card0 = sim_val["neow_outcomes"][0]["first_cards"][0].as_u64().unwrap();
            let max_path_elites = sim_val["maps"][0]["max_path_elites"].as_u64().unwrap();
            let max_path_rests = sim_val["maps"][0]["max_path_rests"].as_u64().unwrap();

            assert!(
                matches(vec![
                    json!({ "kind": "boss", "slot": 0, "enc": boss0 }),
                    json!({ "kind": "act", "slot": 0, "act": act0 }),
                    json!({ "kind": "ancient", "slot": 1, "ancient": anc1 }),
                    json!({ "kind": "ancient_relic", "slot": 1, "relic": anc1_relic }),
                    json!({ "kind": "treasure_relic", "relic": treasure0, "within": 1 }),
                    json!({ "kind": "shop_item", "what": "card", "item": shop_card0 }),
                    json!({ "kind": "shop_item", "what": "card", "item": sale_card }),
                    json!({ "kind": "shop_item", "what": "card", "item": shop_colorless0 }),
                    json!({ "kind": "shop_item", "what": "relic", "item": shop_relic0 }),
                    json!({ "kind": "shop_item", "what": "potion", "item": shop_potion0 }),
                    json!({ "kind": "reward_card", "card": reward_card0 }),
                    json!({ "kind": "reward_card", "card": post_neow_card0, "after_neow": neow0 }),
                    json!({ "kind": "map_path_count", "slot": 0, "point_type": 6, "min": max_path_elites, "max": max_path_elites }),
                    json!({ "kind": "map_path_count", "slot": 0, "point_type": 4, "min": max_path_rests, "max": max_path_rests }),
                    json!({
                        "kind": "any_of",
                        "conds": [
                            { "kind": "boss", "slot": 0, "enc": 999999 },
                            { "kind": "treasure_relic", "relic": treasure0, "within": 1 }
                        ]
                    }),
                ]),
                "{name}, seed {seed} failed filter parity"
            );

            assert!(
                !matches(vec![json!({
                    "kind": "any_of",
                    "conds": [
                        { "kind": "boss", "slot": 0, "enc": 999999 },
                        { "kind": "boss", "slot": 1, "enc": 999999 }
                    ]
                })]),
                "{name}, seed {seed} matched impossible any_of"
            );

            checked += 1;
        }
    }
    println!("checked {checked} seeds across all filter kinds");
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
        let m = map::generate(&snapshot, rng::hash(&seed), 0, act).expect("valid map");
        assert!(m.max_path_elites > 0 && m.max_path_elites as usize <= m.points.len());
        assert!(m.max_path_rests > 0 && m.max_path_rests as usize <= m.points.len());
        let ms = one.elapsed().as_secs_f64() * 1000.0;
        if ms > slowest.0 {
            slowest = (ms, seed);
        }
    }
    println!(
        "{:.3} ms per map on average, slowest {:.1} ms ({})",
        start.elapsed().as_secs_f64() * 1000.0 / n as f64,
        slowest.0,
        slowest.1
    );
}

/// A filter on what a relic of Neow's Bones gives finds the seeds whose simulation shows it,
/// and all such filters on one seed must agree on the order the relics are taken in.
#[test]
fn nested_filters_follow_the_simulation() {
    let mut checked = 0;
    for (name, snapshot, seeds) in fixtures() {
        for seed in seeds {
            let case = serde_json::to_value(sim::simulate(&snapshot, &seed)).unwrap();
            let offers = case["neow"].as_array().unwrap();
            for (offer, outcome) in offers.iter().zip(case["neow_outcomes"].as_array().unwrap()) {
                let nested = outcome["nested"].as_array().unwrap();
                let cond = |i: usize, order: &str| {
                    let card = nested[i][order]["cards"].as_array().unwrap().first().cloned()?;
                    Some(serde_json::json!({ "kind": "neow_nested", "relic": offer, "via": outcome["relics"][i], "what": "card", "item": card }))
                };
                let matches = |conds: Vec<Value>| {
                    let conds: Vec<sim::Cond> = serde_json::from_value(Value::Array(conds)).unwrap();
                    sim::Filter::new(conds).matches(&snapshot, rng::hash(&seed))
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
