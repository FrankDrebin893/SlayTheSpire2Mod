//! The snapshot of game data the mod exports from ModelDb. Everything is an integer id into
//! tables kept on the C# side; lists are already filtered by unlocks and in the game's order.

use serde::Deserialize;

#[derive(Deserialize, Clone, Copy)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Roll {
    /// `rng.NextGaussianInt(mean, std, min, max)`
    Gaussian { mean: i32, std: i32, min: i32, max: i32 },
    /// `rng.NextInt(min, max)`
    Int { min: i32, max: i32 },
}

#[derive(Deserialize)]
pub struct ActData {
    /// AllEvents + shared events, minus events behind unrevealed epochs.
    pub events: Vec<u32>,
    pub weak: Vec<u32>,
    pub regular: Vec<u32>,
    pub elite: Vec<u32>,
    pub boss: Vec<u32>,
    /// GetUnlockedAncients
    pub ancients: Vec<u32>,
    /// GetNumberOfRooms(false)
    pub rooms: i32,
    pub weak_count: i32,
    /// GetMapPointTypes: the rest roll comes first, then StandardRandomUnknownCount + offset.
    pub rest: Roll,
    pub unknown_offset: i32,
}

#[derive(Deserialize)]
pub struct ActSlot {
    /// Unlocked acts for this index, in ModelDb.ActsByIndex order.
    pub candidates: Vec<u32>,
    /// An undiscovered non-default act is forced without consuming the rng.
    pub forced: Option<u32>,
}

#[derive(Deserialize)]
pub struct NeowData {
    /// Id.Entry of the event, mixed into its rng seed.
    pub entry: String,
    pub ancient: u32,
    pub curse: Vec<u32>,
    pub positive: Vec<u32>,
    pub lava_rock: u32,
    pub small_capsule: u32,
    pub oyster: u32,
    pub humidifier: u32,
    pub talisman: u32,
    pub pomander: u32,
    pub large_capsule: u32,
    /// (curse relic, positive relic removed when that curse is rolled)
    pub exclusions: Vec<[u32; 2]>,
    /// Relics for which IsAllowedAtNeow is false.
    pub disallowed: Vec<u32>,
    /// The Neow relics that roll something when obtained.
    pub gives: Vec<Give>,
    /// ColorlessCardPool, unlocked.
    pub colorless: Vec<CardData>,
    /// The card pools of the other characters, sorted the way StableShuffle sorts them.
    pub other_pools: Vec<Vec<CardData>>,
    /// Curses Neow's Bones can add, ordered by id.
    pub curses: Vec<u32>,
    /// NeowsBones.GetValidRelics
    pub bones: Vec<u32>,
    /// Claw, only for the Defect: Scroll Boxes can roll a bundle of three.
    pub claw: Option<u32>,
    /// Relics in the player bag for which IsAllowed is false at the start of a run.
    pub bag_disallowed: Vec<u32>,
}

#[derive(Deserialize, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum GiveKind {
    ArcaneScroll,
    HeftyTablet,
    LeadPaperweight,
    LostCoffer,
    ScrollBoxes,
    SmallCapsule,
    LargeCapsule,
    NeowsBones,
    Kaleidoscope,
    PhialHolster,
    LeafyPoultice,
    NewLeaf,
}

#[derive(Deserialize, Clone, Copy)]
pub struct Give {
    pub relic: u32,
    pub kind: GiveKind,
    /// The count the relic states (cards, relics or potions), read from its DynamicVars.
    pub count: usize,
}

#[derive(Deserialize, Clone, Copy)]
pub struct CardData {
    pub id: u32,
    /// CardRarity as int
    pub rarity: u8,
}

#[derive(Deserialize)]
pub struct Snapshot {
    pub acts: Vec<ActData>,
    pub act_slots: Vec<ActSlot>,
    /// The act 1 picked in the lobby, replacing the rolled one.
    pub act1_override: Option<u32>,
    /// Bitmask of EncounterTag per encounter id.
    pub enc_tags: Vec<u64>,
    /// [MinGoldReward, MaxGoldReward] per encounter id.
    pub enc_gold: Vec<[i32; 2]>,
    pub shared_ancients: Vec<u32>,
    /// [relic, rarity] in population order.
    pub shared_bag: Vec<[u32; 2]>,
    pub player_bag: Vec<[u32; 2]>,
    pub neow: Option<NeowData>,
    pub cards: Vec<CardData>,
    /// [potion, rarity]
    pub potions: Vec<[u32; 2]>,
    pub rare_odds: f32,
    pub uncommon_odds: f32,
    pub rarity_growth: f32,
    pub map_elites: i32,
    pub map_shops: i32,
    pub double_boss: bool,
}
