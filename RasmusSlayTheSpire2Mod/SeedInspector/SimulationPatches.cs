using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace RasmusSlayTheSpire2Mod.SeedInspector;

// Ascension checks go through RunManager.Instance.HasAscension, which is false outside a run.
// While a preview is being simulated, answer with the previewed ascension instead.
[HarmonyPatch(typeof(RunManager), nameof(RunManager.HasAscension))]
public static class SimulatedAscensionPatch
{
    static bool Prefix(AscensionLevel level, ref bool __result)
    {
        if (SeedSimulator.AscensionOverride is not { } ascension)
            return true;

        __result = ascension.HasLevel(level);
        return false;
    }
}

// Creating a player marks its starting relics as seen in the save file. A preview must not
// write to the player's progress.
[HarmonyPatch]
public static class SimulatedSeenPatch
{
    static IEnumerable<MethodBase> TargetMethods() =>
    [
        AccessTools.Method(typeof(SaveManager), nameof(SaveManager.MarkRelicAsSeen)),
        AccessTools.Method(typeof(SaveManager), nameof(SaveManager.MarkCardAsSeen)),
        AccessTools.Method(typeof(SaveManager), nameof(SaveManager.MarkPotionAsSeen))
    ];

    static bool Prefix() => !SeedSimulator.IsSimulating;
}
