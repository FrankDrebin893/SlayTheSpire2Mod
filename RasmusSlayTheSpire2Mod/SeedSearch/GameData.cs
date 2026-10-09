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

            WriteNeow(w, player);

            List<CardModel> cards = CardCreationOptions.ForRoom(player, RoomType.Monster).GetPossibleCards(player)
                .Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
                .ToList();
            RewardCards = cards.Select(c => Cards.Id(c)).ToArray();
            w.WriteStartArray("cards");
            foreach (CardModel c in cards)
            {
                w.WriteStartObject();
                w.WriteNumber("id", Cards.Id(c));
                w.WriteNumber("rarity", (int)c.Rarity);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("potions");
            foreach (PotionModel p in PotionFactory.GetPotionOptions(player))
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

    // The option tables of Neow.GenerateInitialOptions, read off a Neow owned by a real player so
    // IsAllowedAtNeow can be asked of each relic. Which curse removes which positive option is
    // code in that method and is restated here.
    private void WriteNeow(Utf8JsonWriter w, Player player)
    {
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
        w.WriteEndObject();
    }
}
