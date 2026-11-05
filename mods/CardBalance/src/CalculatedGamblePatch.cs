using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace RasmusCardBalance;

// CalculatedGamble.OnUpgrade is `AddKeyword(CardKeyword.Retain)`. We swap that call for
// `RemoveKeyword(CardKeyword.Exhaust)`, so the upgrade removes Exhaust instead of adding Retain.
// OnUpgrade is also what UpgradeInternal runs when cards are rebuilt, so this covers every path.
[HarmonyPatch(typeof(CalculatedGamble), "OnUpgrade")]
public static class CalculatedGamblePatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var addKeyword = AccessTools.Method(typeof(CardModel), nameof(CardModel.AddKeyword));
        var removeKeyword = AccessTools.Method(typeof(CardModel), nameof(CardModel.RemoveKeyword));
        var list = new List<CodeInstruction>(instructions);
        var patched = false;

        for (var i = 1; i < list.Count; i++)
        {
            if (!patched && list[i].Calls(addKeyword) && list[i - 1].LoadsConstant((int)CardKeyword.Retain))
            {
                list[i - 1] = new CodeInstruction(OpCodes.Ldc_I4, (int)CardKeyword.Exhaust).WithLabels(list[i - 1].labels);
                list[i] = new CodeInstruction(OpCodes.Call, removeKeyword);
                patched = true;
            }
        }

        if (patched)
            MainFile.Logger.Info("Calculated Gamble upgrade now removes Exhaust instead of adding Retain.");
        else
            MainFile.Logger.Error("Calculated Gamble OnUpgrade no longer calls AddKeyword(Retain); patch not applied. The game probably updated.");
        return list;
    }
}
