using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Models.RelicPools;
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
    All = Reward | Maps
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
        AscensionOverride = new AscensionManager(ascension);
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
            GameMode.Custom,
            ascension,
            seed);
        return new Run(state, player, unlocks);
    }

    private static SimResult SimulateInternal(GameData data, string seed, SimParts parts)
    {
        (RunState state, Player player, _) = CreateRun(seed, data.Character, data.Ascension, data.Act1Key);

        // RunManager.InitializeNewRun
        Rng upFront = state.Rng.UpFront;
        state.SharedRelicGrabBag.Populate(ModelDb.RelicPool<SharedRelicPool>().GetUnlockedRelics(state.UnlockState), upFront);
        player.PopulateRelicGrabBagIfNecessary(upFront);
        AscensionOverride!.ApplyEffectsTo(player);

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
            if (i == state.Acts.Count - 1 && AscensionOverride.HasLevel(AscensionLevel.DoubleBoss))
            {
                act.SetSecondBossEncounter(upFront.NextItem(act.AllBossEncounters.Where(e => e.Id != act.BossEncounter.Id)));
            }
        }

        List<RoomSet> rooms = state.Acts.Select(a => (RoomSet)RoomsField.GetValue(a)!).ToList();
        EncounterModel? secondBoss = state.Acts[^1].SecondBossEncounter;
        AncientEventModel? act1Ancient = rooms[0].HasAncient ? rooms[0].Ancient : null;

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
            SharedRelics = Deques(data, state.SharedRelicGrabBag),
            PlayerRelics = Deques(data, player.RelicGrabBag),
            Neow = act1Ancient != null && data.Ancients.Id(act1Ancient) == data.NeowId ? GetAncientOffers(data, act1Ancient, player, state) : [],
            Maps = state.Acts.Select((a, i) => parts.HasFlag(SimParts.Maps) ? BuildMap(state, a, i) : null).ToArray()
        };

        // Last, because it advances the player's rewards stream.
        if (parts.HasFlag(SimParts.Reward))
            AddFirstReward(data, result, player, rooms[0]);
        return result;
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
        return new MapResult
        {
            Points = points.Select(p => (int[]) [p.coord.col, p.coord.row, (int)p.PointType]).ToArray(),
            Edges = points
                .SelectMany(p => p.Children.Where(c => c.PointType != MapPointType.Boss).Select(c => (int[]) [p.coord.col, p.coord.row, c.coord.col, c.coord.row]))
                .OrderBy(e => e[0]).ThenBy(e => e[1]).ThenBy(e => e[2]).ThenBy(e => e[3])
                .ToArray()
        };
    }

    // RewardsSet.GenerateRewardsFor for a normal combat: potion roll, then gold, potion and cards
    // are populated in that order, all from the player's rewards stream.
    private static void AddFirstReward(GameData data, SimResult result, Player player, RoomSet act1Rooms)
    {
        EncounterModel encounter = act1Rooms.normalEncounters[0];
        bool potionDropped = player.PlayerOdds.PotionReward.Roll(player, RoomType.Monster);
        GoldReward gold = new(encounter.MinGoldReward, encounter.MaxGoldReward, player);
        gold.Populate();
        result.Gold = gold.Amount;
        if (potionDropped)
        {
            PotionModel? potion = PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
            result.Potion = potion == null ? -1 : data.Potions.Id(potion);
        }

        CardCreationOptions options = CardCreationOptions.ForRoom(player, RoomType.Monster)
            .WithFlags(CardCreationFlags.IsFromCombat)
            .WithFlags(CardCreationFlags.IsCardReward);
        result.Cards = CardFactory.CreateForReward(player, 3, options).Select(c => data.Cards.Id(c.Card)).ToArray();
    }
}
