using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using MegaCrit.Sts2.Core.Models.Enchantments;
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

    // The id of the model with this ModelId entry, for filters that were saved under other data.
    public int? Find(string entry)
    {
        int id = _items.FindIndex(m => m.Id.Entry == entry);
        return id < 0 ? null : id;
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
    // The act opens with this many weak fights; the regular ones come after them.
    public int WeakCount;
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
    private static readonly Regex BbTagRegex = new(@"\[/?[^\]]+\]", RegexOptions.Compiled);

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
    // The cards of the rarities the first card reward can roll.
    public int[] FirstRewardCards { get; private set; } = [];
    // The shared bag relics of the rarities that are ever dealt.
    public int[] SharedBagDealt { get; private set; } = [];
    // Relics of the shared bag that are skipped when one is pulled.
    public int[] SharedBagDisallowed { get; private set; } = [];
    // Relics of the player bag that are skipped when one is pulled.
    public int[] BagDisallowed { get; private set; } = [];
    // Relics of the player bag that are skipped in the merchant shop.
    public int[] ShopDisallowed { get; private set; } = [];
    // Relics that can appear in Treasure Chests.
    public int[] TreasureRelicOptions { get; private set; } = [];
    // Relics that can appear in the First Shop.
    public int[] ShopRelicOptions { get; private set; } = [];
    // Cards (character + colorless) that can appear in the First Shop.
    public int[] ShopCardOptions { get; private set; } = [];
    // Pairs of Neow relics that are never offered together.
    public int[][] NeowNeverTogether { get; private set; } = [];
    public int[] PotionOptions { get; private set; } = [];
    public int[] ColorlessCards { get; private set; } = [];
    public int[] OtherCharacterCards { get; private set; } = [];
    private readonly Dictionary<int, string> _cardOwners = new();
    private readonly Dictionary<(int Ancient, int Slot), int[]> _ancientRelicsBySlot = new();
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

    // The character a card of another character belongs to; null for every other card.
    public string? CardOwner(int id) => _cardOwners.GetValueOrDefault(id);
    public string PotionName(int id) => id < 0 ? "none" : Potions[id].Title.GetFormattedText();

    public string RelicTooltip(int id)
    {
        if (id < 0 || id >= Relics.Count)
            return "";
        try
        {
            RelicModel r = Relics[id];
            string desc = StripBbCode(r.DynamicDescription.GetFormattedText());
            return $"{r.Title.GetFormattedText()} ({r.Rarity})\n{desc}".Trim();
        }
        catch
        {
            return RelicName(id);
        }
    }

    public string CardTooltip(int id)
    {
        if (id < 0 || id >= Cards.Count)
            return "";
        try
        {
            CardModel c = Cards[id];
            string desc = StripBbCode(c.GetDescriptionForPile(PileType.None));
            string owner = _cardOwners.TryGetValue(id, out string? o) ? $" · {o}" : "";
            return $"{c.Title} ({c.Rarity} {c.Type}{owner})\n{desc}".Trim();
        }
        catch
        {
            return CardName(id);
        }
    }

    public string PotionTooltip(int id)
    {
        if (id < 0 || id >= Potions.Count)
            return "";
        try
        {
            PotionModel p = Potions[id];
            string desc = StripBbCode(p.DynamicDescription.GetFormattedText());
            return $"{p.Title.GetFormattedText()} ({p.Rarity})\n{desc}".Trim();
        }
        catch
        {
            return PotionName(id);
        }
    }

    public string ItemTooltip(GiveItem what, int id) => what switch
    {
        GiveItem.Card => CardTooltip(id),
        GiveItem.Relic => RelicTooltip(id),
        _ => PotionTooltip(id)
    };

    private static string StripBbCode(string text) =>
        BbTagRegex.Replace(text.Replace("[img]res://images/packed/sprite_fonts/star_icon.png[/img]", "★"), "").Trim();

    // All relics that any Ancient in act `slot` (or a specific `ancientId`) can offer.
    public int[] AncientRelicOptions(int slot, int? ancientId = null)
    {
        if (slot == 0)
            return NeowRelics;
        IEnumerable<int> ancients = ancientId.HasValue
            ? [ancientId.Value]
            : SlotActs[slot].SelectMany(a => ActInfos[a].Ancients).Concat(SharedAncients).Distinct();
        return ancients
            .SelectMany(a => _ancientRelicsBySlot.GetValueOrDefault((a, slot), []))
            .Distinct()
            .ToArray();
    }

    // Which Ancient offers a given relic in Act 2/3, for labelling in pickers.
    public string? AncientOwnerOfRelic(int slot, int relicId)
    {
        if (slot == 0)
            return NeowId >= 0 ? AncientName(NeowId) : null;
        IEnumerable<int> ancients = SlotActs[slot].SelectMany(a => ActInfos[a].Ancients).Concat(SharedAncients).Distinct();
        foreach (int anc in ancients)
        {
            if (_ancientRelicsBySlot.TryGetValue((anc, slot), out int[]? pool) && pool.Contains(relicId))
                return AncientName(anc);
        }

        return null;
    }

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
            SharedBagDealt = SharedBag.Where(r => BagRarities.Contains(Relics[r].Rarity)).ToArray();
            PlayerBag = WriteBag(w, "player_bag", mine);

            WriteNeow(w, run, mine);

            List<CardModel> cards = CardCreationOptions.ForRoom(player, RoomType.Monster).GetPossibleCards(player)
                .Where(c => c.MultiplayerConstraint != CardMultiplayerConstraint.MultiplayerOnly)
                .ToList();
            RewardCards = WriteCards(w, "cards", cards);

            // CardRarityOdds.Roll: a rare needs a roll under the rare odds plus the pity offset,
            // which starts below zero and grows with every card that is not rare.
            CardRarityOdds rarityOdds = player.PlayerOdds.CardRarity;
            float offset = rarityOdds.CurrentValue;
            bool rare = false;
            for (int i = 0; i < 3; i++)
            {
                rare |= CardRarityOdds.RegularRareOdds + offset > 0f;
                offset += rarityOdds.RarityGrowth;
            }

            FirstRewardCards = RewardCards.Where(c => Cards[c].Rarity is CardRarity.Common or CardRarity.Uncommon || (rare && Cards[c].Rarity == CardRarity.Rare)).ToArray();

            List<PotionModel> potions = PotionFactory.GetPotionOptions(player).ToList();
            PotionOptions = potions.Select(p => Potions.Id(p)).ToArray();
            w.WriteStartArray("potions");
            foreach (PotionModel p in potions)
                w.WriteInts([Potions.Id(p), (int)p.Rarity]);
            w.WriteEndArray();

            w.WriteNumber("rare_odds", CardRarityOdds.RegularRareOdds);
            w.WriteNumber("uncommon_odds", CardRarityOdds.regularUncommonOdds);
            w.WriteNumber("rarity_growth", rarityOdds.RarityGrowth);

            MapPointTypeCounts counts = new(0, 0);
            w.WriteNumber("map_elites", counts.NumOfElites);
            w.WriteNumber("map_shops", counts.NumOfShops);

            DoubleBoss = SeedSimulator.AscensionOverride!.HasLevel(AscensionLevel.DoubleBoss);
            w.WriteBoolean("double_boss", DoubleBoss);

            WriteAncientDefs(w, run);

            SharedBagDisallowed = shared.Where(r => !r.IsAllowed(run.State)).Select(r => Relics.Id(r)).ToArray();
            w.WriteInts("shared_bag_disallowed", SharedBagDisallowed);
            TreasureRelicOptions = SharedBagDealt
                .Where(r => Relics[r].Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare && !SharedBagDisallowed.Contains(r))
                .ToArray();

            ShopDisallowed = mine.Where(r => !r.IsAllowed(run.State) || !r.IsAllowedInShops).Select(r => Relics.Id(r)).ToArray();
            w.WriteInts("shop_disallowed", ShopDisallowed);
            ShopRelicOptions = PlayerBag.Where(r => !ShopDisallowed.Contains(r)).ToArray();

            CardMultiplayerConstraint constraint = player.RunState.CardMultiplayerConstraint;
            List<CardModel> shopCards = Character.CardPool.GetUnlockedCards(unlocks, constraint)
                .Where(c => c.Rarity != CardRarity.Basic && c.IsAllowedInRun(run.State))
                .ToList();
            int[] shopCharIds = WriteCards(w, "shop_cards", shopCards);

            List<CardModel> shopColorless = ModelDb.CardPool<ColorlessCardPool>().GetUnlockedCards(unlocks, constraint)
                .Where(c => c.Rarity != CardRarity.Basic && c.IsAllowedInRun(run.State))
                .ToList();
            int[] shopColorlessIds = WriteCards(w, "shop_colorless", shopColorless);

            ShopCardOptions = shopCharIds
                .Concat(shopColorlessIds.Where(id => Cards[id].Rarity is CardRarity.Uncommon or CardRarity.Rare))
                .Distinct()
                .ToArray();

            w.WriteNumber("shop_rare_odds", CardRarityOdds.ShopRareOdds);
            w.WriteNumber("shop_uncommon_odds", CardRarityOdds.shopUncommonOdds);

            w.WriteEndObject();
        }

        SnapshotJson = Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteAncientDefs(Utf8JsonWriter w, SeedSimulator.Run run)
    {
        Player player = run.Player;
        int[] allAncients = Enumerable.Range(0, Ancients.Count).ToArray();
        int RelicId<T>() where T : RelicModel => Relics.Id(ModelDb.Relic<T>());

        IEnumerable<EventOption> PropPool(AncientEventModel ev, string prop) =>
            (IEnumerable<EventOption>)(AccessTools.PropertyGetter(ev.GetType(), prop) ?? throw new MissingMemberException(ev.GetType().Name, prop)).Invoke(ev, null)!;
        EventOption PropOption(AncientEventModel ev, string prop) =>
            (EventOption)(AccessTools.PropertyGetter(ev.GetType(), prop) ?? throw new MissingMemberException(ev.GetType().Name, prop)).Invoke(ev, null)!;
        int[] AllowedIds(IEnumerable<EventOption> opts) =>
            opts.Where(o => o.Relic != null && o.Relic.IsAllowed(run.State)).Select(o => Relics.Id(o.Relic!)).ToArray();

        w.WriteStartArray("ancient_defs");
        foreach (int ancId in allAncients)
        {
            AncientEventModel canonical = Ancients[ancId];
            AncientEventModel ev = (AncientEventModel)canonical.ToMutable();
            SeedSimulator.OwnerSetter.Invoke(ev, [player]);

            w.WriteStartObject();
            w.WriteNumber("id", ancId);
            w.WriteString("entry", canonical.Id.Entry);

            switch (ev)
            {
                case Neow:
                    w.WriteString("kind", "neow");
                    break;
                case Orobas:
                {
                    int otherCharCount = run.Unlocks.Characters.Count(c => c.Id != Character.Id);
                    int gem = RelicId<PrismaticGem>();
                    int glass = RelicId<SeaGlass>();
                    int[] p1 = AllowedIds(PropPool(ev, "OptionPool1"));
                    int[] p2 = AllowedIds(PropPool(ev, "OptionPool2"));
                    int[] p3 = AllowedIds(PropPool(ev, "OptionPool3"));
                    w.WriteString("kind", "orobas");
                    w.WriteNumber("other_char_count", otherCharCount);
                    w.WriteNumber("prismatic_gem", gem);
                    w.WriteNumber("sea_glass", glass);
                    w.WriteInts("pool1", p1);
                    w.WriteInts("pool2", p2);
                    w.WriteInts("pool3", p3);
                    int[] all = [.. p1, gem, glass, .. p2, .. p3];
                    _ancientRelicsBySlot[(ancId, 1)] = all.Distinct().ToArray();
                    _ancientRelicsBySlot[(ancId, 2)] = all.Distinct().ToArray();
                    break;
                }
                case Pael:
                {
                    int[] p1 = AllowedIds(PropPool(ev, "OptionPool1"));
                    List<EventOption> pool2 = PropPool(ev, "OptionPool2").ToList();
                    IReadOnlyList<CardModel> deck = player.Deck.Cards;
                    if (deck.Count(c => ModelDb.Enchantment<Goopy>().CanEnchant(c)) >= 3)
                        pool2.Add(PropOption(ev, "PaelsClawOption"));
                    if (deck.Count(c => c.IsRemovable) >= 5)
                        pool2.Add(PropOption(ev, "PaelsToothOption"));
                    pool2.AddRange(pool2.ToList());
                    pool2.Add(PropOption(ev, "PaelsGrowthOption"));
                    int[] p2 = AllowedIds(pool2);

                    List<EventOption> pool3 = PropPool(ev, "OptionPool3").ToList();
                    if (!player.HasEventPet())
                        pool3.Add(PropOption(ev, "PaelsLegionOption"));
                    int[] p3 = AllowedIds(pool3);

                    w.WriteString("kind", "three_pools");
                    w.WriteInts("pool1", p1);
                    w.WriteInts("pool2", p2);
                    w.WriteInts("pool3", p3);
                    int[] all = [.. p1, .. p2, .. p3];
                    _ancientRelicsBySlot[(ancId, 1)] = all.Distinct().ToArray();
                    _ancientRelicsBySlot[(ancId, 2)] = all.Distinct().ToArray();
                    break;
                }
                case Tezcatara:
                {
                    List<EventOption> pool1 = PropPool(ev, "OptionPool1").ToList();
                    if (player.Deck.Cards.Any(c => c.Tags.Contains(CardTag.Strike) && c.Rarity == CardRarity.Basic))
                        pool1.Add(PropOption(ev, "NutritiousSoupOption"));
                    int[] p1 = AllowedIds(pool1);
                    int[] p2 = AllowedIds(PropPool(ev, "OptionPool2"));
                    int[] p3 = AllowedIds(PropPool(ev, "OptionPool3"));

                    w.WriteString("kind", "three_pools");
                    w.WriteInts("pool1", p1);
                    w.WriteInts("pool2", p2);
                    w.WriteInts("pool3", p3);
                    int[] all = [.. p1, .. p2, .. p3];
                    _ancientRelicsBySlot[(ancId, 1)] = all.Distinct().ToArray();
                    _ancientRelicsBySlot[(ancId, 2)] = all.Distinct().ToArray();
                    break;
                }
                case Vakuu:
                {
                    int[] p1 = AllowedIds(PropPool(ev, "Pool1"));
                    int[] p2 = AllowedIds(PropPool(ev, "Pool2"));
                    int[] p3 = AllowedIds(PropPool(ev, "Pool3"));

                    w.WriteString("kind", "three_pools");
                    w.WriteInts("pool1", p1);
                    w.WriteInts("pool2", p2);
                    w.WriteInts("pool3", p3);
                    int[] all = [.. p1, .. p2, .. p3];
                    _ancientRelicsBySlot[(ancId, 1)] = all.Distinct().ToArray();
                    _ancientRelicsBySlot[(ancId, 2)] = all.Distinct().ToArray();
                    break;
                }
                case Nonupeipe:
                {
                    List<EventOption> pool1 = PropPool(ev, "OptionPool").ToList();
                    if (player.Deck.Cards.Count(ModelDb.Enchantment<Swift>().CanEnchant) >= 4)
                        pool1.Add(PropOption(ev, "BeautifulBraceletEventOption"));
                    int[] p1 = AllowedIds(pool1);

                    w.WriteString("kind", "shuffle_three");
                    w.WriteInts("pool1", p1);
                    _ancientRelicsBySlot[(ancId, 1)] = p1.Distinct().ToArray();
                    _ancientRelicsBySlot[(ancId, 2)] = p1.Distinct().ToArray();
                    break;
                }
                case Tanx:
                {
                    List<EventOption> pool1 = PropPool(ev, "BaseOptionPool").ToList();
                    if (player.Deck.Cards.Count(c => ModelDb.Enchantment<Instinct>().CanEnchant(c)) >= 3)
                        pool1.Add(PropOption(ev, "TriBoomerangOption"));
                    int[] p1 = AllowedIds(pool1);

                    w.WriteString("kind", "shuffle_three");
                    w.WriteInts("pool1", p1);
                    _ancientRelicsBySlot[(ancId, 1)] = p1.Distinct().ToArray();
                    _ancientRelicsBySlot[(ancId, 2)] = p1.Distinct().ToArray();
                    break;
                }
                case Darv:
                {
                    int[] act2 =
                    [
                        RelicId<Astrolabe>(), RelicId<BlackStar>(), RelicId<CallingBell>(), RelicId<EmptyCage>(),
                        RelicId<PandorasBox>(), RelicId<RunicPyramid>(), RelicId<SneckoEye>(),
                        RelicId<Ectoplasm>(), RelicId<Sozu>(), RelicId<PhilosophersStone>(), RelicId<VelvetChoker>()
                    ];
                    int[] act3 =
                    [
                        RelicId<Astrolabe>(), RelicId<BlackStar>(), RelicId<CallingBell>(), RelicId<EmptyCage>(),
                        RelicId<PandorasBox>(), RelicId<RunicPyramid>(), RelicId<SneckoEye>(),
                        RelicId<PhilosophersStone>(), RelicId<VelvetChoker>()
                    ];
                    int tome = RelicId<DustyTome>();
                    w.WriteString("kind", "darv");
                    w.WriteInts("pool1", act2);
                    w.WriteInts("pool2", act3);
                    w.WriteNumber("dusty_tome", tome);
                    _ancientRelicsBySlot[(ancId, 1)] = [.. act2, tome];
                    _ancientRelicsBySlot[(ancId, 2)] = [.. act3, tome];
                    break;
                }
                default:
                    w.WriteString("kind", "other");
                    break;
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
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
            Rooms = act.GetNumberOfRooms(false),
            WeakCount = (int)AccessTools.PropertyGetter(act.GetType(), "NumberOfWeakEncounters").Invoke(act, null)!
        };

        w.WriteStartObject();
        w.WriteInts("events", info.Events);
        w.WriteInts("weak", info.Weak);
        w.WriteInts("regular", info.Regular);
        w.WriteInts("elite", info.Elite);
        w.WriteInts("boss", info.Boss);
        w.WriteInts("ancients", info.Ancients);
        w.WriteNumber("rooms", info.Rooms);
        w.WriteNumber("weak_count", info.WeakCount);
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
    // Card rewards only ever roll a common, uncommon or rare card, and a relic pull skips the
    // relics that are not allowed.
    public (int[] Cards, int[] Relics, int[] Potions) NeowGiveOptions(int relic)
    {
        int[] Rolled(int[] cards) => cards.Where(c => Cards[c].Rarity is CardRarity.Common or CardRarity.Uncommon or CardRarity.Rare).ToArray();
        int[] OfRarity(params CardRarity[] rarities) => RewardCards.Where(c => rarities.Contains(Cards[c].Rarity)).ToArray();
        return Relics[relic] switch
        {
            ArcaneScroll or HeftyTablet => (OfRarity(CardRarity.Rare), [], []),
            LeadPaperweight => (Rolled(ColorlessCards), [], []),
            LostCoffer => (Rolled(RewardCards), [], PotionOptions),
            ScrollBoxes => (OfRarity(CardRarity.Common, CardRarity.Uncommon), [], []),
            SmallCapsule or LargeCapsule =>
                ([], PlayerBag.Where(r => Relics[r].Rarity is RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare && !BagDisallowed.Contains(r)).ToArray(), []),
            NeowsBones => (Curses, BonesRelics, []),
            Kaleidoscope => (Rolled(OtherCharacterCards), [], []),
            PhialHolster => ([], [], PotionOptions),
            LeafyPoultice or NewLeaf => (Rolled(RewardCards), [], []),
            _ => ([], [], [])
        };
    }

    public int[] NeowGiveItems(int relic, GiveItem what)
    {
        (int[] cards, int[] relics, int[] potions) = NeowGiveOptions(relic);
        return what switch
        {
            GiveItem.Card => cards,
            GiveItem.Relic => relics,
            _ => potions
        };
    }

    // How many different cards, relics and potions a Neow relic hands out at once.
    public (int Cards, int Relics, int Potions) NeowGiveCapacity(int relic)
    {
        if (NeowGiveOf(Relics[relic]) is not (_, int count))
            return (0, 0, 0);
        return Relics[relic] switch
        {
            LostCoffer => (count, 0, 1),
            ScrollBoxes or Kaleidoscope => (count * 3, 0, 0),
            SmallCapsule or LargeCapsule => (0, count, 0),
            NeowsBones => (1, count, 0),
            PhialHolster => (0, 0, count),
            _ => (count, 0, 0)
        };
    }

    // The fights that can be among the first `within` of an act slot. Every act opens with its
    // weak fights, so a regular one cannot come before those are through.
    public IEnumerable<int> FightOptions(int slot, bool elite, int within) =>
        SlotActs[slot]
            .Select(a => ActInfos[a])
            .SelectMany(a => elite ? a.Elite : within > a.WeakCount ? a.Weak.Concat(a.Regular) : a.Weak)
            .Distinct();

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
            w.WriteNumber("card_type", (int)c.Type);
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
        _ancientRelicsBySlot[(NeowId, 0)] = NeowRelics;

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
        int[][] exclusions =
        [
            [Relic<CursedPearl>(), Relic<GoldenPearl>()],
            [Relic<HeftyTablet>(), Relic<ArcaneScroll>()],
            [Relic<LeafyPoultice>(), Relic<NewLeaf>()],
            [Relic<PrecariousShears>(), Relic<PreciseScissors>()],
            [Relic<NeowsSacrifice>(), Relic<PhialHolster>()],
            [Relic<NeowsSacrifice>(), Relic<LostCoffer>()]
        ];
        w.WriteIntLists("exclusions", exclusions);
        // The Large Capsule curse skips the Lava Rock or Small Capsule roll, and each of the three
        // rolls adds one relic or the other.
        NeowNeverTogether =
        [
            .. exclusions,
            [Relic<LargeCapsule>(), extras[0]], [Relic<LargeCapsule>(), extras[1]],
            [extras[0], extras[1]], [extras[2], extras[3]], [extras[4], extras[5]]
        ];
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
            int first = otherCards.Count;
            WriteCardList(w, Unlocked(pool), otherCards);
            w.WriteEndArray();
            string owner = ModelDb.AllCharacters.FirstOrDefault(c => c.CardPool == pool)?.Title.GetFormattedText() ?? "Other";
            foreach (int card in otherCards.Skip(first))
                _cardOwners[card] = owner;
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
        BagDisallowed = playerBag.Where(r => !r.IsAllowed(run.State)).Select(r => Relics.Id(r)).ToArray();
        w.WriteInts("bag_disallowed", BagDisallowed);
        w.WriteStartArray("bag_shuffles");
        foreach (RelicModel r in playerBag)
        {
            if (SeedSimulator.PickupUpgradeChoices(player, r) is { } choices)
                w.WriteInts([Relics.Id(r), choices.Count]);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}
