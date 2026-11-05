using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace RasmusCardBalance;

// Unceasing Top only triggers through Hook.AfterHandEmptied, which CombatManager.CheckForEmptyHand fires when the hand
// has no cards at all. A hand holding only unplayable cards (curses, statuses, ...) never counts as empty, so the relic
// stays idle. Rather than change the shared emptiness check (other relics also use AfterHandEmptied), this postfix runs
// after CheckForEmptyHand and, if the hand is non-empty but contains nothing playable, calls Unceasing Top directly.
// A card that is merely unaffordable (energy/stars) is still playable, so such a hand does not count as empty.
[HarmonyPatch]
public static class UnceasingTopPatch
{
    private const UnplayableReason ResourceReasons = UnplayableReason.EnergyCostTooHigh | UnplayableReason.StarCostTooHigh;

    private static readonly MethodInfo? Target = AccessTools.Method(
        typeof(CombatManager), nameof(CombatManager.CheckForEmptyHand),
        new[] { typeof(CombatId?), typeof(PlayerChoiceContext), typeof(Player) });

    static bool Prepare()
    {
        if (Target == null)
        {
            MainFile.Logger.Error("CombatManager.CheckForEmptyHand(CombatId?, PlayerChoiceContext, Player) not found; Unceasing Top patch not applied. The game probably updated.");
            return false;
        }

        return true;
    }

    static MethodBase TargetMethod() => Target!;

    static void Postfix(ref Task __result, PlayerChoiceContext choiceContext, Player player)
    {
        __result = AfterCheck(__result, choiceContext, player);
    }

    private static async Task AfterCheck(Task original, PlayerChoiceContext choiceContext, Player player)
    {
        await original;

        var relic = player.Relics.OfType<UnceasingTop>().FirstOrDefault();
        if (relic == null)
            return;

        // Each draw can add more unplayable cards; stop as soon as a draw adds nothing (piles exhausted or hand full).
        while (CombatManager.Instance.IsInProgress
               && !CombatManager.Instance.IsExecutingCardOrPotionEffect(player)
               && HandHasOnlyUnplayableCards(player))
        {
            var before = player.PlayerCombatState!.Hand.Cards.Count;
            await relic.AfterHandEmptied(choiceContext, player);
            if (player.PlayerCombatState.Hand.Cards.Count <= before)
                break;
        }
    }

    private static bool HandHasOnlyUnplayableCards(Player player)
    {
        var cards = player.PlayerCombatState?.Hand.Cards;
        if (cards == null || cards.Count == 0)
            return false; // truly empty: the game's own check already fired the hook

        foreach (var card in cards)
        {
            card.CanPlay(out var reason, out _);
            // Only a card blocked for a non-resource reason counts as unplayable.
            if ((reason & ~ResourceReasons) == UnplayableReason.None)
                return false;
        }

        return true;
    }
}
