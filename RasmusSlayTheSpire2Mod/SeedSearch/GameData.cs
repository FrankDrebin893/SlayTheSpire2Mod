using System.Reflection;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Gives models of one kind small integer ids. The Rust engine only ever sees these ids.
public sealed class IdTable<T> where T : AbstractModel
{
    private readonly Dictionary<ModelId, int> _ids = new();
    private readonly List<T> _items = [];

    public int Count => _items.Count;

    public T this[int id] => _items[id];

    public int Id(T model)
    {
        if (!_ids.TryGetValue(model.Id, out int id))
        {
            id = _items.Count;
            _ids[model.Id] = id;
            _items.Add(model);
        }

        return id;
    }
}

public sealed class ActInfo
{
    public int[] Events = [];
    public int[] Weak = [];
    public int[] Regular = [];
    public int[] Elite = [];
    public int[] Boss = [];
    public int[] Ancients = [];
    public int Rooms;
}

// Everything start-of-run generation depends on for one character, ascension and act 1 choice,
// read from ModelDb and the player's unlocks in the game's own list order. SnapshotJson is the
// form handed to the Rust engine (see seed-engine/src/data.rs); the arrays feed the pickers.
public sealed class GameData
{
    public const int MapUnknown = (int)MapPointType.Unknown;
    public const int MapShop = (int)MapPointType.Shop;
    public const int MapRest = (int)MapPointType.RestSite;
    public const int MapElite = (int)MapPointType.Elite;

    private static readonly RelicRarity[] BagRarities = [RelicRarity.Common, RelicRarity.Uncommon, RelicRarity.Rare, RelicRarity.Shop];

    public readonly IdTable<ActModel> Acts = new();
    public readonly IdTable<EncounterModel> Encounters = new();
    public readonly IdTable<EventModel> Events = new();
    public readonly IdTable<AncientEventModel> Ancients = new();
    public readonly IdTable<RelicModel> Relics = new();
    public readonly IdTable<CardModel> Cards = new();
    public readonly IdTable<PotionModel> Potions = new();

    public CharacterModel Character { get; }
    public int Ascension { get; }
    public string Act1Key { get; }

    public ActInfo[] ActInfos { get; private set; } = [];
    // The acts that can end up in each slot.
    public int[][] SlotActs { get; private set; } = [];
    public int[] SharedAncients { get; private set; } = [];
    public int[] SharedBag { get; private set; } = [];
    public int[] PlayerBag { get; private set; } = [];
    public int[] NeowRelics { get; private set; } = [];
    public int NeowId { get; private set; } = -1;
    public int[] RewardCards { get; private set; } = [];
    public int[] PotionOptions { get; private set; } = [];
    public int[] ColorlessCards { get; private set; } = [];
    public int[] OtherCharacterCards { get; private set; } = [];
    public int[] Curses { get; private set; } = [];
    public int[] BonesRelics { get; private set; } = [];
    // The cursed offers, for marking them in the pickers.
    public int[] NeowCurses { get; private set; } = [];
    public bool DoubleBoss { get; private set; }
    public string SnapshotJson { get; private set; } = "";

    private GameData(CharacterModel character, int ascension, string act1Key)
    {
        Character = character;
        Ascension = ascension;
        Act1Key = act1Key;
    }

    public static GameData Build(CharacterModel character, int ascension, string act1Key)
    {
        GameData data = new(character, ascension, act1Key);
        SeedSimulator.WithAscension(ascension, () =>
        {
            data.Export();
            return 0;
        });
        return data;
    }

    public string ActName(int id) => Acts[id].Title.GetFormattedText();
    public string EncounterName(int id) => id < 0 ? "none" : Encounters[id].Title.GetFormattedText();
    public string EventName(int id) => Events[id].Title.GetFormattedText();
    public string AncientName(int id) => id < 0 ? "none" : Ancients[id].Title.GetFormattedText();
    public string RelicName(int id) => id < 0 ? "?" : Relics[id].Title.GetFormattedText();
    public string CardName(int id) => Cards[id].Title;
    public string PotionName(int id) => id < 0 ? "none" : Potions[id].Title.GetFormattedText();

    private void Export()
    {
        SeedSimulator.Run run = SeedSimulator.CreateRun("0", Character, Ascension, Act1Key);
        UnlockState unlocks = run.Unlocks;
        Player player = run.Player;

        List<ActModel> acts = ModelDb.Acts.Where(a => a.Index >= 0).ToList();
        foreach (ActModel act in acts)
        {
            Acts.Id(act);
            foreach (EncounterModel e in act.AllEncounters)
                Encounters.Id(e);
        }

        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartObject();

            w.WriteStartArray("acts");
            ActInfos = acts.Select(a => WriteAct(w, a, unlocks)).ToArray();
            w.WriteEndArray();

            WriteActSlots(w, unlocks);

            w.WriteStartArray("enc_tags");
            for (int i = 0; i < Encounters.Count; i++)
                w.WriteNumberValue(Encounters[i].Tags.Aggregate(0UL, (mask, tag) => mask | 1UL << (int)tag));
            w.WriteEndArray();

            w.WriteStartArray("enc_gold");
            for (int i = 0; i < Encounters.Count; i++)
                w.WriteInts([Encounters[i].MinGoldReward, Encounters[i].MaxGoldReward]);
            w.WriteEndArray();

            SharedAncients = unlocks.SharedAncients.Select(a => Ancients.Id(a)).ToArray();
            w.WriteInts("shared_ancients", SharedAncients);

            // RunManager.InitializeNewRun fills the shared bag with every unlocked shared relic,
            // while RelicGrabBag.Populate(player) keeps only the four reward rarities.
            List<RelicModel> shared = ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(unlocks).ToList();
            List<RelicModel> mine = shared.Concat(Character.RelicPool.GetUnlockedRelics(unlocks)).Where(r => BagRarities.Contains(r.Rarity)).ToList();
            SharedBag = WriteBag(w, "shared_bag", shared);
            PlayerBag = WriteBag(w, "player_bag", mine);

            WriteNeow(w, run, mine);

            List<CardModel> cards = CardCreationOptions.ForRoom(player, RoomType.Monster).GetPossibleCards(player)
                .Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
                .ToList();
            RewardCards = WriteCards(w, "cards", cards);

            List<PotionModel> potions = PotionFactory.GetPotionOptions(player).ToList();
            PotionOptions = potions.Select(p => Potions.Id(p)).ToArray();
            w.WriteStartArray("potions");
            foreach (PotionModel p in potions)
                w.WriteInts([Potions.Id(p), (int)p.Rarity]);
            w.WriteEndArray();

            w.WriteNumber("rare_odds", CardRarityOdds.RegularRareOdds);
            w.WriteNumber("uncommon_odds", CardRarityOdds.regularUncommonOdds);
            w.WriteNumber("rarity_growth", player.PlayerOdds.CardRarity.RarityGrowth);

            MapPointTypeCounts counts = new(0, 0);
            w.WriteNumber("map_elites", counts.NumOfElites);
            w.WriteNumber("map_shops", counts.NumOfShops);

            DoubleBoss = SeedSimulator.AscensionOverride!.HasLevel(AscensionLevel.DoubleBoss);
            w.WriteBoolean("double_boss", DoubleBoss);

            w.WriteEndObject();
        }

        SnapshotJson = Encoding.UTF8.GetString(stream.ToArray());
    }

    private ActInfo WriteAct(Utf8JsonWriter w, ActModel act, UnlockState unlocks)
    {
        // Let the game decide which events are unlocked: generate rooms on a scratch copy and
        // keep the events that survived, restored to the order GenerateRooms starts from.
        ActModel scratch = act.ToMutable();
        scratch.GenerateRooms(new Rng(0uL), unlocks, false);
        HashSet<ModelId> unlocked = ((RoomSet)SeedSimulator.RoomsField.GetValue(scratch)!).events.Select(e => e.Id).ToHashSet();

        ActInfo info = new()
        {
            Events = act.AllEvents.Concat(ModelDb.AllSharedEvents).Where(e => unlocked.Contains(e.Id)).Select(e => Events.Id(e)).ToArray(),
            Weak = act.AllWeakEncounters.Select(e => Encounters.Id(e)).ToArray(),
            Regular = act.AllRegularEncounters.Select(e => Encounters.Id(e)).ToArray(),
            Elite = act.AllEliteEncounters.Select(e => Encounters.Id(e)).ToArray(),
            Boss = act.AllBossEncounters.Select(e => Encounters.Id(e)).ToArray(),
            Ancients = act.GetUnlockedAncients(unlocks).Select(a => Ancients.Id(a)).ToArray(),
            Rooms = act.GetNumberOfRooms(false)
        };

        w.WriteStartObject();
        w.WriteInts("events", info.Events);
        w.WriteInts("weak", info.Weak);
        w.WriteInts("regular", info.Regular);
        w.WriteInts("elite", info.Elite);
        w.WriteInts("boss", info.Boss);
        w.WriteInts("ancients", info.Ancients);
        w.WriteNumber("rooms", info.Rooms);
        w.WriteNumber("weak_count", (int)AccessTools.PropertyGetter(act.GetType(), "NumberOfWeakEncounters").Invoke(act, null)!);
        WriteMapRolls(w, act);
        w.WriteEndObject();
        return info;
    }

    // ActModel.GetMapPointTypes is code, not data, so each act's rolls are restated here. An act
    // this does not know makes the export fail, which sends the screen to the C# fallback.
    private static void WriteMapRolls(Utf8JsonWriter w, ActModel act)
    {
        (bool gaussian, int mean, int min, int max, int unknownOffset) = act switch
        {
            Overgrowth or Underdocks => (true, 7, 6, 7, 0),
            Hive => (true, 6, 6, 7, -1),
            Glory => (false, 0, 5, 7, -1),
            _ => throw new NotSupportedException($"Seed search does not know the map rules of act {act.Id.Entry}.")
        };

        w.WriteStartObject("rest");
        w.WriteString("kind", gaussian ? "gaussian" : "int");
        if (gaussian)
        {
            w.WriteNumber("mean", mean);
            w.WriteNumber("std", 1);
        }

        w.WriteNumber("min", min);
        w.WriteNumber("max", max);
        w.WriteEndObject();
        w.WriteNumber("unknown_offset", unknownOffset);
    }

    // ActModel.GetRandomList
    private void WriteActSlots(Utf8JsonWriter w, UnlockState unlocks)
    {
        int? act1 = Act1Key switch
        {
            "overgrowth" => Acts.Id(ModelDb.Act<Overgrowth>()),
            "underdocks" => Acts.Id(ModelDb.Act<Underdocks>()),
            _ => null
        };

        List<int[]> slotActs = [];
        w.WriteStartArray("act_slots");
        foreach (IReadOnlyList<ActModel> options in ModelDb.ActsByIndex)
        {
            List<ActModel> unlocked = options.Where(a => a.IsUnlocked(unlocks)).ToList();
            ActModel? forced = unlocked.FirstOrDefault(a => !a.IsDefault && !SaveManager.Instance.Progress.DiscoveredActs.Contains(a.Id));
            // The game stops collecting candidates at a forced act, and never rolls for that slot.
            int[] candidates = (forced == null ? unlocked : unlocked.TakeWhile(a => a != forced)).Select(a => Acts.Id(a)).ToArray();

            w.WriteStartObject();
            w.WriteInts("candidates", candidates);
            if (forced == null)
                w.WriteNull("forced");
            else
                w.WriteNumber("forced", Acts.Id(forced));
            w.WriteEndObject();

            slotActs.Add(slotActs.Count == 0 && act1 != null ? [act1.Value] : forced != null ? [Acts.Id(forced)] : candidates);
        }

        w.WriteEndArray();
        if (act1 == null)
            w.WriteNull("act1_override");
        else
            w.WriteNumber("act1_override", act1.Value);
        SlotActs = slotActs.ToArray();
    }

    private int[] WriteBag(Utf8JsonWriter w, string name, List<RelicModel> relics)
    {
        w.WriteStartArray(name);
        foreach (RelicModel r in relics)
            w.WriteInts([Relics.Id(r), (int)r.Rarity]);
        w.WriteEndArray();
        return relics.Select(r => Relics.Id(r)).ToArray();
    }

    // The Neow relics whose AfterObtained rolls something, as the kind the Rust engine knows them
    // by and the count the relic states. SeedSimulator.NeowOutcome is the game-code counterpart.
    public static (string Kind, int Count)? NeowGiveOf(RelicModel relic) => relic switch
    {
        ArcaneScroll => ("arcane_scroll", relic.DynamicVars.Cards.IntValue),
        HeftyTablet => ("hefty_tablet", relic.DynamicVars.Cards.IntValue),
        LeadPaperweight => ("lead_paperweight", 2),
        LostCoffer => ("lost_coffer", 3),
        ScrollBoxes => ("scroll_boxes", 2),
        SmallCapsule => ("small_capsule", 1),
        LargeCapsule => ("large_capsule", relic.DynamicVars["Relics"].IntValue),
        NeowsBones => ("neows_bones", relic.DynamicVars["Relics"].IntValue),
        Kaleidoscope => ("kaleidoscope", relic.DynamicVars.Cards.IntValue),
        PhialHolster => ("phial_holster", relic.DynamicVars["Potions"].IntValue),
        LeafyPoultice => ("leafy_poultice", 2),
        NewLeaf => ("new_leaf", relic.DynamicVars.Cards.IntValue),
        _ => null
    };

    // Everything a Neow relic can hand out, for the pickers. All empty for a relic without a roll.
    public (int[] Cards, int[] Relics, int[] Potions) NeowGiveOptions(int relic)
    {
        int[] OfRarity(params CardRarity[] rarities) => RewardCards.Where(c => rarities.Contains(Cards[c].Rarity)).ToArray();
        return Relics[relic] switch
        {
            ArcaneScroll or HeftyTablet => (OfRarity(CardRarity.Rare), [], []),
            LeadPaperweight => (ColorlessCards, [], []),
            LostCoffer => (OfRarity(CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare), [], PotionOptions),
            ScrollBoxes => (OfRarity(CardRarity.Common, CardRarity.Uncommon), [], []),
            SmallCapsule or LargeCapsule => ([], PlayerBag.Where(r => Relics[r].Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare).ToArray(), []),
            NeowsBones => (Curses, BonesRelics, []),
            Kaleidoscope => (OtherCharacterCards, [], []),
            PhialHolster => ([], [], PotionOptions),
            LeafyPoultice or NewLeaf => (OfRarity(CardRarity.Common, CardRarity.Uncommon, CardRarity.Rare), [], []),
            _ => ([], [], [])
        };
    }

    private int[] WriteCards(Utf8JsonWriter w, string name, IEnumerable<CardModel> cards)
    {
        List<int> ids = [];
        w.WriteStartArray(name);
        WriteCardList(w, cards, ids);
        w.WriteEndArray();
        return ids.ToArray();
    }

    private void WriteCardList(Utf8JsonWriter w, IEnumerable<CardModel> cards, List<int> ids)
    {
        foreach (CardModel c in cards)
        {
            ids.Add(Cards.Id(c));
            w.WriteStartObject();
            w.WriteNumber("id", Cards.Id(c));
            w.WriteNumber("rarity", (int)c.Rarity);
            w.WriteEndObject();
        }
    }

    // The option tables of Neow.GenerateInitialOptions, read off a Neow owned by a real player so
    // IsAllowedAtNeow can be asked of each relic, and the pools the relics draw from when they
    // are obtained. Which curse removes which positive option is code in that method and is
    // restated here.
    private void WriteNeow(Utf8JsonWriter w, SeedSimulator.Run run, List<RelicModel> playerBag)
    {
        Player player = run.Player;
        Neow canonical = ModelDb.AncientEvent<Neow>();
        if (!SlotActs[0].Any(a => ActInfos[a].Ancients.Contains(Ancients.Id(canonical))))
        {
            w.WriteNull("neow");
            return;
        }

        NeowId = Ancients.Id(canonical);
        EventModel neow = canonical.ToMutable();
        SeedSimulator.OwnerSetter.Invoke(neow, [player]);

        List<int> disallowed = [];
        int Register(EventOption option)
        {
            RelicModel relic = option.Relic ?? throw new InvalidOperationException("A Neow option has no relic.");
            int id = Relics.Id(relic);
            if (!relic.IsAllowedAtNeow(player) && !disallowed.Contains(id))
                disallowed.Add(id);
            return id;
        }

        object Get(string property) => (AccessTools.PropertyGetter(typeof(Neow), property) ?? throw new MissingMemberException(nameof(Neow), property)).Invoke(neow, null)!;
        int[] Many(string property) => ((IEnumerable<EventOption>)Get(property)).Select(Register).ToArray();
        int One(string property) => Register((EventOption)Get(property));
        int Relic<T>() where T : RelicModel => Relics.Id(ModelDb.Relic<T>());

        int[] curse = Many("CurseOptions");
        int[] positive = Many("PositiveOptions");
        int[] extras =
        [
            One("LavaRockOption"), One("SmallCapsuleOption"), One("NutritiousOysterOption"),
            One("StoneHumidifierOption"), One("NeowsTalismanOption"), One("PomanderOption")
        ];
        NeowRelics = curse.Concat(positive).Concat(extras).Where(r => !disallowed.Contains(r)).Distinct().ToArray();
        NeowCurses = curse;

        w.WriteStartObject("neow");
        w.WriteString("entry", canonical.Id.Entry);
        w.WriteNumber("ancient", NeowId);
        w.WriteInts("curse", curse);
        w.WriteInts("positive", positive);
        w.WriteNumber("lava_rock", extras[0]);
        w.WriteNumber("small_capsule", extras[1]);
        w.WriteNumber("oyster", extras[2]);
        w.WriteNumber("humidifier", extras[3]);
        w.WriteNumber("talisman", extras[4]);
        w.WriteNumber("pomander", extras[5]);
        w.WriteNumber("large_capsule", Relic<LargeCapsule>());
        w.WriteStartArray("exclusions");
        w.WriteInts([Relic<CursedPearl>(), Relic<GoldenPearl>()]);
        w.WriteInts([Relic<HeftyTablet>(), Relic<ArcaneScroll>()]);
        w.WriteInts([Relic<LeafyPoultice>(), Relic<NewLeaf>()]);
        w.WriteInts([Relic<PrecariousShears>(), Relic<PreciseScissors>()]);
        w.WriteInts([Relic<NeowsSacrifice>(), Relic<PhialHolster>()]);
        w.WriteInts([Relic<NeowsSacrifice>(), Relic<LostCoffer>()]);
        w.WriteEndArray();
        w.WriteInts("disallowed", disallowed);

        w.WriteStartArray("gives");
        foreach (int id in NeowRelics)
        {
            if (NeowGiveOf(Relics[id]) is not (string kind, int count))
                continue;
            w.WriteStartObject();
            w.WriteNumber("relic", id);
            w.WriteString("kind", kind);
            w.WriteNumber("count", count);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        CardMultiplayerConstraint constraint = player.RunState.CardMultiplayerConstraint;
        IEnumerable<CardModel> Unlocked(CardPoolModel pool) =>
            pool.GetUnlockedCards(run.Unlocks, constraint).Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly);

        ColorlessCards = WriteCards(w, "colorless", Unlocked(ModelDb.CardPool<ColorlessCardPool>()));

        // Kaleidoscope shuffles these with StableShuffle, which sorts them first.
        List<CardPoolModel> others = run.Unlocks.CharacterCardPools.Where(p => p != Character.CardPool).ToList();
        others.Sort();
        List<int> otherCards = [];
        w.WriteStartArray("other_pools");
        foreach (CardPoolModel pool in others)
        {
            w.WriteStartArray();
            WriteCardList(w, Unlocked(pool), otherCards);
            w.WriteEndArray();
        }

        w.WriteEndArray();
        OtherCharacterCards = otherCards.ToArray();

        // NeowsBones.AfterObtained
        Curses = ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(run.Unlocks, constraint)
            .Where(c => c.CanBeGeneratedByModifiers)
            .OrderBy(c => c.Id)
            .Select(c => Cards.Id(c))
            .ToArray();
        w.WriteInts("curses", Curses);
        BonesRelics = SeedSimulator.BonesRelics(player).Select(r => Relics.Id(r)).ToArray();
        w.WriteInts("bones", BonesRelics);

        if (Character is Defect)
            w.WriteNumber("claw", Cards.Id(ModelDb.Card<Claw>()));
        else
            w.WriteNull("claw");
        w.WriteInts("bag_disallowed", playerBag.Where(r => !r.IsAllowed(run.State)).Select(r => Relics.Id(r)));
        w.WriteEndObject();
    }
}