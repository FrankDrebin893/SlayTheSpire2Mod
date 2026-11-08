using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace RasmusCardBalance;

// Frozen Eye from Slay the Spire 1: a shop relic that shows the draw pile in draw order.
// SlayTheSpire2.pck still ships frozen_eye.tres in both relic_atlas and relic_outline_atlas,
// so the relic needs no .pck; BigIconPath points at PackedIconPath because the standalone
// inspect PNG (images/relics/frozen_eye.png) is not in the pack.
public sealed class FrozenEye : RelicModel
{
    internal static bool Enabled;

    public override RelicRarity Rarity => RelicRarity.Shop;

    protected override string BigIconPath => PackedIconPath;
}

// ReflectionHelper.GetSubtypesInMods<AbstractModel>() discovers every AbstractModel subclass in
// loaded mod assemblies when ModelDb.Init() runs. When the Frozen Eye tweak is turned off in
// settings, we filter FrozenEye out so ModelDb and ModelIdSerializationCache stay untouched.
[HarmonyPatch(typeof(ReflectionHelper), nameof(ReflectionHelper.GetSubtypesInMods), typeof(Type))]
internal static class FrozenEyeSubtypesPatch
{
    static void Postfix(ref IEnumerable<Type> __result)
    {
        if (!FrozenEye.Enabled)
            __result = __result.Where(t => t != typeof(FrozenEye));
    }
}

[HarmonyPatch]
public static class FrozenEyePatch
{
    [HarmonyPrepare]
    static bool Prepare(MethodBase? original)
    {
        if (original != null) return true;

        var locCtor = AccessTools.Constructor(typeof(LocTable), [typeof(string), typeof(Dictionary<string, string>), typeof(LocTable)]);
        var pileChanged = AccessTools.Method(typeof(NCardPileScreen), "OnPileContentsChanged");
        var pileReady = AccessTools.Method(typeof(NCardPileScreen), nameof(NCardPileScreen._Ready));
        var setCards = AccessTools.Method(typeof(NCardGrid), nameof(NCardGrid.SetCards));
        var gridField = AccessTools.Field(typeof(NCardPileScreen), "_grid");
        var labelField = AccessTools.Field(typeof(NCardPileScreen), "_bottomLabel");
        if (locCtor == null || pileChanged == null || pileReady == null || setCards == null || gridField == null || labelField == null)
        {
            MainFile.Logger.Error("Frozen Eye patch targets were not found; patch not applied. The game probably updated.");
            return false;
        }

        try
        {
            ModHelper.AddModelToPool<SharedRelicPool, FrozenEye>();
            FrozenEye.Enabled = true;
            MainFile.Logger.Info("Frozen Eye added to the shop relic pool.");
            return true;
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not add Frozen Eye to SharedRelicPool: {e}");
            return false;
        }
    }

    // Injects English strings into the base "relics" table; other locales inherit them via _fallback.
    [HarmonyPatch(typeof(LocTable), MethodType.Constructor, typeof(string), typeof(Dictionary<string, string>), typeof(LocTable))]
    [HarmonyPostfix]
    static void AddLocalization(string name, Dictionary<string, string> data, LocTable? fallback)
    {
        if (name != "relics" || fallback != null) return;
        data.TryAdd("FROZEN_EYE.title", "Frozen Eye");
        data.TryAdd("FROZEN_EYE.description", "When viewing your [gold]Draw Pile[/gold], the cards are now shown in order.");
        data.TryAdd("FROZEN_EYE.flavor", "Staring into the eye, you see a glimpse of your future.");
    }

    // NCardPileScreen.OnPileContentsChanged sorts Pile.Cards by rarity and entry ID when
    // Pile.Type == PileType.Draw before handing the list to _grid.SetCards(..., [Ascending]).
    // With Frozen Eye, pass Pile.Cards in draw order (index 0 is the next card drawn).
    [HarmonyPatch(typeof(NCardPileScreen), "OnPileContentsChanged")]
    [HarmonyPrefix]
    static bool ShowDrawPileInOrder(NCardPileScreen __instance, NCardGrid ____grid)
    {
        if (!HasFrozenEye(__instance.Pile)) return true;
        ____grid.SetCards(__instance.Pile.Cards.ToList(), __instance.Pile.Type, [SortingOrders.Ascending]);
        return false;
    }

    // DRAW_PILE_INFO has two lines in every locale: the draw pile description and
    // "[gold](Cards shown are sorted by rarity)[/gold]". Drop the second line with Frozen Eye.
    [HarmonyPatch(typeof(NCardPileScreen), nameof(NCardPileScreen._Ready))]
    [HarmonyPostfix]
    static void HideSortedByRarityBanner(NCardPileScreen __instance, MegaRichTextLabel ____bottomLabel)
    {
        if (!HasFrozenEye(__instance.Pile)) return;
        var nl = ____bottomLabel.Text.IndexOf('\n');
        if (nl >= 0) ____bottomLabel.Text = ____bottomLabel.Text[..nl];
    }

    // NCombatPileCardSelectScreen passes PileType.Draw with [RarityAscending, AlphabetAscending]
    // to NCardGrid.SetCards. Switching to Ascending preserves the draw order there as well.
    [HarmonyPatch(typeof(NCardGrid), nameof(NCardGrid.SetCards))]
    [HarmonyPrefix]
    static void KeepDrawPileSelectOrder(IReadOnlyList<CardModel> cardsToDisplay, PileType pileType, List<SortingOrders> sortingPriority)
    {
        if (pileType != PileType.Draw || sortingPriority.Count == 0 || sortingPriority[0] == SortingOrders.Ascending)
            return;
        if (!HasFrozenEye(cardsToDisplay)) return;
        sortingPriority.Clear();
        sortingPriority.Add(SortingOrders.Ascending);
    }

    private static bool HasFrozenEye(CardPile? pile) =>
        pile is { Type: PileType.Draw } && HasFrozenEye(pile.Cards);

    private static bool HasFrozenEye(IReadOnlyList<CardModel> cards)
    {
        var owner = cards.FirstOrDefault()?.Owner;
        return owner != null && owner.Relics.Any(r => r is FrozenEye && !r.IsMelted);
    }
}
