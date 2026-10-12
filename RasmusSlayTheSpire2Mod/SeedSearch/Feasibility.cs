namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Whether a filter can hold together with the ones already there. The pickers only offer what a
// seed can generate on its own; this catches filters that rule each other out, so a search is
// never started that cannot find anything.
public static class Feasibility
{
    private const int RewardCards = 3;

    // Why no seed can match candidate together with existing, or null when one can.
    public static string? Conflict(GameData d, IReadOnlyList<Cond> existing, Cond candidate)
    {
        foreach (Cond other in existing)
        {
            if (Clash(d, candidate, other) || Clash(d, other, candidate))
                return $"It rules out \"{other.Describe(d)}\".";
        }

        List<Cond> all = [.. existing, candidate];
        return Neow(d, all) ?? Gives(d, all) ?? Crowding(d, all);
    }

    // Two filters of which only one can hold. Asked both ways round.
    private static bool Clash(GameData d, Cond a, Cond b) => (a, b) switch
    {
        (ActCond x, ActCond y) => x.Slot == y.Slot,
        (BossCond x, BossCond y) => x.Slot == y.Slot,
        (AncientCond x, AncientCond y) => x.Slot == y.Slot,
        (SecondBossCond, SecondBossCond) => true,
        (RewardPotionCond, RewardPotionCond) => true,
        // RunManager.GenerateRooms picks the second boss from the others.
        (SecondBossCond x, BossCond y) => y.Slot == d.SlotActs.Length - 1 && x.Encounter == y.Encounter,
        (MapCountCond x, MapCountCond y) => x.Slot == y.Slot && x.PointType == y.PointType && (x.Max < y.Min || y.Max < x.Min),
        (ActCond x, BossCond y) => x.Slot == y.Slot && !d.ActInfos[x.Act].Boss.Contains(y.Encounter),
        (ActCond x, AncientCond y) => x.Slot == y.Slot && !d.ActInfos[x.Act].Ancients.Contains(y.Ancient) && !(x.Slot > 0 && d.SharedAncients.Contains(y.Ancient)),
        (ActCond x, EventCond y) => x.Slot == y.Slot && !d.ActInfos[x.Act].Events.Contains(y.Event),
        (ActCond x, EncounterCond y) => x.Slot == y.Slot && !FightsOf(d.ActInfos[x.Act], y.Elite, y.WithinFirst).Contains(y.Encounter),
        _ => false
    };

    private static IEnumerable<int> FightsOf(ActInfo act, bool elite, int within) =>
        elite ? act.Elite : within > act.WeakCount ? act.Weak.Concat(act.Regular) : act.Weak;

    // Neow.GenerateInitialOptions: one cursed relic and two of the others, and some never together.
    private static string? Neow(GameData d, List<Cond> all)
    {
        int[] offers = all.Select(c => c switch
        {
            NeowOfferCond o => o.Relic,
            NeowGivesCond g => g.Relic,
            NeowNestedCond n => n.Relic,
            _ => -1
        }).Where(r => r >= 0).Distinct().ToArray();

        if (offers.Count(d.NeowCurses.Contains) > 1)
            return "Neow offers only one cursed relic.";
        if (offers.Count(r => !d.NeowCurses.Contains(r)) > 2)
            return "Neow offers only two relics next to the cursed one.";
        foreach (int[] pair in d.NeowNeverTogether)
        {
            if (offers.Contains(pair[0]) && offers.Contains(pair[1]))
                return $"Neow never offers {d.RelicName(pair[0])} together with {d.RelicName(pair[1])}.";
        }

        return null;
    }

    // More different things asked of a relic, or of the first card reward, than it hands out.
    private static string? Gives(GameData d, List<Cond> all)
    {
        if (all.OfType<RewardCardCond>().Count() > RewardCards)
            return $"The first card reward has only {RewardCards} cards.";

        // What is asked of each relic: of a Neow offer directly, or of a relic Neow's Bones gives.
        var asked = all.OfType<NeowGivesCond>().Select(c => (Path: (c.Relic, Via: -1), c.What, c.Item))
            .Concat(all.OfType<NeowNestedCond>().Select(c => (Path: (c.Relic, Via: -1), What: GiveItem.Relic, Item: c.Via)))
            .Concat(all.OfType<NeowNestedCond>().Select(c => (Path: (c.Relic, c.Via), c.What, c.Item)));
        foreach (var group in asked.GroupBy(a => (a.Path, a.What)))
        {
            int relic = group.Key.Path.Via >= 0 ? group.Key.Path.Via : group.Key.Path.Relic;
            (int cards, int relics, int potions) = d.NeowGiveCapacity(relic);
            int capacity = group.Key.What switch
            {
                GiveItem.Card => cards,
                GiveItem.Relic => relics,
                _ => potions
            };
            if (group.Select(a => a.Item).Distinct().Count() > capacity)
                return $"{d.RelicName(relic)} gives only {capacity} {group.Key.What.ToString().ToLowerInvariant()}{(capacity == 1 ? "" : "s")}.";
        }

        return null;
    }

    // More different things asked for within the first N of a list than fit there.
    private static string? Crowding(GameData d, List<Cond> all)
    {
        if (all.OfType<EventCond>().GroupBy(c => c.Slot).Any(g => Crowded(g.DistinctBy(c => c.Event).Select(c => c.WithinFirst))))
            return "More events are asked for that early than fit.";
        if (all.OfType<EncounterCond>().GroupBy(c => (c.Slot, c.Elite)).Any(g => Crowded(g.DistinctBy(c => c.Encounter).Select(c => c.WithinFirst))))
            return "More fights are asked for that early than fit.";
        if (all.OfType<RelicCond>().GroupBy(c => (c.Shared, d.Relics[c.Relic].Rarity)).Any(g => Crowded(g.DistinctBy(c => c.Relic).Select(c => c.WithinFirst))))
            return "More relics of one rarity are asked for that early than fit.";

        // The weak fights of an act only fill its first few rooms.
        foreach (IGrouping<int, EncounterCond> slot in all.OfType<EncounterCond>().Where(c => !c.Elite).GroupBy(c => c.Slot))
        {
            ActInfo[] acts = d.SlotActs[slot.Key].Select(a => d.ActInfos[a]).ToArray();
            int weakOnly = slot.Select(c => c.Encounter).Distinct().Count(e => acts.All(a => !a.Regular.Contains(e)));
            if (weakOnly > acts.Max(a => a.WeakCount))
                return "An act does not open with that many easy fights.";
        }

        return null;
    }

    // Each needs a place of its own among the first N it asks for.
    private static bool Crowded(IEnumerable<int> withins) => withins.Order().Select((within, i) => within < i + 1).Any(tooEarly => tooEarly);
}
