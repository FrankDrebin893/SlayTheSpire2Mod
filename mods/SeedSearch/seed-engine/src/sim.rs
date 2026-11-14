//! Full simulation of one seed (for previews and the parity check) and the filter used by the
//! search, which only computes the stages its conditions need, cheapest first.

use crate::data::Snapshot;
use crate::gen::{self, Outcome, RelicDeque, Reward, ShopOut, UpFront};
use crate::map::{self, MapOut};
use crate::rng::hash;
use serde::{Deserialize, Serialize};

pub const TREASURE_RELIC_COUNT: usize = 6;

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
    pub max_path_elites: i32,
    pub max_path_rests: i32,
    pub max_path_shops: i32,
    pub max_path_unknowns: i32,
}

#[derive(Serialize)]
pub struct SimOut {
    pub seed: String,
    pub acts: Vec<u32>,
    pub bosses: Vec<i32>,
    pub second_boss: i32,
    pub ancients: Vec<i32>,
    pub ancient_relics: Vec<Vec<u32>>,
    pub events: Vec<Vec<u32>>,
    pub normals: Vec<Vec<u32>>,
    pub elites: Vec<Vec<u32>>,
    pub shared_relics: Vec<DequeOut>,
    pub player_relics: Vec<DequeOut>,
    pub treasure_relics: Vec<u32>,
    pub first_shop: ShopOut,
    pub neow: Vec<u32>,
    /// What each offer in `neow` gives when taken.
    pub neow_outcomes: Vec<OutcomeOut>,
    pub gold: i32,
    pub potion: i32,
    pub cards: Vec<u32>,
    pub maps: Vec<Option<MapJson>>,
}

#[derive(Serialize)]
pub struct OutcomeOut {
    pub cards: Vec<u32>,
    pub relics: Vec<u32>,
    pub potions: Vec<u32>,
    /// One per entry of `relics` for Neow's Bones, otherwise empty.
    pub nested: Vec<NestedOut>,
    pub first_gold: i32,
    pub first_potion: i32,
    pub first_cards: Vec<u32>,
}

/// What a relic given by Neow's Bones gives when it is taken first and when it is taken second.
#[derive(Serialize)]
pub struct NestedOut {
    pub first: GivenOut,
    pub second: GivenOut,
}

#[derive(Serialize)]
pub struct GivenOut {
    pub cards: Vec<u32>,
    pub relics: Vec<u32>,
    pub potions: Vec<u32>,
}

fn given(o: Outcome) -> GivenOut {
    GivenOut { cards: o.cards, relics: o.relics, potions: o.potions }
}

fn deques(d: Vec<RelicDeque>) -> Vec<DequeOut> {
    d.into_iter().map(|d| DequeOut { rarity: d.rarity, relics: d.relics }).collect()
}

fn map_json(m: MapOut) -> MapJson {
    MapJson {
        points: m.points.iter().map(|&(c, r, t)| [c, r, t as i32]).collect(),
        edges: m.edges.iter().map(|&(a, b, c, d)| [a, b, c, d]).collect(),
        max_path_elites: m.max_path_elites,
        max_path_rests: m.max_path_rests,
        max_path_shops: m.max_path_shops,
        max_path_unknowns: m.max_path_unknowns,
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
    let neow_outcomes = neow
        .iter()
        .map(|&relic| {
            let Outcome { cards, relics, potions, nested, first_reward } = gen::neow_outcome(snap, seed, relic, first_encounter);
            let nested = nested.into_iter().map(|n| NestedOut { first: given(n.first), second: given(n.second) }).collect();
            OutcomeOut {
                cards,
                relics,
                potions,
                nested,
                first_gold: first_reward.gold,
                first_potion: first_reward.potion,
                first_cards: first_reward.cards,
            }
        })
        .collect();
    let ancient_relics = up
        .ancients
        .iter()
        .enumerate()
        .map(|(slot, &anc)| {
            if slot == 0 {
                neow.clone()
            } else {
                gen::ancient_offers(snap, seed, slot, anc)
            }
        })
        .collect();
    let treasure_relics = gen::treasure_relics(snap, seed, &up.shared_relics, TREASURE_RELIC_COUNT);
    let first_shop = gen::first_shop(snap, seed, &up.player_relics);
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
        ancient_relics,
        events: up.events,
        normals: up.normals,
        elites: up.elites,
        shared_relics: deques(up.shared_relics),
        player_relics: deques(up.player_relics),
        treasure_relics,
        first_shop,
        neow,
        neow_outcomes,
        gold: reward.gold,
        potion: reward.potion,
        cards: reward.cards,
        maps,
    }
}

#[derive(Deserialize, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum Item {
    Card,
    Relic,
    Potion,
}

/// One search condition; a seed must satisfy all of them.
#[derive(Deserialize, Clone)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Cond {
    /// Any of the three Neow options.
    NeowOffer { relic: u32 },
    /// The cursed (third) Neow option.
    NeowCurse { relic: u32 },
    /// The relic is offered and taking it gives the item.
    NeowGives { relic: u32, what: Item, item: u32 },
    /// The relic is offered, taking it gives the relic `via`, and that gives the item. All of
    /// these for one relic must hold for the same order of taking what it gives.
    NeowNested { relic: u32, via: u32, what: Item, item: u32 },
    Act { slot: usize, act: u32 },
    Boss { slot: usize, enc: i32 },
    SecondBoss { enc: i32 },
    Ancient { slot: usize, ancient: i32 },
    /// The Ancient in act `slot` offers `relic`.
    AncientRelic { slot: usize, relic: u32 },
    /// The event is among the first `within` events queued for the act.
    Event { slot: usize, event: u32, within: usize },
    Elite { slot: usize, enc: u32, within: usize },
    Normal { slot: usize, enc: u32, within: usize },
    /// The relic is among the first `within` of its rarity in the shared or the player bag.
    Relic { shared: bool, relic: u32, within: usize },
    /// The relic is among the first `within` Treasure Chest relics rolled from the shared bag.
    TreasureRelic { relic: u32, within: usize },
    /// The First Shop sells `item` (Card, Relic, or Potion).
    ShopItem { what: Item, item: u32 },
    MapCount { slot: usize, point_type: u8, min: i32, max: i32 },
    /// Maximum count of `point_type` reachable on a single legal path in act `slot`.
    MapPathCount { slot: usize, point_type: u8, min: i32, max: i32 },
    RewardCard {
        card: u32,
        #[serde(default)]
        after_neow: Option<u32>,
    },
    RewardPotion { present: bool },
    /// Holds if ANY of the child conditions in `conds` holds.
    AnyOf { conds: Vec<Cond> },
}

impl Cond {
    /// Cost stage (0 = cheapest, 5 = most expensive) so conditions are evaluated cheapest-first.
    fn stage(&self) -> u8 {
        match self {
            Cond::NeowOffer { .. }
            | Cond::NeowCurse { .. }
            | Cond::NeowGives { .. }
            | Cond::NeowNested { .. }
            | Cond::RewardCard { after_neow: Some(_), .. } => 0,
            Cond::Act { .. } => 1,
            Cond::RewardCard { after_neow: None, .. } | Cond::RewardPotion { .. } => 2,
            Cond::Boss { .. }
            | Cond::SecondBoss { .. }
            | Cond::Ancient { .. }
            | Cond::AncientRelic { .. }
            | Cond::Event { .. }
            | Cond::Elite { .. }
            | Cond::Normal { .. }
            | Cond::Relic { .. }
            | Cond::TreasureRelic { .. } => 3,
            Cond::ShopItem { .. } => 4,
            Cond::MapCount { .. } | Cond::MapPathCount { .. } => 5,
            Cond::AnyOf { conds } => conds.iter().map(|c| c.stage()).max().unwrap_or(0),
        }
    }
}

pub struct Filter {
    conds: Vec<Cond>,
    nested_relics: Vec<u32>,
}

fn has(outcome: &Outcome, what: Item, item: u32) -> bool {
    match what {
        Item::Card => outcome.cards.contains(&item),
        Item::Relic => outcome.relics.contains(&item),
        Item::Potion => outcome.potions.contains(&item),
    }
}

fn within(list: Option<&Vec<u32>>, item: u32, n: usize) -> bool {
    list.is_some_and(|l| l.iter().take(n).any(|&x| x == item))
}

struct EvalCtx<'a> {
    snap: &'a Snapshot,
    seed: u64,
    neow_offers: Option<Vec<u32>>,
    neow_outcomes: Vec<(u32, Outcome)>,
    acts: Option<Vec<u32>>,
    reward: Option<Reward>,
    up: Option<UpFront>,
    ancient_relics: [Option<Vec<u32>>; 3],
    treasure: Option<Vec<u32>>,
    shop: Option<ShopOut>,
    maps: [Option<Option<MapOut>>; 3],
}

impl<'a> EvalCtx<'a> {
    fn new(snap: &'a Snapshot, seed: u64) -> Self {
        EvalCtx {
            snap,
            seed,
            neow_offers: None,
            neow_outcomes: Vec::new(),
            acts: None,
            reward: None,
            up: None,
            ancient_relics: [None, None, None],
            treasure: None,
            shop: None,
            maps: [None, None, None],
        }
    }

    fn offers(&mut self) -> &[u32] {
        let snap = self.snap;
        let seed = self.seed;
        self.neow_offers.get_or_insert_with(|| gen::neow_offers(snap, seed))
    }

    fn outcome(&mut self, relic: u32) -> &Outcome {
        if let Some(pos) = self.neow_outcomes.iter().position(|(r, _)| *r == relic) {
            return &self.neow_outcomes[pos].1;
        }
        let out = gen::neow_outcome(self.snap, self.seed, relic, None);
        self.neow_outcomes.push((relic, out));
        &self.neow_outcomes.last().unwrap().1
    }

    fn acts(&mut self) -> &[u32] {
        let snap = self.snap;
        let seed = self.seed;
        self.acts.get_or_insert_with(|| gen::roll_acts(snap, seed))
    }

    fn reward(&mut self) -> &Reward {
        let snap = self.snap;
        let seed = self.seed;
        self.reward.get_or_insert_with(|| gen::first_reward(snap, seed, None))
    }

    fn up(&mut self) -> &UpFront {
        if self.up.is_none() {
            let acts = self.acts().to_vec();
            self.up = Some(gen::up_front(self.snap, self.seed, &acts));
        }
        self.up.as_ref().unwrap()
    }

    fn ancient_offers_for(&mut self, slot: usize) -> &[u32] {
        if slot >= 3 {
            return &[];
        }
        if self.ancient_relics[slot].is_none() {
            let anc = self.up().ancients.get(slot).copied().unwrap_or(gen::NONE);
            let offers = if slot == 0 {
                self.offers().to_vec()
            } else {
                gen::ancient_offers(self.snap, self.seed, slot, anc)
            };
            self.ancient_relics[slot] = Some(offers);
        }
        self.ancient_relics[slot].as_ref().unwrap()
    }

    fn treasure_relics(&mut self) -> &[u32] {
        if self.treasure.is_none() {
            let shared = self.up().shared_relics.clone();
            self.treasure = Some(gen::treasure_relics(self.snap, self.seed, &shared, TREASURE_RELIC_COUNT));
        }
        self.treasure.as_ref().unwrap()
    }

    fn shop(&mut self) -> &ShopOut {
        if self.shop.is_none() {
            let player_bag = self.up().player_relics.clone();
            self.shop = Some(gen::first_shop(self.snap, self.seed, &player_bag));
        }
        self.shop.as_ref().unwrap()
    }

    fn map(&mut self, slot: usize) -> Option<&MapOut> {
        if slot >= 3 {
            return None;
        }
        if self.maps[slot].is_none() {
            let act = *self.acts().get(slot)?;
            let m = map::generate(self.snap, self.seed, slot, &self.snap.acts[act as usize]);
            self.maps[slot] = Some(m);
        }
        self.maps[slot].as_ref().unwrap().as_ref()
    }

    fn eval(&mut self, c: &Cond) -> bool {
        match *c {
            Cond::NeowOffer { relic } => self.offers().contains(&relic),
            Cond::NeowCurse { relic } => self.offers().last() == Some(&relic),
            Cond::NeowGives { relic, what, item } => {
                self.offers().contains(&relic) && has(self.outcome(relic), what, item)
            }
            Cond::NeowNested { relic, via, what, item } => {
                if !self.offers().contains(&relic) {
                    return false;
                }
                let o = self.outcome(relic);
                o.relics.iter().position(|&g| g == via).is_some_and(|i| {
                    let n = &o.nested[i];
                    has(&n.first, what, item) || has(&n.second, what, item)
                })
            }
            Cond::Act { slot, act } => self.acts().get(slot) == Some(&act),
            Cond::RewardCard { card, after_neow } => match after_neow {
                None => self.reward().cards.contains(&card),
                Some(relic) => {
                    self.offers().contains(&relic) && self.outcome(relic).first_reward.cards.contains(&card)
                }
            },
            Cond::RewardPotion { present } => (self.reward().potion != gen::NONE) == present,
            Cond::Boss { slot, enc } => self.up().bosses.get(slot) == Some(&enc),
            Cond::SecondBoss { enc } => self.up().second_boss == enc,
            Cond::Ancient { slot, ancient } => self.up().ancients.get(slot) == Some(&ancient),
            Cond::AncientRelic { slot, relic } => self.ancient_offers_for(slot).contains(&relic),
            Cond::Event { slot, event, within: n } => within(self.up().events.get(slot), event, n),
            Cond::Elite { slot, enc, within: n } => within(self.up().elites.get(slot), enc, n),
            Cond::Normal { slot, enc, within: n } => within(self.up().normals.get(slot), enc, n),
            Cond::Relic { shared, relic, within: n } => {
                let bag = if shared { &self.up().shared_relics } else { &self.up().player_relics };
                bag.iter().any(|d| d.relics.iter().take(n).any(|&r| r == relic))
            }
            Cond::TreasureRelic { relic, within: n } => {
                self.treasure_relics().iter().take(n).any(|&r| r == relic)
            }
            Cond::ShopItem { what, item } => {
                let s = self.shop();
                match what {
                    Item::Card => s.cards.contains(&item) || s.colorless.contains(&item),
                    Item::Relic => s.relics.contains(&item),
                    Item::Potion => s.potions.contains(&item),
                }
            }
            Cond::MapCount { slot, point_type, min, max } => {
                let Some(m) = self.map(slot) else { return false };
                let n = m.count(point_type);
                n >= min && n <= max
            }
            Cond::MapPathCount { slot, point_type, min, max } => {
                let Some(m) = self.map(slot) else { return false };
                let n = m.max_on_path(point_type);
                n >= min && n <= max
            }
            Cond::AnyOf { ref conds } => conds.iter().any(|sub| self.eval(sub)),
        }
    }
}

impl Filter {
    pub fn new(mut conds: Vec<Cond>) -> Self {
        conds.sort_by_key(|c| c.stage());
        let mut nested_relics = Vec::new();
        for c in &conds {
            if let Cond::NeowNested { relic, .. } = *c {
                if !nested_relics.contains(&relic) {
                    nested_relics.push(relic);
                }
            }
        }
        Filter { conds, nested_relics }
    }

    pub fn is_empty(&self) -> bool {
        self.conds.is_empty()
    }

    pub fn matches(&self, snap: &Snapshot, seed: u64) -> bool {
        let mut ctx = EvalCtx::new(snap, seed);
        for c in &self.conds {
            if !ctx.eval(c) {
                return false;
            }
        }
        for &relic in &self.nested_relics {
            let outcome = ctx.outcome(relic).clone();
            let holds = |taken_first: usize| {
                self.conds.iter().all(|c| match *c {
                    Cond::NeowNested { relic: r, via, what, item } if r == relic => {
                        outcome.relics.iter().position(|&g| g == via).is_some_and(|i| {
                            let nested = &outcome.nested[i];
                            has(if i == taken_first { &nested.first } else { &nested.second }, what, item)
                        })
                    }
                    _ => true,
                })
            };
            if !(0..outcome.relics.len()).any(holds) {
                return false;
            }
        }
        true
    }
}
