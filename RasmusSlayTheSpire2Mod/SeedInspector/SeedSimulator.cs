using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Players;
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

namespace RasmusSlayTheSpire2Mod.SeedInspector;

// Builds a detached RunState for a seed and runs the game's own start-of-run generation on it,
// mirroring NGame.StartNewSingleplayerRun, RunManager.InitializeNewRun and RunManager.GenerateRooms
// without ever handing the state to RunManager.
public static class SeedSimulator
{
    private const int EventsShown = 3;

    private static readonly FieldInfo RoomsField = AccessTools.Field(typeof(ActModel), "_rooms");
    private static readonly MethodInfo OwnerSetter = AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Owner));
    private static readonly MethodInfo RngSetter = AccessTools.PropertySetter(typeof(EventModel), nameof(EventModel.Rng));
    private static readonly MethodInfo InitialOptions = AccessTools.Method(typeof(EventModel), "GenerateInitialOptionsWrapper");

    // Non-null only while a simulation runs. SimulationPatches reads it so that ascension checks,
    // which normally ask the RunManager singleton, see the ascension being previewed.
    public static AscensionManager? AscensionOverride { get; private set; }

    public static bool IsSimulating => AscensionOverride != null;

    private static bool _loggedFailure;

    public static SeedPreview? Simulate(string rawSeed, CharacterModel character, int ascension, string act1Key, bool includeCardReward)
    {
        string seed = SeedHelper.CanonicalizeSeed(rawSeed);

        // CardRarityOdds caches an ascension-dependent value in a static field. Make sure it is
        // initialised before the override is active, so a preview can never leak into real runs.
        RuntimeHelpers.RunClassConstructor(typeof(CardRarityOdds).TypeHandle);

        AscensionOverride = new AscensionManager(ascension);
        try
        {
            return SimulateInternal(seed, character, ascension, act1Key, includeCardReward);
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                MainFile.Logger.Error($"Seed inspector could not simulate seed {seed}. The game probably updated. {e}");
            }

            return null;
        }
        finally
        {
            AscensionOverride = null;
        }
    }

    private static SeedPreview SimulateInternal(string seed, CharacterModel character, int ascension, string act1Key, bool includeCardReward)
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

        ActModel act1 = state.Acts[0];
        RoomSet act1Rooms = (RoomSet)RoomsField.GetValue(act1)!;

        return new SeedPreview
        {
            Seed = seed,
            Acts = state.Acts.Select(a => a.Title.GetFormattedText()).ToList(),
            Bosses = state.Acts.Select(a => a.BossEncounter.Title.GetFormattedText()).ToList(),
            SecondBoss = state.Acts[^1].SecondBossEncounter?.Title.GetFormattedText(),
            Ancients = state.Acts.Select(a => a.Ancient.Title.GetFormattedText()).ToList(),
            NeowOffers = GetAncientOffers(act1.Ancient, player, state),
            Act1Events = act1Rooms.events.Take(EventsShown).Select(e => e.Title.GetFormattedText()).ToList(),
            Act1Map = DescribeMap(state, act1),
            // Last, because it advances the player's rewards stream.
            FirstCardReward = includeCardReward ? GetFirstCardReward(player, act1Rooms) : null
        };
    }

    // EventModel.BeginEvent, minus everything that touches the scene.
    private static List<string> GetAncientOffers(AncientEventModel ancient, Player player, RunState state)
    {
        EventModel ev = ancient.ToMutable();
        ulong slot = ev.IsShared ? 0uL : (ulong)state.GetPlayerSlotIndex(player);
        OwnerSetter.Invoke(ev, [player]);
        RngSetter.Invoke(ev, [new Rng(state.Rng.Seed + slot + StringHelper.GetDeterministicHashCode(ev.Id.Entry))]);

        var options = (IReadOnlyList<EventOption>)InitialOptions.Invoke(ev, null)!;
        return options.Select(o => o.Relic?.Title.GetFormattedText() ?? o.Title.GetFormattedText()).ToList();
    }

    // StandardActMap.CreateFor
    private static string DescribeMap(RunState state, ActModel act)
    {
        StandardActMap map = new(new Rng(state.Rng.Seed, "act_1_map"), act, false, false, act.HasSecondBoss);
        List<MapPoint> points = map.GetAllMapPoints().ToList();
        int Count(MapPointType type) => points.Count(p => p.PointType == type);
        return $"{Count(MapPointType.Elite)} elites, {Count(MapPointType.RestSite)} rests, {Count(MapPointType.Unknown)} unknown, {Count(MapPointType.Shop)} shops";
    }

    // RewardsSet.GenerateRewardsFor for a normal combat: potion roll, then gold, potion and cards
    // are populated in that order, all from the player's rewards stream.
    private static List<string> GetFirstCardReward(Player player, RoomSet act1Rooms)
    {
        EncounterModel encounter = act1Rooms.normalEncounters[0];
        bool potionDropped = player.PlayerOdds.PotionReward.Roll(player, RoomType.Monster);
        new GoldReward(encounter.MinGoldReward, encounter.MaxGoldReward, player).Populate();
        if (potionDropped)
        {
            PotionFactory.CreateRandomPotionOutOfCombat(player, player.PlayerRng.Rewards);
        }

        CardCreationOptions options = CardCreationOptions.ForRoom(player, RoomType.Monster)
            .WithFlags(CardCreationFlags.IsFromCombat)
            .WithFlags(CardCreationFlags.IsCardReward);
        return CardFactory.CreateForReward(player, 3, options).Select(c => c.Card.Title).ToList();
    }
}
