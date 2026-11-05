using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace RasmusCardBalance;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "RasmusCardBalance";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);

        // Tweaks are patched only when enabled (a restart is needed to change that); everything else,
        // such as the settings menu, is patched unconditionally.
        var tweakPatches = Tweaks.All.Select(t => t.PatchType).ToHashSet();
        foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
        {
            if (!type.IsDefined(typeof(HarmonyPatch), false) || tweakPatches.Contains(type)) continue;
            harmony.CreateClassProcessor(type).Patch();
        }

        foreach (var tweak in Tweaks.All)
        {
            var enabled = Settings.IsEnabled(tweak);
            Settings.MarkApplied(tweak, enabled);
            if (!enabled)
            {
                Logger.Info($"{tweak.Name} is turned off in the settings; not patched.");
                continue;
            }

            try
            {
                harmony.CreateClassProcessor(tweak.PatchType).Patch();
            }
            catch (Exception e)
            {
                Logger.Error($"Could not apply the {tweak.Name} tweak: {e}");
            }
        }
    }
}
