//! Start-of-run generation. Mirrors StartRunLobby.BeginRunLocally, RunManager.InitializeNewRun,
//! RunManager.GenerateRooms, ActModel.GenerateRooms, Neow.GenerateInitialOptions, AfterObtained of
//! the Neow relics, Act 2/3 AncientEventModel.GenerateInitialOptions, TreasureRoomRelicSynchronizer,
//! MerchantInventory.CreateForNormalMerchant, and the first combat reward.

use crate::data::{ActData, AncientKind, CardData, GiveKind, Snapshot};
use crate::rng::{hash, Rng};
use serde::Serialize;

pub const NONE: i32 = -1;

const CARD_RARITY_BASIC: u8 = 1;
const CARD_RARITY_COMMON: u8 = 2;
const CARD_RARITY_UNCOMMON: u8 = 3;
const CARD_RARITY_RARE: u8 = 4;
const CARD_RARITY_ANCIENT: u8 = 5;

const CARD_TYPE_ATTACK: u8 = 1;
const CARD_TYPE_SKILL: u8 = 2;
const CARD_TYPE_POWER: u8 = 3;

const RELIC_RARITY_COMMON: u32 = 2;
const RELIC_RARITY_UNCOMMON: u32 = 3;
const RELIC_RARITY_RARE: u32 = 4;
const RELIC_RARITY_SHOP: u32 = 5;

const POTION_RARITY_COMMON: u32 = 1;
const POTION_RARITY_UNCOMMON: u32 = 2;
const POTION_RARITY_RARE: u32 = 3;

/// Relics of one rarity in the order a grab bag hands them out.
#[derive(Clone)]
pub struct RelicDeque {
    pub rarity: u32,
    pub relics: Vec<u32>,
}

/// Everything decided by the up-front stream.
pub struct UpFront {
    pub shared_relics: Vec<RelicDeque>,
    pub player_relics: Vec<RelicDeque>,
    pub events: Vec<Vec<u32>>,
    pub normals: Vec<Vec<u32>>,
    pub elites: Vec<Vec<u32>>,
    pub bosses: Vec<i32>,
    pub ancients: Vec<i32>,
    pub second_boss: i32,
}

#[derive(Default, Clone, Serialize)]
pub struct Reward {
    pub gold: i32,
    pub potion: i32,
    pub cards: Vec<u32>,
}

#[derive(Default, Clone, Serialize)]
pub struct ShopOut {
    /// 5 character cards: [Attack, Attack, Skill, Skill, Power]
    pub cards: Vec<u32>,
    /// Index in `cards` (0..5) that is 50% On Sale
    pub sale_index: usize,
    /// 2 colorless cards: [Uncommon, Rare]
    pub colorless: Vec<u32>,
    /// 3 shop relics: [Rolled, Rolled, Shop]
    pub relics: Vec<u32>,
    /// 3 shop potions
    pub potions: Vec<u32>,
}

/// ActModel.GetRandomList followed by the act 1 choice made in the lobby.
pub fn roll_acts(snap: &Snapshot, seed: u64) -> Vec<u32> {
    let mut rng = Rng::named(seed, "act_selection");
    let mut acts: Vec<u32> = snap
        .act_slots
        .iter()
        .map(|slot| match slot.forced {
            Some(act) => act,
            None => rng.next_item(&slot.candidates).expect("no unlocked act for slot"),
        })
        .collect();
    if let (Some(over), Some(first)) = (snap.act1_override, acts.first_mut()) {
        *first = over;
    }
    acts
}

/// RelicGrabBag.Populate: one deque per rarity in first-seen order, each shuffled in turn.
fn populate_bag(entries: &[[u32; 2]], rng: &mut Rng) -> Vec<RelicDeque> {
    let mut deques: Vec<RelicDeque> = Vec::new();
    for &[relic, rarity] in entries {
        match deques.iter_mut().find(|d| d.rarity == rarity) {
            Some(d) => d.relics.push(relic),
            None => deques.push(RelicDeque { rarity, relics: vec![relic] }),
        }
    }
    for d in &mut deques {
        rng.unstable_shuffle(&mut d.relics);
    }
    deques
}

/// GrabBag<EncounterModel> with every weight 1.0.
struct GrabBag {
    entries: Vec<u32>,
    total: f64,
}

impl GrabBag {
    fn new() -> Self {
        GrabBag { entries: Vec::new(), total: 0.0 }
    }

    fn fill(&mut self, items: &[u32]) {
        for &i in items {
            self.entries.push(i);
            self.total += 1.0;
        }
    }

    fn grab_index(&self, rng: &mut Rng) -> Option<usize> {
        let roll = rng.next_double() * self.total;
        let mut acc = 0.0;
        for i in 0..self.entries.len() {
            acc += 1.0;
            if roll < acc {
                return Some(i);
            }
        }
        None
    }

    fn grab_and_remove(&mut self, rng: &mut Rng, pred: Option<&dyn Fn(u32) -> bool>) -> Option<u32> {
        let idx = match pred {
            None => self.grab_index(rng)?,
            Some(p) => {
                if !self.entries.iter().any(|&e| p(e)) {
                    return None;
                }
                loop {
                    match self.grab_index(rng) {
                        None => return None,
                        Some(i) if p(self.entries[i]) => break i,
                        Some(_) => {}
                    }
                }
            }
        };
        self.total -= 1.0;
        Some(self.entries.remove(idx))
    }
}

fn add_without_repeating_tags(snap: &Snapshot, out: &mut Vec<u32>, bag: &mut GrabBag, rng: &mut Rng) {
    let last = out.last().copied();
    let pred = |e: u32| match last {
        None => true,
        Some(l) => snap.enc_tags[e as usize] & snap.enc_tags[l as usize] == 0 && e != l,
    };
    let picked = bag.grab_and_remove(rng, Some(&pred)).or_else(|| bag.grab_and_remove(rng, None));
    if let Some(e) = picked {
        out.push(e);
    }
}

fn fill_encounters(snap: &Snapshot, out: &mut Vec<u32>, pool: &[u32], count: i32, rng: &mut Rng) {
    let mut bag = GrabBag::new();
    for _ in 0..count {
        if bag.entries.is_empty() {
            bag.fill(pool);
        }
        add_without_repeating_tags(snap, out, &mut bag, rng);
    }
}

pub fn up_front(snap: &Snapshot, seed: u64, acts: &[u32]) -> UpFront {
    let mut rng = Rng::named(seed, "up_front");

    // RunManager.InitializeNewRun
    let shared_relics = populate_bag(&snap.shared_bag, &mut rng);
    let player_relics = populate_bag(&snap.player_bag, &mut rng);

    // RunManager.GenerateRooms: hand each later act a slice of the shuffled shared ancients.
    let mut shared = snap.shared_ancients.clone();
    rng.unstable_shuffle(&mut shared);
    let mut subsets: Vec<Vec<u32>> = vec![Vec::new(); acts.len()];
    for subset in subsets.iter_mut().skip(1) {
        let count = rng.next_int(shared.len() as i32 + 1) as usize;
        *subset = shared.drain(..count).collect();
    }

    let n = acts.len();
    let mut out = UpFront {
        shared_relics,
        player_relics,
        events: Vec::with_capacity(n),
        normals: Vec::with_capacity(n),
        elites: Vec::with_capacity(n),
        bosses: Vec::with_capacity(n),
        ancients: Vec::with_capacity(n),
        second_boss: NONE,
    };

    for (i, &act_id) in acts.iter().enumerate() {
        let act: &ActData = &snap.acts[act_id as usize];

        let mut events = act.events.clone();
        rng.unstable_shuffle(&mut events);

        let mut normals = Vec::with_capacity(act.rooms.max(0) as usize);
        fill_encounters(snap, &mut normals, &act.weak, act.weak_count, &mut rng);
        fill_encounters(snap, &mut normals, &act.regular, act.rooms - act.weak_count, &mut rng);
        let mut elites = Vec::with_capacity(15);
        fill_encounters(snap, &mut elites, &act.elite, 15, &mut rng);

        let boss = rng.next_item(&act.boss).map_or(NONE, |b| b as i32);
        let ancient = if subsets[i].is_empty() {
            rng.next_item(&act.ancients)
        } else {
            let mut all = act.ancients.clone();
            all.extend_from_slice(&subsets[i]);
            rng.next_item(&all)
        }
        .map_or(NONE, |a| a as i32);

        if i == n - 1 && snap.double_boss {
            let others: Vec<u32> = act.boss.iter().copied().filter(|&b| b as i32 != boss).collect();
            out.second_boss = rng.next_item(&others).map_or(NONE, |b| b as i32);
        }

        out.events.push(events);
        out.normals.push(normals);
        out.elites.push(elites);
        out.bosses.push(boss);
        out.ancients.push(ancient);
    }

    out
}

/// Neow.GenerateInitialOptions without modifiers. Returns the two positive relics then the curse.
pub fn neow_offers(snap: &Snapshot, seed: u64) -> Vec<u32> {
    let Some(neow) = &snap.neow else { return Vec::new() };
    let allowed = |r: &u32| !neow.disallowed.contains(r);
    // Rng(seed + player slot 0 + hash(event entry))
    let mut rng = Rng::new(seed.wrapping_add(hash(&neow.entry)));

    let curses: Vec<u32> = neow.curse.iter().copied().filter(allowed).collect();
    let Some(curse) = rng.next_item(&curses) else { return Vec::new() };

    let mut positive: Vec<u32> = neow
        .positive
        .iter()
        .copied()
        .filter(|&p| !neow.exclusions.iter().any(|&[c, x]| c == curse && x == p))
        .collect();
    if curse != neow.large_capsule {
        positive.push(if rng.next_bool() { neow.lava_rock } else { neow.small_capsule });
    }
    positive.push(if rng.next_bool() { neow.oyster } else { neow.humidifier });
    positive.push(if rng.next_bool() { neow.talisman } else { neow.pomander });
    positive.retain(allowed);
    rng.unstable_shuffle(&mut positive);

    let mut offers: Vec<u32> = positive.into_iter().take(2).collect();
    offers.push(curse);
    offers
}

/// AncientEventModel.GenerateInitialOptions for any Ancient (Act 1 Neow or Act 2/3 Ancients).
pub fn ancient_offers(snap: &Snapshot, seed: u64, slot: usize, ancient_id: i32) -> Vec<u32> {
    if ancient_id < 0 {
        return Vec::new();
    }
    let id = ancient_id as u32;
    if let Some(neow) = &snap.neow {
        if neow.ancient == id {
            return neow_offers(snap, seed);
        }
    }
    let Some(def) = snap.ancient_defs.iter().find(|d| d.id == id) else {
        return Vec::new();
    };
    let mut rng = Rng::new(seed.wrapping_add(hash(&def.entry)));
    match def.kind {
        AncientKind::Neow => neow_offers(snap, seed),
        AncientKind::Orobas => {
            if def.other_char_count > 0 {
                rng.next_int(def.other_char_count as i32);
            }
            let extra = if rng.next_float() < 0.3333333f32 { def.prismatic_gem } else { def.sea_glass };
            let mut p1 = def.pool1.clone();
            p1.push(extra);
            let mut out = Vec::with_capacity(3);
            out.extend(rng.next_item(&p1));
            out.extend(rng.next_item(&def.pool2));
            out.extend(rng.next_item(&def.pool3));
            out
        }
        AncientKind::ThreePools => {
            let mut out = Vec::with_capacity(3);
            out.extend(rng.next_item(&def.pool1));
            out.extend(rng.next_item(&def.pool2));
            out.extend(rng.next_item(&def.pool3));
            out
        }
        AncientKind::ShuffleThree => {
            let mut pool = def.pool1.clone();
            rng.unstable_shuffle(&mut pool);
            pool.truncate(3);
            pool
        }
        AncientKind::Darv => {
            let base_pool = if slot == 1 { &def.pool1 } else { &def.pool2 };
            let mut source: Vec<u32> = Vec::with_capacity(base_pool.len());
            for &r in base_pool {
                // Darv calls `Rng.NextItem(rs.relics)` on each 1-element ValidRelicSet.
                let _ = rng.next_int(1);
                source.push(r);
            }
            rng.unstable_shuffle(&mut source);
            if rng.next_bool() {
                let mut out: Vec<u32> = source.into_iter().take(2).collect();
                out.push(def.dusty_tome);
                out
            } else {
                source.into_iter().take(3).collect()
            }
        }
        AncientKind::Other => Vec::new(),
    }
}

fn next_rarity_with_wrapping(rarity: u8) -> u8 {
    match rarity {
        CARD_RARITY_BASIC => CARD_RARITY_COMMON,
        CARD_RARITY_COMMON => CARD_RARITY_UNCOMMON,
        CARD_RARITY_UNCOMMON => CARD_RARITY_RARE,
        CARD_RARITY_RARE => CARD_RARITY_COMMON,
        _ => 0,
    }
}

/// How CardFactory.CreateForReward decides the rarity of a card.
enum Odds<'a> {
    /// CardRarityOdds.Roll: regular encounter odds plus the pity offset, which it updates.
    Pity(&'a mut f32),
    /// CardRarityOdds.RollWithBaseOdds, for every source other than an encounter.
    Base,
    /// No roll: any card that is not basic or ancient.
    Uniform,
}

/// One card of CardFactory.CreateForReward, without the upgrade roll. `pool` is what
/// GetPossibleCards returns, `taken` the cards already picked for the same reward.
fn pick_card(snap: &Snapshot, rng: &mut Rng, pool: &[CardData], taken: &[u32], odds: Odds) -> Option<u32> {
    let pool: Vec<&CardData> = pool.iter().filter(|c| !taken.contains(&c.id)).collect();
    let options: Vec<u32> = match odds {
        Odds::Uniform => pool.iter().filter(|c| c.rarity != CARD_RARITY_BASIC && c.rarity != CARD_RARITY_ANCIENT).map(|c| c.id).collect(),
        odds => {
            let roll = rng.next_float();
            let rare_threshold = match &odds {
                Odds::Pity(offset) => snap.rare_odds + **offset,
                _ => snap.rare_odds,
            };
            let mut rarity = if roll < rare_threshold {
                CARD_RARITY_RARE
            } else if roll < snap.uncommon_odds + rare_threshold {
                CARD_RARITY_UNCOMMON
            } else {
                CARD_RARITY_COMMON
            };
            if let Odds::Pity(offset) = odds {
                *offset = if rarity == CARD_RARITY_RARE { -0.05f32 } else { (*offset + snap.rarity_growth).min(0.4f32) };
            }

            // GetNextAllowedRarity
            let first = rarity;
            while rarity != 0 && !pool.iter().any(|c| c.rarity == rarity) {
                rarity = next_rarity_with_wrapping(rarity);
                if rarity == first {
                    rarity = 0;
                }
            }
            pool.iter().filter(|c| c.rarity == rarity).map(|c| c.id).collect()
        }
    };
    rng.next_item(&options)
}

/// PotionFactory.CreateRandomPotions
fn random_potions(snap: &Snapshot, rng: &mut Rng, count: usize) -> Vec<u32> {
    let mut left: Vec<[u32; 2]> = snap.potions.clone();
    let mut out = Vec::with_capacity(count);
    for _ in 0..count {
        let roll = rng.next_float();
        let rarity = if roll <= 0.1f32 {
            POTION_RARITY_RARE
        } else if roll <= 0.35f32 {
            POTION_RARITY_UNCOMMON
        } else {
            POTION_RARITY_COMMON
        };
        let options: Vec<u32> = left.iter().filter(|p| p[1] == rarity).map(|p| p[0]).collect();
        if let Some(potion) = rng.next_item(&options) {
            out.push(potion);
            left.retain(|p| p[0] != potion);
        }
    }
    out
}

pub fn first_reward_from_rng(snap: &Snapshot, rng: &mut Rng, first_encounter: Option<u32>) -> Reward {
    // PotionRewardOdds.Roll, initial value 0.4
    let potion_dropped = rng.next_float() < 0.4f32;

    // GoldReward.Populate
    let [min, max] = first_encounter.map_or([10, 20], |e| snap.enc_gold[e as usize]);
    let gold = rng.next_int_range(min, max + 1);

    // PotionReward.Populate
    let potion = if potion_dropped {
        random_potions(snap, rng, 1).first().map_or(NONE, |&p| p as i32)
    } else {
        NONE
    };

    // CardFactory.CreateForReward, three cards
    let mut offset = -0.05f32;
    let mut cards: Vec<u32> = Vec::with_capacity(3);
    for _ in 0..3 {
        if let Some(card) = pick_card(snap, rng, &snap.cards, &cards, Odds::Pity(&mut offset)) {
            cards.push(card);
        }
        // RollForUpgrade: the chance is 0 in act 1, but the roll is still consumed.
        rng.next_float();
    }

    Reward { gold, potion, cards }
}

/// The reward of the first normal combat of act 1, assuming nothing drew from the rewards
/// stream of the player before it.
pub fn first_reward(snap: &Snapshot, seed: u64, first_encounter: Option<u32>) -> Reward {
    let mut rng = Rng::named(seed, "rewards");
    first_reward_from_rng(snap, &mut rng, first_encounter)
}

#[inline]
fn roll_relic_rarity(rng: &mut Rng) -> u32 {
    let roll = rng.next_float();
    if roll < 0.5f32 {
        RELIC_RARITY_COMMON
    } else if roll < 0.83f32 {
        RELIC_RARITY_UNCOMMON
    } else {
        RELIC_RARITY_RARE
    }
}

/// RelicFactory.PullNextRelicForRewardFromFront(player) or TreasureRoomRelicSynchronizer:
/// a rarity roll, then the first allowed relic of that rarity in the bag, moving on to the
/// next rarity when there is none.
fn pull_relic(rng: &mut Rng, bag: &mut [RelicDeque], disallowed: &[u32]) -> Option<u32> {
    let mut rarity = roll_relic_rarity(rng);
    loop {
        if let Some(deque) = bag.iter_mut().find(|d| d.rarity == rarity) {
            if let Some(i) = deque.relics.iter().position(|r| !disallowed.contains(r)) {
                return Some(deque.relics.remove(i));
            }
        }
        rarity = match rarity {
            RELIC_RARITY_COMMON => RELIC_RARITY_UNCOMMON,
            RELIC_RARITY_UNCOMMON => RELIC_RARITY_RARE,
            _ => return None,
        };
    }
}

/// RelicFactory.PullNextRelicFromBack(player, rarity, filter): pulls the last allowed relic of
/// `rarity` from the back of the bag, wrapping Shop -> Common -> Uncommon -> Rare if empty.
fn pull_relic_back(bag: &mut [RelicDeque], mut rarity: u32, disallowed: &[u32]) -> Option<u32> {
    loop {
        if let Some(deque) = bag.iter_mut().find(|d| d.rarity == rarity) {
            if let Some(i) = deque.relics.iter().rposition(|r| !disallowed.contains(r)) {
                return Some(deque.relics.remove(i));
            }
        }
        rarity = match rarity {
            RELIC_RARITY_SHOP => RELIC_RARITY_COMMON,
            RELIC_RARITY_COMMON => RELIC_RARITY_UNCOMMON,
            RELIC_RARITY_UNCOMMON => RELIC_RARITY_RARE,
            _ => return None,
        };
    }
}

/// TreasureRoomRelicSynchronizer.BeginRelicPicking: rolls on `treasure_room_relics` and pulls
/// from the front of `shared_relics`.
pub fn treasure_relics(snap: &Snapshot, seed: u64, shared_relics: &[RelicDeque], count: usize) -> Vec<u32> {
    let mut rng = Rng::named(seed, "treasure_room_relics");
    let mut bag = shared_relics.to_vec();
    let mut out = Vec::with_capacity(count);
    for _ in 0..count {
        if let Some(r) = pull_relic(&mut rng, &mut bag, &snap.shared_bag_disallowed) {
            out.push(r);
        }
    }
    out
}

/// MerchantInventory.CreateForNormalMerchant(player) before any shop has been visited.
pub fn first_shop(snap: &Snapshot, seed: u64, player_relics: &[RelicDeque]) -> ShopOut {
    let mut rng = Rng::named(seed, "shops");
    let mut bag = player_relics.to_vec();

    // 1. GetDefaultRelicEntries: roll 2 rarities + Shop, pull each from back, and roll cost.
    let r0 = roll_relic_rarity(&mut rng);
    let r1 = roll_relic_rarity(&mut rng);
    let mut relics = Vec::with_capacity(3);
    for rarity in [r0, r1, RELIC_RARITY_SHOP] {
        if let Some(r) = pull_relic_back(&mut bag, rarity, &snap.shop_disallowed) {
            relics.push(r);
        }
        // MerchantRelicEntry.CalcCost
        rng.next_float();
    }

    // 2. GetDefaultPotionEntries: roll 3 potions out of combat, then roll cost for each.
    let potions = random_potions(snap, &mut rng, 3);
    for _ in 0..potions.len() {
        // MerchantPotionEntry.CalcCost
        rng.next_float();
    }

    // 3. Setup() -> CardEntries.Populate(this)
    let mut blacklist: Vec<u32> = Vec::with_capacity(7);
    let mut cards: Vec<u32> = Vec::with_capacity(5);
    let rare_thresh = snap.shop_rare_odds - 0.05f32;
    let uncommon_thresh = rare_thresh + snap.shop_uncommon_odds;

    for card_type in [CARD_TYPE_ATTACK, CARD_TYPE_ATTACK, CARD_TYPE_SKILL, CARD_TYPE_SKILL, CARD_TYPE_POWER] {
        let roll = rng.next_float();
        let mut rarity = if roll < rare_thresh {
            CARD_RARITY_RARE
        } else if roll < uncommon_thresh {
            CARD_RARITY_UNCOMMON
        } else {
            CARD_RARITY_COMMON
        };
        let first = rarity;
        while rarity != 0
            && !snap
                .shop_cards
                .iter()
                .any(|c| !blacklist.contains(&c.id) && c.rarity == rarity && c.card_type == card_type)
        {
            rarity = next_rarity_with_wrapping(rarity);
            if rarity == first {
                rarity = 0;
            }
        }
        let options: Vec<u32> = snap
            .shop_cards
            .iter()
            .filter(|c| !blacklist.contains(&c.id) && c.rarity == rarity && c.card_type == card_type)
            .map(|c| c.id)
            .collect();
        if let Some(card) = rng.next_item(&options) {
            cards.push(card);
            blacklist.push(card);
        }
        // RollForUpgrade + CalcCost
        rng.next_float();
        rng.next_float();
    }

    let mut colorless: Vec<u32> = Vec::with_capacity(2);
    for rarity in [CARD_RARITY_UNCOMMON, CARD_RARITY_RARE] {
        let options: Vec<u32> = snap
            .shop_colorless
            .iter()
            .filter(|c| !blacklist.contains(&c.id) && c.rarity == rarity)
            .map(|c| c.id)
            .collect();
        if let Some(card) = rng.next_item(&options) {
            colorless.push(card);
            blacklist.push(card);
        }
        // RollForUpgrade + CalcCost
        rng.next_float();
        rng.next_float();
    }

    let sale_index = if !cards.is_empty() {
        let idx = rng.next_int(cards.len() as i32) as usize;
        // SetOnSale -> CalcCost
        rng.next_float();
        idx
    } else {
        0
    };

    ShopOut { cards, sale_index, colorless, relics, potions }
}

/// What a relic hands out when it is obtained.
#[derive(Default, Clone)]
pub struct Outcome {
    pub cards: Vec<u32>,
    pub relics: Vec<u32>,
    pub potions: Vec<u32>,
    /// One per entry of `relics` when those roll things of their own (Neow's Bones).
    pub nested: Vec<Nested>,
    /// Floor 1 combat reward after taking this Neow relic.
    pub first_reward: Reward,
}

/// What a relic handed out by Neow's Bones gives. The player takes the relics of Neow's Bones
/// in either order, and the later one carries on from the streams the earlier one used.
#[derive(Default, Clone)]
pub struct Nested {
    pub first: Outcome,
    pub second: Outcome,
}

/// The streams the Neow relics draw from, and the player's relic bag once something pulls from it.
#[derive(Clone)]
struct Streams {
    seed: u64,
    rewards: Rng,
    non_combat: Rng,
    transformations: Rng,
    bag: Option<Vec<RelicDeque>>,
}

impl Streams {
    fn new(seed: u64) -> Self {
        Streams {
            seed,
            rewards: Rng::named(seed, "rewards"),
            non_combat: Rng::named(seed, "non_combat_generation"),
            transformations: Rng::named(seed, "transformations"),
            bag: None,
        }
    }
}

/// CreateForReward with base odds and the upgrade roll, as relics and events use it.
fn reward_cards(snap: &Snapshot, rng: &mut Rng, pool: &[CardData], count: usize) -> Vec<u32> {
    let mut cards = Vec::with_capacity(count);
    for _ in 0..count {
        if let Some(card) = pick_card(snap, rng, pool, &cards, Odds::Base) {
            cards.push(card);
        }
        rng.next_float();
    }
    cards
}

fn with_rarity(pool: &[CardData], rarity: u8) -> Vec<CardData> {
    pool.iter().copied().filter(|c| c.rarity == rarity).collect()
}

/// CardFactory.CreateRandomCardForTransform for a basic card of the character: any common,
/// uncommon or rare card of the pool.
fn transform(snap: &Snapshot, rng: &mut Rng) -> Option<u32> {
    let options: Vec<u32> = snap
        .cards
        .iter()
        .filter(|c| (CARD_RARITY_COMMON..=CARD_RARITY_RARE).contains(&c.rarity))
        .map(|c| c.id)
        .collect();
    rng.next_item(&options)
}

/// AfterObtained of one Neow relic other than Neow's Bones, continuing on `st`. Relics without
/// a roll give an empty outcome.
fn obtain(snap: &Snapshot, relic: u32, st: &mut Streams) -> Outcome {
    let mut out = Outcome::default();
    let Some(neow) = &snap.neow else { return out };
    let Some(give) = neow.gives.iter().find(|g| g.relic == relic) else { return out };

    match give.kind {
        GiveKind::ArcaneScroll | GiveKind::HeftyTablet => {
            let rares = with_rarity(&snap.cards, CARD_RARITY_RARE);
            for _ in 0..give.count {
                if let Some(card) = pick_card(snap, &mut st.rewards, &rares, &out.cards, Odds::Uniform) {
                    out.cards.push(card);
                }
            }
        }
        GiveKind::LeadPaperweight => out.cards = reward_cards(snap, &mut st.rewards, &neow.colorless, give.count),
        GiveKind::LostCoffer => {
            out.cards = reward_cards(snap, &mut st.rewards, &snap.cards, give.count);
            out.potions = random_potions(snap, &mut st.rewards, 1);
        }
        GiveKind::ScrollBoxes => {
            // ScrollBoxes.GenerateRandomBundles
            let commons = with_rarity(&snap.cards, CARD_RARITY_COMMON);
            let uncommons = with_rarity(&snap.cards, CARD_RARITY_UNCOMMON);
            let mut used: Vec<u32> = Vec::new();
            for _ in 0..give.count {
                if let Some(claw) = neow.claw {
                    if st.rewards.next_int(100) < 1 {
                        out.cards.extend([claw; 3]);
                        continue;
                    }
                }
                for pool in [&commons, &commons, &uncommons] {
                    let options: Vec<u32> = pool.iter().map(|c| c.id).filter(|c| !used.contains(c)).collect();
                    if let Some(card) = st.rewards.next_item(&options) {
                        out.cards.push(card);
                        used.push(card);
                    }
                }
            }
        }
        GiveKind::SmallCapsule | GiveKind::LargeCapsule => {
            let seed = st.seed;
            let bag = st.bag.get_or_insert_with(|| {
                let mut up_front = Rng::named(seed, "up_front");
                populate_bag(&snap.shared_bag, &mut up_front);
                populate_bag(&snap.player_bag, &mut up_front)
            });
            for _ in 0..give.count {
                if let Some(r) = pull_relic(&mut st.rewards, bag, &neow.bag_disallowed) {
                    out.relics.push(r);
                    // War Paint and Whetstone shuffle the cards they could upgrade on NonCombatGeneration.
                    if let Some(&[_, n]) = neow.bag_shuffles.iter().find(|s| s[0] == r) {
                        st.non_combat.unstable_shuffle(&mut vec![(); n as usize]);
                    }
                }
            }
        }
        GiveKind::NeowsBones => {}
        GiveKind::Kaleidoscope => {
            for _ in 0..give.count {
                let mut pools: Vec<usize> = (0..neow.other_pools.len()).collect();
                st.non_combat.unstable_shuffle(&mut pools);
                for &pool in pools.iter().take(3) {
                    out.cards.extend(reward_cards(snap, &mut st.rewards, &neow.other_pools[pool], 1));
                }
            }
        }
        GiveKind::PhialHolster => out.potions = random_potions(snap, &mut st.non_combat, give.count),
        GiveKind::LeafyPoultice => {
            // The first Strike, then the first Defend.
            for _ in 0..2 {
                out.cards.extend(transform(snap, &mut st.transformations));
            }
        }
        GiveKind::NewLeaf => out.cards.extend(transform(snap, &mut st.transformations)),
    }

    out
}

/// AfterObtained of the Neow relics that roll something, each as the first thing to draw from
/// its streams. Also computes `first_reward` on the resulting `rewards` RNG stream.
pub fn neow_outcome(snap: &Snapshot, seed: u64, relic: u32, first_encounter: Option<u32>) -> Outcome {
    let mut st = Streams::new(seed);
    let Some(neow) = &snap.neow else {
        return Outcome { first_reward: first_reward_from_rng(snap, &mut st.rewards, first_encounter), ..Outcome::default() };
    };
    let Some(give) = neow.gives.iter().find(|g| g.relic == relic) else {
        return Outcome { first_reward: first_reward_from_rng(snap, &mut st.rewards, first_encounter), ..Outcome::default() };
    };
    if give.kind != GiveKind::NeowsBones {
        let mut out = obtain(snap, relic, &mut st);
        out.first_reward = first_reward_from_rng(snap, &mut st.rewards, first_encounter);
        return out;
    }

    // NeowsBones.AfterObtained: the relics are rewards that cannot be skipped, and the curse is
    // rolled once they have all been taken.
    let mut out = Outcome::default();
    let mut relics = neow.bones.clone();
    st.non_combat.unstable_shuffle(&mut relics);
    relics.truncate(give.count);
    for (i, &given) in relics.iter().enumerate() {
        let first = obtain(snap, given, &mut st.clone());
        let mut after = st.clone();
        for (_, &other) in relics.iter().enumerate().filter(|&(j, _)| j != i) {
            obtain(snap, other, &mut after);
        }
        let second = obtain(snap, given, &mut after);
        out.nested.push(Nested { first, second });
        if i + 1 == relics.len() {
            st = after;
        }
    }
    out.relics = relics;
    out.cards.extend(st.non_combat.next_item(&neow.curses));
    out.first_reward = first_reward_from_rng(snap, &mut st.rewards, first_encounter);
    out
}
