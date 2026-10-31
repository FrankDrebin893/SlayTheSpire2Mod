using System.Reflection;
using HarmonyLib;

namespace CardRarityRandomizer;

// Hooks:
//  - postfix on CardModel.Rarity getter answers from the table while active;
//  - postfix on RunManager.InitializeNewRun checks Settings.Enabled, builds table and shows summary;
//  - postfix on RunManager.InitializeSavedRun restores the table on reload if the run was randomized;
//  - postfix on RunManager.CleanUp resets the table when leaving a run.
public static class RarityPatches
{
    private static PropertyInfo? _idProp;

    public static void Apply(Harmony harmony)
    {
        var card = GameReflection.FindType("MegaCrit.Sts2.Core.Models.CardModel");
        var getter = card?.GetProperty("Rarity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetMethod;
        if (getter == null)
        {
            MainFile.Logger.Error("CardModel.Rarity getter not found; rarities will not change. The game probably updated.");
            return;
        }
        harmony.Patch(getter, postfix: new HarmonyMethod(typeof(RarityPatches), nameof(RarityPostfix)));

        var run = GameReflection.FindType("MegaCrit.Sts2.Core.Runs.RunManager")
                  ?? AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeTypes).FirstOrDefault(t => t.Name == "RunManager");

        var initNew = run?.GetMethod("InitializeNewRun", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (initNew == null)
        {
            MainFile.Logger.Error("RunManager.InitializeNewRun not found; no run will be randomized. The game probably updated.");
        }
        else
        {
            harmony.Patch(initNew, postfix: new HarmonyMethod(typeof(RarityPatches), nameof(RunStartPostfix)));
        }

        var initSaved = run?.GetMethod("InitializeSavedRun", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (initSaved != null)
        {
            harmony.Patch(initSaved, postfix: new HarmonyMethod(typeof(RarityPatches), nameof(RunSavedPostfix)));
        }

        var cleanUp = run?.GetMethod("CleanUp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (cleanUp != null)
        {
            harmony.Patch(cleanUp, postfix: new HarmonyMethod(typeof(RarityPatches), nameof(CleanUpPostfix)));
        }

        MainFile.Logger.Info("Card Rarity Randomizer patches applied.");
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
    }

    // __result is the real rarity; Harmony needs the exact enum type for a ref result.
    public static void RarityPostfix(object __instance, ref MegaCrit.Sts2.Core.Entities.Cards.CardRarity __result)
    {
        if (!RarityState.Active) return;
        try
        {
            _idProp ??= __instance.GetType().GetProperty("Id");
            var id = _idProp?.GetValue(__instance);
            var entry = id == null ? null : GameReflection.Get(id, "Entry") as string;
            if (entry != null && RarityState.TryGet(entry, out var name))
                __result = Enum.Parse<MegaCrit.Sts2.Core.Entities.Cards.CardRarity>(name);
        }
        catch (Exception e)
        {
            RarityState.Active = false;
            MainFile.Logger.Error($"Rarity override failed, disabled for this session: {e}");
        }
    }

    public static void RunStartPostfix(object __instance)
    {
        try
        {
            RarityState.Active = false;
            var seed = GameReflection.FindRunSeed(__instance) ?? Guid.NewGuid().ToString("N");

            // Decision is locked in at run start
            if (!Settings.Enabled)
            {
                RarityState.Clear();
                Settings.RecordRun(seed, false);
                MainFile.Logger.Info($"Card Rarity Randomizer is disabled; run '{seed}' started with standard rarities.");
                return;
            }

            var entries = CollectAllCards();
            if (entries.Count == 0)
            {
                MainFile.Logger.Error("ModelDb.AllCards gave no cards; run not randomized. The game probably updated.");
                return;
            }

            var changes = Randomizer.Build(entries, seed);
            RarityState.Set(seed, changes);
            RarityState.Active = true;
            Settings.RecordRun(seed, true);
            MainFile.Logger.Info($"Randomized {changes.Count} of {entries.Count} card rarities for seed '{seed}'.");
            SummaryScreen.Show(changes);
        }
        catch (Exception e)
        {
            RarityState.Active = false;
            MainFile.Logger.Error($"Rarity randomization failed: {e}");
        }
    }

    public static void RunSavedPostfix(object __instance)
    {
        try
        {
            var seed = GameReflection.FindRunSeed(__instance);
            if (seed == null) return;

            // Follows whether this run started with randomizer
            var recorded = Settings.RecordedRun(seed);
            var wasRandomized = recorded ?? Settings.Enabled;

            if (!wasRandomized)
            {
                RarityState.Clear();
                MainFile.Logger.Info($"Reloaded run '{seed}': randomizer was OFF for this run.");
                return;
            }

            var entries = CollectAllCards();
            if (entries.Count > 0)
            {
                var changes = Randomizer.Build(entries, seed);
                RarityState.Set(seed, changes);
                RarityState.Active = true;
                MainFile.Logger.Info($"Restored {changes.Count} randomized rarities for reloaded run '{seed}'.");
            }
        }
        catch (Exception e)
        {
            RarityState.Active = false;
            MainFile.Logger.Error($"Rarity restore on save reload failed: {e}");
        }
    }

    public static void CleanUpPostfix()
    {
        try
        {
            RarityState.Clear();
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Error clearing RarityState on cleanup: {e}");
        }
    }

    private static List<Randomizer.Entry> CollectAllCards()
    {
        var modelDb = GameReflection.FindType("MegaCrit.Sts2.Core.Models.ModelDb");
        var all = modelDb == null ? null : GameReflection.GetStatic(modelDb, "AllCards");
        var entries = new List<Randomizer.Entry>();
        foreach (var card in GameReflection.Enumerate(all))
        {
            var entry = GameReflection.Get(GameReflection.Get(card, "Id")!, "Entry") as string;
            var rarity = GameReflection.Get(card, "Rarity")?.ToString();
            if (entry == null || rarity == null) continue;
            var pool = GameReflection.Get(card, "Pool")?.GetType().Name ?? "Cards";
            var name = GameReflection.Get(card, "Title")?.ToString() ?? entry;
            entries.Add(new Randomizer.Entry(entry, name, pool, rarity));
        }
        return entries;
    }
}
