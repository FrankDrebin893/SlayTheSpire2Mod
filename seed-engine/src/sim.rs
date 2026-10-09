//! Full simulation of one seed (for previews and the parity check) and the filter used by the
//! search, which only computes the stages its conditions need, cheapest first.

use crate::data::Snapshot;
use crate::gen::{self, RelicDeque, UpFront};
use crate::map::{self, MapOut};
use crate::rng::hash;
use serde::{Deserialize, Serialize};

#[derive(Serialize)]
pub struct DequeOut {
    pub rarity: u32,
    pub relics: Vec<u32>,
}

#[derive(Serialize)]
pub struct MapJson {
    /// [col, row, type]
    pub points: Vec<[i32; 3]>,
    /// [col, row, col, row]
    pub edges: Vec<[i32; 4]>,
}

#[derive(Serialize)]
pub struct SimOut {
    pub seed: String,
    pub acts: Vec<u32>,
    pub bosses: Vec<i32>,
    pub second_boss: i32,
    pub ancients: Vec<i32>,
    pub events: Vec<Vec<u32>>,
    pub normals: Vec<Vec<u32>>,
    pub elites: Vec<Vec<u32>>,
    pub shared_relics: Vec<DequeOut>,
    pub player_relics: Vec<DequeOut>,
    pub neow: Vec<u32>,
    pub gold: i32,
    pub potion: i32,
    pub cards: Vec<u32>,
    pub maps: Vec<Option<MapJson>>,
}

fn deques(d: Vec<RelicDeque>) -> Vec<DequeOut> {
    d.into_iter().map(|d| DequeOut { rarity: d.rarity, relics: d.relics }).collect()
}

fn map_json(m: MapOut) -> MapJson {
    MapJson {
        points: m.points.iter().map(|&(c, r, t)| [c, r, t as i32]).collect(),
        edges: m.edges.iter().map(|&(a, b, c, d)| [a, b, c, d]).collect(),
    }
}

/// The ancient of act 1 is Neow whenever Neow is unlocked; otherwise there are no offers.
fn neow_for(snap: &Snapshot, seed: u64, act1_ancient: i32) -> Vec<u32> {
    match &snap.neow {
        Some(n) if n.ancient as i32 == act1_ancient => gen::neow_offers(snap, seed),
        _ => Vec::new(),
    }
}

pub fn simulate(snap: &Snapshot, seed_text: &str) -> SimOut {
    let seed = hash(seed_text);
    let acts = gen::roll_acts(snap, seed);
    let up = gen::up_front(snap, seed, &acts);
    let first_encounter = up.normals.first().and_then(|n| n.first()).copied();
    let reward = gen::first_reward(snap, seed, first_encounter);
    let neow = neow_for(snap, seed, up.ancients.first().copied().unwrap_or(gen::NONE));
    let maps = acts
        .iter()
        .enumerate()
        .map(|(slot, &act)| map::generate(snap, seed, slot, &snap.acts[act as usize]).map(map_json))
        .collect();

    SimOut {
        seed: seed_text.to_string(),
        acts,
        bosses: up.bosses,
        second_boss: up.second_boss,
        ancients: up.ancients,
        events: up.events,
        normals: up.normals,
        elites: up.elites,
        shared_relics: deques(up.shared_relics),
        player_relics: deques(up.player_relics),
        neow,
        gold: reward.gold,
        potion: reward.potion,
        cards: reward.cards,
        maps,
    }
}

/// One search condition; a seed must satisfy all of them.
#[derive(Deserialize, Clone)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Cond {
    /// Any of the three Neow options.
    NeowOffer { relic: u32 },
    /// The cursed (third) Neow option.
    NeowCurse { relic: u32 },
    Act { slot: usize, act: u32 },
    Boss { slot: usize, enc: i32 },
    SecondBoss { enc: i32 },
    Ancient { slot: usize, ancient: i32 },
    /// The event is among the first `within` events queued for the act.
    Event { slot: usize, event: u32, within: usize },
    Elite { slot: usize, enc: u32, within: usize },
    Normal { slot: usize, enc: u32, within: usize },
    /// The relic is among the first `within` of its rarity in the shared or the player bag.
    Relic { shared: bool, relic: u32, within: usize },
    MapCount { slot: usize, point_type: u8, min: i32, max: i32 },
    RewardCard { card: u32 },
    RewardPotion { present: bool },
}

pub struct Filter {
    neow: Vec<Cond>,
    acts: Vec<Cond>,
    reward: Vec<Cond>,
    up_front: Vec<Cond>,
    maps: Vec<Cond>,
}

fn within(list: Option<&Vec<u32>>, item: u32, n: usize) -> bool {
    list.is_some_and(|l| l.iter().take(n).any(|&x| x == item))
}

impl Filter {
    pub fn new(conds: Vec<Cond>) -> Self {
        let mut f = Filter { neow: vec![], acts: vec![], reward: vec![], up_front: vec![], maps: vec![] };
        for c in conds {
            match c {
                Cond::NeowOffer { .. } | Cond::NeowCurse { .. } => f.neow.push(c),
                Cond::Act { .. } => f.acts.push(c),
                Cond::RewardCard { .. } | Cond::RewardPotion { .. } => f.reward.push(c),
                Cond::MapCount { .. } => f.maps.push(c),
                _ => f.up_front.push(c),
            }
        }
        f
    }

    pub fn is_empty(&self) -> bool {
        self.neow.is_empty() && self.acts.is_empty() && self.reward.is_empty() && self.up_front.is_empty() && self.maps.is_empty()
    }

    pub fn matches(&self, snap: &Snapshot, seed: u64) -> bool {
        // Neow first: when Neow is unlocked it is always the act 1 ancient, and it is the cheapest roll.
        if !self.neow.is_empty() {
            let offers = gen::neow_offers(snap, seed);
            let ok = self.neow.iter().all(|c| match *c {
                Cond::NeowOffer { relic } => offers.contains(&relic),
                Cond::NeowCurse { relic } => offers.last() == Some(&relic),
                _ => true,
            });
            if !ok {
                return false;
            }
        }

        let acts = gen::roll_acts(snap, seed);
        for c in &self.acts {
            if let Cond::Act { slot, act } = *c {
                if acts.get(slot) != Some(&act) {
                    return false;
                }
            }
        }

        if !self.reward.is_empty() {
            let reward = gen::first_reward(snap, seed, None);
            let ok = self.reward.iter().all(|c| match *c {
                Cond::RewardCard { card } => reward.cards.contains(&card),
                Cond::RewardPotion { present } => (reward.potion != gen::NONE) == present,
                _ => true,
            });
            if !ok {
                return false;
            }
        }

        if !self.up_front.is_empty() && !self.up_front_matches(&gen::up_front(snap, seed, &acts)) {
            return false;
        }

        if !self.maps.is_empty() {
            let mut cache: Vec<Option<Option<MapOut>>> = (0..acts.len()).map(|_| None).collect();
            for c in &self.maps {
                let Cond::MapCount { slot, point_type, min, max } = *c else { continue };
                let Some(&act) = acts.get(slot) else { return false };
                let map = cache[slot].get_or_insert_with(|| map::generate(snap, seed, slot, &snap.acts[act as usize]));
                let Some(map) = map else { return false };
                let n = map.count(point_type);
                if n < min || n > max {
                    return false;
                }
            }
        }

        true
    }

    fn up_front_matches(&self, up: &UpFront) -> bool {
        self.up_front.iter().all(|c| match *c {
            Cond::Boss { slot, enc } => up.bosses.get(slot) == Some(&enc),
            Cond::SecondBoss { enc } => up.second_boss == enc,
            Cond::Ancient { slot, ancient } => up.ancients.get(slot) == Some(&ancient),
            Cond::Event { slot, event, within: n } => within(up.events.get(slot), event, n),
            Cond::Elite { slot, enc, within: n } => within(up.elites.get(slot), enc, n),
            Cond::Normal { slot, enc, within: n } => within(up.normals.get(slot), enc, n),
            Cond::Relic { shared, relic, within: n } => {
                let bag = if shared { &up.shared_relics } else { &up.player_relics };
                bag.iter().any(|d| d.relics.iter().take(n).any(|&r| r == relic))
            }
            _ => true,
        })
    }
}
