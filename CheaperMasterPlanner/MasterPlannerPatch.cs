using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Cards;

namespace CheaperMasterPlanner;

// MasterPlanner's constructor is `base(2, CardType.Power, CardRarity.Rare, TargetType.Self)`.
// The first constant it loads is the canonical energy cost, so that is the one we lower.
// The upgrade (EnergyCost.UpgradeBy(-1)) is untouched and now takes the card from 1 to 0.
[HarmonyPatch(typeof(MasterPlanner), MethodType.Constructor)]
public static class MasterPlannerPatch
{
    private const int NewCost = 1;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var patched = false;
        foreach (var instruction in instructions)
        {
            if (!patched && instruction.opcode == OpCodes.Ldc_I4_2)
            {
                patched = true;
                yield return new CodeInstruction(OpCodes.Ldc_I4, NewCost).WithLabels(instruction.labels);
                continue;
            }

            yield return instruction;
        }

        if (patched)
            MainFile.Logger.Info($"Master Planner energy cost set to {NewCost}.");
        else
            MainFile.Logger.Error("Master Planner constructor no longer loads a cost of 2; patch not applied. The game probably updated.");
    }
}
