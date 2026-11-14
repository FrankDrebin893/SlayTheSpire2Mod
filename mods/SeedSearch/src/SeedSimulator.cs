using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Odds;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

[Flags]
public enum SimParts
{
    None = 0,
    Reward = 1,
    Maps = 2,
    NeowOutcomes = 4,
    All = Reward | Maps | NeowOutcomes
}

// Builds a detached RunState for a seed and runs the game's own start-of-run generation on it,
// mirroring NGame.StartNewSingleplayerRun, RunManager.InitializeNewRun and RunManager.GenerateRooms
// without ever handing the state to RunManager. The Rust engine reimplements the same sequence;
// this is the reference it is checked against, and the slow fallback when it is unavailable.
public static class SeedSimulator
{
    public static readonly FieldInfo RoomsField = AccessTools.Field(typeof(ActModel), "_rooms");
    public static readonly MethodInfo OwnerSetter = AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Owner));
    private static readonly MethodInfo RngSetter = AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Rng));
    private static readonly MethodInfo InitialOptions = AccessTools.Method(typeof(EventModel), "GenerateInitialOptionsWrapper");
    private static readonly FieldInfo DequesField = AccessTools.Field(typeof(RelicGrabBag), "_deques");
    private static readonly MethodInfo BonesValidRelics = AccessTools.Method(typeof(NeowsBones), "GetValidRelics");

    // Non-null only while a simulation runs. SimulationPatches reads it so that ascension checks,
    // which normally ask the RunManager singleton, see the ascension being previewed.
    public static AscensionManager? AscensionOverride { get; private set; }

    public static bool IsSimulating => AscensionOverride != null;

    private static bool _loggedFailure;

    public sealed record Run(RunState State, Player Player, UnlockState Unlocks);

    // Runs body while ascension checks answer for the given level.
    public static T WithAscension<T>(int ascension, Func<T> body)
    {
        // CardRarityOdds caches an ascension-dependent value in a static field. Make sure it is
        // initialised before the override is active, so a preview can never leak into real runs.
        RuntimeHelpers.RunClassConstructor(typeof(CardRarityOdds).TypeHandle);

        AscensionManager? previous = AscensionOverride;
        AscensionOverride = new AscensionManager(GameMode.Standard, ascension);
        try
        {
            return body();
        }
        finally
        {
            AscensionOverride = previous;
        }
    }

    public static SimResult? Simulate(GameData data, string rawSeed, SimParts parts)
    {
        string seed = SeedHelper.CanonicalizeSeed(rawSeed);
        try
        {
            return WithAscension(data.Ascension, () => SimulateInternal(data, seed, parts));
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                MainFile.Logger.Error($"Seed search could not simulate seed {seed}. The game probably updated. {e}");
            }

            return null;
        }
    }

    // Everything up to the point where generation starts. Call inside WithAscension.
    public static Run CreateRun(string seed, CharacterModel character, int ascension, string act1Key)
    {
        UnlockState unlocks = SaveManager.Instance.GenerateUnlockStateFromProgress();

        // StartRunLobby.BeginRunLocally
        Rng actRng = new(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        List<ActModel> acts = ActModel.GetRandomList(actRng, unlocks, false).ToList();
        acts[0] = act1Key switch
        {
            "overgrowth" => ModelDb.Act<Overgrowth>(),
            "underdocks" => ModelDb.Act<Underdocks>(),
            _ => acts[0]
        };

        // NGame.StartNewSingleplayerRun
        Player player = Player.CreateForNewRun(character, unlocks, 1uL);
        RunState state = RunState.CreateForNewRun(
            new[] { player },
            acts.Select(a => a.ToMutable()).ToList(),
            Array.Empty<ModifierModel>(),
            GameMode.Standard,
            false,
            ascension,
            seed);
        return new Run(state, player, unlocks);
    }

    // CreateRun followed by RunManager.InitializeNewRun.
    private static Run StartRun(GameData data, string seed)
    {
        Run run = CreateRun(seed, data.Character, data.Ascension, data.Act1Key);
        Rng upFront = run.State.Rng.UpFront;
        run.State.SharedRelicGrabBag.Populate(ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(run.State.UnlockState), upFront);
        run.Player.PopulateRelicGrabBagIfNecessary(upFront);
        AscensionOverride!.ApplyEffectsTo(run.Player);
        return run;
    }

    private static SimResult SimulateInternal(GameData data, string seed, SimParts parts)
    {
        (RunState state, Player player, _) = StartRun(data, seed);
        Rng upFront = state.Rng.UpFront;

        // RunManager.GenerateRooms
        List<AncientEventModel> sharedAncients = state.UnlockState.SharedAncients.ToList().UnstableShuffle(upFront);
        foreach (ActModel act in state.Acts.Skip(1))
        {
            int count = upFront.NextInt(sharedAncients.Count + 1);
            List<AncientEventModel> subset = sharedAncients.Take(count).ToList();
            sharedAncients = sharedAncients.Except(subset).ToList();
            act.SetSharedAncientSubset(subset);
        }

        for (int i = 0; i < state.Acts.Count; i++)
        {
            ActModel act = state.Acts[i];
            act.GenerateRooms(upFront, state.UnlockState, false);
            if (i == state.Acts.Count - 1 && AscensionOverride!.HasLevel(AscensionLevel.DoubleBoss))
            {
                act.SetSecondBossEncounter(upFront.NextItem(act.AllBossEncounters.Where(e => e.Id != act.BossEncounter.Id)));
            }
        }

        List<RoomSet> rooms = state.Acts.Select(a => (RoomSet)RoomsField.GetValue(a)!).ToList();
        EncounterModel? secondBoss = state.Acts[^1].SecondBossEncounter;
        AncientEventModel? act1Ancient = rooms[0].HasAncient ? rooms[0].Ancient : null;

        List<RelicDeque> sharedRelics = Deques(data, state.SharedRelicGrabBag);
        List<RelicDeque> playerRelics = Deques(data, player.RelicGrabBag);
        int[] treasureRelics = BuildTreasureRelics(data, state);
        ShopResult firstShop = BuildFirstShop(data, player);

        SimResult result = new()
        {
            Seed = seed,
            Acts = state.Acts.Select(a => data.Acts.Id(a)).ToArray(),
            Bosses = rooms.Select(r => data.Encounters.Id(r.Boss)).ToArray(),
            SecondBoss = secondBoss == null ? -1 : data.Encounters.Id(secondBoss),
            Ancients = rooms.Select(r => r.HasAncient ? data.Ancients.Id(r.Ancient) : -1).ToArray(),
            Events = rooms.Select(r => r.events.Select(e => data.Events.Id(e)).ToArray()).ToArray(),
            Normals = rooms.Select(r => r.normalEncounters.Select(e => data.Encounters.Id(e)).ToArray()).ToArray(),
            Elites = rooms.Select(r => r.eliteEncounters.Select(e => data.Encounters.Id(e)).ToArray()).ToArray(),
            SharedRelics = sharedRelics,
            PlayerRelics = playerRelics,
            TreasureRelics = treasureRelics,
            FirstShop = firstShop,
            Neow = act1Ancient != null && data.Ancients.Id(act1Ancient) == data.NeowId ? GetAncientOffers(data, act1Ancient, player, state) : [],
            Maps = state.Acts.Select((a, i) => parts.HasFlag(SimParts.Maps) ? BuildMap(state, a, i) : null).ToArray()
        };

        if (parts.HasFlag(SimParts.NeowOutcomes))
            result.NeowOutcomes = result.Neow.Select(relic => NeowOutcome(data, seed, relic, rooms[0])).ToArray();

        // Advance the player's rewards stream for the baseline Floor 1 combat reward before
        // Act 2/3 Ancients (since Darv's DustyTome setup also draws from PlayerRng.Rewards).
        if (parts.HasFlag(SimParts.Reward))
            AddFirstReward(data, result, player, rooms[0]);

        int[][] ancientRelics = new int[rooms.Count][];
        ancientRelics[0] = result.Neow;
        for (int i = 1; i < rooms.Count; i++)
        {
            state.CurrentActIndex = i;
            ancientRelics[i] = rooms[i].HasAncient ? GetAncientOffers(data, rooms[i].Ancient, player, state) : [];
        }

        state.CurrentActIndex = 0;
        result.AncientRelics = ancientRelics;
        return result;
    }

    private static int[] BuildTreasureRelics(GameData data, RunState state, int count = 6)
    {
        Rng rng = state.Rng.TreasureRoomRelics;
        List<int> relics = new(count);
        for (int i = 0; i < count; i++)
        {
            RelicRarity rarity = RelicFactory.RollRarity(rng);
            RelicModel? pulled = state.SharedRelicGrabBag.PullFromFront(rarity, state);
            if (pulled != null)
                relics.Add(data.Relics.Id(pulled));
        }

        return relics.ToArray();
    }

    private static ShopResult BuildFirstShop(GameData data, Player player)
    {
        MerchantInventory inv = MerchantInventory.CreateForNormalMerchant(player);
        int saleIndex = 0;
        for (int i = 0; i < inv.CharacterCardEntries.Count; i++)
        {
            if (inv.CharacterCardEntries[i].IsOnSale)
            {
                saleIndex = i;
                break;
            }
        }

        return new ShopResult
        {
            Cards = inv.CharacterCardEntries.Where(e => e.CreationResult != null).Select(e => data.Cards.Id(e.CreationResult!.Card)).ToArray(),
            SaleIndex = saleIndex,
            Colorless = inv.ColorlessCardEntries.Where(e => e.CreationResult != null).Select(e => data.Cards.Id(e.CreationResult!.Card)).ToArray(),
            Relics = inv.RelicEntries.Where(e => e.Model != null).Select(e => data.Relics.Id(e.Model!)).ToArray(),
            Potions = inv.PotionEntries.Where(e => e.Model != null).Select(e => data.Potions.Id(e.Model!)).ToArray()
        };
    }

    private static List<RelicDeque> Deques(GameData data, RelicGrabBag bag)
    {
        var deques = (Dictionary<RelicRarity, List<RelicModel>>)DequesField.GetValue(bag)!;
        return deques.Select(d => new RelicDeque { Rarity = (int)d.Key, Relics = d.Value.Select(r => data.Relics.Id(r)).ToArray() }).ToList();
    }

    // EventModel.BeginEvent, minus everything that touches the scene.
    private static int[] GetAncientOffers(GameData data, AncientEventModel ancient, Player player, RunState state)
    {
        EventModel ev = ancient.ToMutable();
        ulong slot = ev.IsShared ? 0uL : (ulong)state.GetPlayerSlotIndex(player);
        OwnerSetter.Invoke(ev, [player]);
        RngSetter.Invoke(ev, [new Rng(state.Rng.Seed + slot + StringHelper.GetDeterministicHashCode(ev.Id.Entry))]);

        var options = (IReadOnlyList<EventOption>)InitialOptions.Invoke(ev, null)!;
        return options.Select(o => o.Relic == null ? -1 : data.Relics.Id(o.Relic)).ToArray();
    }

    // NeowsBones.GetValidRelics
    public static List<RelicModel> BonesRelics(Player player) => ((IEnumerable<RelicModel>)BonesValidRelics.Invoke(null, [player])!).ToList();

    // The deck cards War Paint or Whetstone picks its upgrades from; null for any other relic.
    public static List<CardModel>? PickupUpgradeChoices(Player player, RelicModel relic)
    {
        CardType? type = relic switch
        {
            WarPaint => CardType.Skill,
            Whetstone => CardType.Attack,
            _ => null
        };
        return type == null ? null : PileType.Deck.GetPile(player).Cards.Where(c => c != null && c.Type == type && c.IsUpgradable).ToList();
    }

    // What one Neow offer gives when it is taken, on runs of its own so that the relic is the
    // first thing to draw from its streams.
    private static NeowOutcome NeowOutcome(GameData data, string seed, int relicId, RoomSet act1Rooms)
    {
        if (relicId < 0 || GameData.NeowGiveOf(data.Relics[relicId]) is not (_, int count))
        {
            NeowOutcome empty = new();
            PopulateFirstReward(data, StartRun(data, seed).Player, act1Rooms, out empty.FirstGold, out empty.FirstPotion, out empty.FirstCards);
            return empty;
        }

        if (data.Relics[relicId] is not NeowsBones)
        {
            Run singleRun = StartRun(data, seed);
            NeowOutcome outcome = Obtain(data, singleRun, relicId);
            PopulateFirstReward(data, singleRun.Player, act1Rooms, out outcome.FirstGold, out outcome.FirstPotion, out outcome.FirstCards);
            return outcome;
        }

        // NeowsBones.AfterObtained: its relics are rewards that cannot be skipped, taken in the
        // order the player likes, and the curse is rolled once they have all been taken.
        (Run Run, int[] Given) Begin()
        {
            Run run = StartRun(data, seed);
            List<RelicModel> valid = BonesRelics(run.Player);
            valid.UnstableShuffle(run.Player.PlayerRng.NonCombatGeneration);
            return (run, valid.Take(count).Select(r => data.Relics.Id(r)).ToArray());
        }

        (Run last, int[] given) = Begin();
        NestedOutcome[] nested = new NestedOutcome[given.Length];
        for (int i = 0; i < given.Length; i++)
        {
            NeowOutcome first = Obtain(data, Begin().Run, given[i]);
            last = Begin().Run;
            for (int j = 0; j < given.Length; j++)
            {
                if (j != i)
                    Obtain(data, last, given[j]);
            }

            nested[i] = new NestedOutcome { First = first, Second = Obtain(data, last, given[i]) };
        }

        List<CardModel> curses = ModelDb.CardPool<CurseCardPool>().GetUnlockedCards(last.Player.UnlockState, last.Player.RunState.CardMultiplayerConstraint)
            .Where(c => c.CanBeGeneratedByModifiers)
            .OrderBy(c => c.Id)
            .ToList();
        int[] curseCards = last.Player.PlayerRng.NonCombatGeneration.NextItem(curses) is { } curse ? [data.Cards.Id(curse)] : [];
        PopulateFirstReward(data, last.Player, act1Rooms, out int fg, out int fp, out int[] fc);
        return new NeowOutcome
        {
            Relics = given,
            Cards = curseCards,
            Nested = nested,
            FirstGold = fg,
            FirstPotion = fp,
            FirstCards = fc
        };
    }

    // AfterObtained of one Neow relic other than Neow's Bones, up to the point where the player
    // is shown the result, continuing on the streams of the run. Each case repeats what the relic
    // does with the game's own factories.
    private static NeowOutcome Obtain(GameData data, Run run, int relicId)
    {
        NeowOutcome outcome = new();
        if (relicId < 0 || GameData.NeowGiveOf(data.Relics[relicId]) is not (_, int count))
            return outcome;

        (_, Player player, _) = run;
        Rng rewards = player.PlayerRng.Rewards;
        Rng nonCombat = player.PlayerRng.NonCombatGeneration;
        CardPoolModel pool = player.Character.CardPool;
        List<CardModel> cards = [];
        List<RelicModel> relics = [];
        List<PotionModel> potions = [];
        IEnumerable<CardModel> Create(int n, CardCreationOptions options) => CardFactory.CreateForReward(player, n, options).Select(r => r.Card);
        IEnumerable<CardModel> Basics() => PileType.Deck.GetPile(player).Cards.Where(c => c.Rarity == CardRarity.Basic);

        switch (data.Relics[relicId])
        {
            case ArcaneScroll or HeftyTablet:
                cards.AddRange(Create(count, new CardCreationOptions([pool], CardCreationSource.Other, CardRarityOddsType.Uniform, c => c.Rarity == CardRarity.Rare).WithFlags(CardCreationFlags.NoUpgradeRoll)));
                break;
            case LeadPaperweight:
                cards.AddRange(Create(count, new CardCreationOptions([ModelDb.CardPool<ColorlessCardPool>()], CardCreationSource.Other, CardRarityOddsType.RegularEncounter)));
                break;
            case LostCoffer:
                // RewardsSet populates its rewards in the order the relic lists them.
                cards.AddRange(Create(count, new CardCreationOptions([pool], CardCreationSource.Other, CardRarityOddsType.RegularEncounter).WithFlags(CardCreationFlags.IsCardReward)));
                potions.Add(PotionFactory.CreateRandomPotionOutOfCombat(player, rewards));
                break;
            case ScrollBoxes:
                cards.AddRange(ScrollBoxes.GenerateRandomBundles(player, rewards).SelectMany(bundle => bundle));
                break;
            case SmallCapsule or LargeCapsule:
                for (int i = 0; i < count; i++)
                {
                    RelicModel pulled = RelicFactory.PullNextRelicForRewardFromFront(player);
                    relics.Add(pulled);
                    // WarPaint.AfterObtained and Whetstone.AfterObtained, against the starter deck.
                    PickupUpgradeChoices(player, pulled)?.StableShuffle(nonCombat);
                }

                break;
            case Kaleidoscope:
                for (int i = 0; i < count; i++)
                {
                    foreach (CardPoolModel other in player.UnlockState.CharacterCardPools.Where(p => p != pool).ToList().StableShuffle(nonCombat).Take(3))
                        cards.AddRange(Create(1, new CardCreationOptions([other], CardCreationSource.Other, CardRarityOddsType.RegularEncounter).WithFlags(CardCreationFlags.NoCardPoolModifications)));
                }

                break;
            case PhialHolster:
                potions.AddRange(PotionFactory.CreateRandomPotionsOutOfCombat(player, count, nonCombat));
                break;
            case LeafyPoultice:
                CardModel?[] originals = [Basics().FirstOrDefault(c => c.Tags.Contains(CardTag.Strike)), Basics().FirstOrDefault(c => c.Tags.Contains(CardTag.Defend))];
                foreach (CardModel? original in originals)
                {
                    if (original != null)
                        cards.Add(CardFactory.CreateRandomCardForTransform(original, false, player.PlayerRng.Transformations));
                }

                break;
            case NewLeaf:
                if (Basics().FirstOrDefault() is { } chosen)
                    cards.Add(CardFactory.CreateRandomCardForTransform(chosen, false, player.PlayerRng.Transformations));
                break;
        }

        outcome.Cards = cards.Select(c => data.Cards.Id(c)).ToArray();
        outcome.Relics = relics.Select(r => data.Relics.Id(r)).ToArray();
        outcome.Potions = potions.Where(p => p != null).Select(p => data.Potions.Id(p)).ToArray();
        return outcome;
    }

    // StandardActMap.CreateFor
    private static MapResult? BuildMap(RunState state, ActModel act, int actIndex)
    {
        StandardActMap map;
        try
        {
            map = new StandardActMap(new Rng(state.Rng.Seed, $"act_{actIndex + 1}_map"), act, false, false, act.HasSecondBoss);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        List<MapPoint> points = map.GetAllMapPoints().ToList();
        MapResult res = new()
        {
            Points = points.Select(p => (int[]) [p.coord.col, p.coord.row, (int)p.PointType]).ToArray(),
            Edges = points
                .SelectMany(p => p.Children.Where(c => c.PointType != MapPointType.Boss).Select(c => (int[]) [p.coord.col, p.coord.row, c.coord.col, c.coord.row]))
                .OrderBy(e => e[0]).ThenBy(e => e[1]).ThenBy(e => e[2]).ThenBy(e => e[3])
                .ToArray()
        };
        res.ComputeMaxPaths();
        return res;
    }

    // RewardsSet.GenerateRewardsFor for a normal combat: potion roll, then gold, potion and cards
    // are populated in that order, all from the player's rewards stream.
    private static void AddFirstReward(GameData data, SimResult result, Player player, RoomSet act1Rooms)
    {
        PopulateFirstReward(data, player, act1Rooms, out result.Gold, out result.Potion, out result.Cards);
    }

    private static void PopulateFirstReward(GameData data, Player player, RoomSet act1Rooms, out int goldAmount, out int potionId, out int[] cardIds)
    {
        EncounterModel encounter = act1Rooms.normalEncounters[0];
        bool potionDropped = player.PlayerOdds.PotionReward.Roll(player, RoomType.Monster);
        GoldReward gold = new(encounter.MinGoldReward, encounter.MaxGoldReward, player);
        gold.Populate();
        goldAmount = gold.Amount;
        potionId = -1;
        if (potionDropped)
        {
            PotionModel? potion = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
            potionId = potion == null ? -1 : data.Potions.Id(potion);
        }

        CardCreationOptions options = CardCreationOptions.ForRoom(player, RoomType.Monster)
            .WithFlags(CardCreationFlags.IsFromCombat)
            .WithFlags(CardCreationFlags.IsCardReward);
        cardIds = CardFactory.CreateForReward(player, 3, options).Select(c => data.Cards.Id(c.Card)).ToArray();
    }
}
