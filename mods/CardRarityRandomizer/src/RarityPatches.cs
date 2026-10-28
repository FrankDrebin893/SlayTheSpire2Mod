using System.Reflection;
using HarmonyLib;

namespace CardRarityRandomizer;

// Two hooks, both found by name:
//  - a postfix on RunManager.InitializeNewRun builds the table and shows the summary;
//  - a postfix on the CardModel.Rarity getter answers from the table while a run is active.
// Reading the real rarity (to build the table) switches the override off for that moment.
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
        var init = run?.GetMethod("InitializeNewRun", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (init == null)
        {
            MainFile.Logger.Error("RunManager.InitializeNewRun not found; no run will be randomized. The game probably updated.");
            return;
        }
        harmony.Patch(init, postfix: new HarmonyMethod(typeof(RarityPatches), nameof(RunStartPostfix)));
        MainFile.Logger.Info("Card Rarity Randomizer patches applied.");
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
    }

    // __result is the real rarity (a CardRarity enum boxed as object when read through ref).
    public static void RarityPostfix(object __instance, ref object __result)
    {
        if (!RarityState.Active) return;
        try
        {
            _idProp ??= __instance.GetType().GetProperty("Id");
            var id = _idProp?.GetValue(__instance);
            var entry = id == null ? null : GameReflection.Get(id, "Entry") as string;
            if (entry != null && RarityState.TryGet(entry, out var name))
                __result = Enum.Parse(__result.GetType(), name);
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

            if (entries.Count == 0)
            {
                MainFile.Logger.Error("ModelDb.AllCards gave no cards; run not randomized. The game probably updated.");
                return;
            }

            var changes = Randomizer.Build(entries, seed);
            RarityState.Set(seed, changes);
            RarityState.Active = true;
            MainFile.Logger.Info($"Randomized {changes.Count} of {entries.Count} card rarities for seed '{seed}'.");
            SummaryScreen.Show(changes);
        }
        catch (Exception e)
        {
            RarityState.Active = false;
            MainFile.Logger.Error($"Rarity randomization failed: {e}");
        }
    }
}
