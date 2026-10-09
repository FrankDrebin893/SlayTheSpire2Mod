using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

namespace RasmusSlayTheSpire2Mod.SeedInspector;

// Adds the seed inspector panel once the Custom Run screen has resolved its own nodes.
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen._Ready))]
public static class CustomRunScreenPatch
{
    static void Postfix(NCustomRunScreen __instance)
    {
        try
        {
            SeedInspectorPanel.AttachTo(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Seed inspector panel could not be added to the Custom Run screen. The game probably updated. {e}");
        }
    }
}
