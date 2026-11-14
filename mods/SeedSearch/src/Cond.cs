using System.Text;
using System.Text.Json;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// One search condition; a seed must satisfy all of them. Write produces the form the Rust engine
// reads (seed-engine/src/sim.rs, enum Cond) and Matches is the same test for the C# fallback.
public abstract record Cond
{
    public virtual SimParts Needs => SimParts.None;

    // Optional inline stepper for "within first N" conditions in the active filter list.
    public virtual (int Value, int Max, Func<int, Cond> With)? WithinStepper => null;

    // Optional inline stepper for "min..max" map count conditions in the active filter list.
    public virtual (int Min, int Max, int Limit, Func<int, int, Cond> With)? RangeStepper => null;

    public abstract void Write(Utf8JsonWriter w);

    public abstract bool Matches(SimResult r);

    public abstract string Describe(GameData d);

    public static string ToJson(IEnumerable<Cond> conds)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartArray();
            foreach (Cond c in conds)
            {
                w.WriteStartObject();
                c.Write(w);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // Whether a seed satisfies every condition. Not the same as asking each one: what the relics of
    // Neow's Bones give depends on the order they are taken in, so the conditions on them must all
    // hold for one order.
    public static bool AllMatch(IReadOnlyList<Cond> conds, SimResult r)
    {
        if (!conds.All(c => c.Matches(r)))
            return false;
        foreach (IGrouping<int, NeowNestedCond> group in conds.OfType<NeowNestedCond>().GroupBy(c => c.Relic))
        {
            int idx = Array.IndexOf(r.Neow, group.Key);
            if (idx < 0 || idx >= r.NeowOutcomes.Length)
                return false;
            NeowOutcome outcome = r.NeowOutcomes[idx];
            if (!Enumerable.Range(0, outcome.Relics.Length).Any(first => group.All(c => c.Holds(outcome, first))))
                return false;
        }

        return true;
    }

    // Flatten top-level and AnyOf child conditions for UI highlighting in PreviewPanel.
    public static IEnumerable<Cond> FlattenAll(IEnumerable<Cond> conds)
    {
        foreach (Cond c in conds)
        {
            if (c is AnyOfCond any)
            {
                foreach (Cond sub in FlattenAll(any.Conds))
                    yield return sub;
            }
            else
            {
                yield return c;
            }
        }
    }

    public static bool TargetsRelic(IEnumerable<Cond> conds, int relic) =>
        FlattenAll(conds).Any(c => c switch
        {
            NeowOfferCond o => o.Relic == relic,
            NeowGivesCond g => g.Relic == relic || (g.What == GiveItem.Relic && g.Item == relic),
            NeowNestedCond n => n.Relic == relic || n.Via == relic || (n.What == GiveItem.Relic && n.Item == relic),
            AncientRelicCond a => a.Relic == relic,
            RelicCond r => r.Relic == relic,
            TreasureRelicCond t => t.Relic == relic,
            ShopItemCond { What: GiveItem.Relic } s => s.Item == relic,
            RewardCardCond { AfterNeow: { } an } => an == relic,
            _ => false
        });

    public static bool TargetsCard(IEnumerable<Cond> conds, int card) =>
        FlattenAll(conds).Any(c => c switch
        {
            RewardCardCond rc => rc.Card == card,
            NeowGivesCond { What: GiveItem.Card } g => g.Item == card,
            NeowNestedCond { What: GiveItem.Card } n => n.Item == card,
            ShopItemCond { What: GiveItem.Card } s => s.Item == card,
            _ => false
        });

    public static bool TargetsPotion(IEnumerable<Cond> conds, int potion) =>
        FlattenAll(conds).Any(c => c switch
        {
            RewardPotionCond { Present: true } => potion >= 0,
            NeowGivesCond { What: GiveItem.Potion } g => g.Item == potion,
            NeowNestedCond { What: GiveItem.Potion } n => n.Item == potion,
            ShopItemCond { What: GiveItem.Potion } s => s.Item == potion,
            _ => false
        });

    public static bool TargetsEncounter(IEnumerable<Cond> conds, int enc) =>
        FlattenAll(conds).Any(c => c switch
        {
            BossCond b => b.Encounter == enc,
            SecondBossCond sb => sb.Encounter == enc,
            EncounterCond ec => ec.Encounter == enc,
            _ => false
        });

    public static bool TargetsEvent(IEnumerable<Cond> conds, int ev) =>
        FlattenAll(conds).Any(c => c is EventCond ec && ec.Event == ev);

    public static bool TargetsAncient(IEnumerable<Cond> conds, int anc) =>
        FlattenAll(conds).Any(c => c is AncientCond ac && ac.Ancient == anc);

    public static bool TargetsAct(IEnumerable<Cond> conds, int act) =>
        FlattenAll(conds).Any(c => c is ActCond ac && ac.Act == act);

    protected static bool Within(int[][] lists, int slot, int item, int within) =>
        slot < lists.Length && lists[slot].Take(within).Contains(item);

    protected static string First(int within) => within == 1 ? "first" : $"in the first {within}";
}

public sealed record NeowOfferCond(int Relic) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "neow_offer");
        w.WriteNumber("relic", Relic);
    }

    public override bool Matches(SimResult r) => r.Neow.Contains(Relic);

    public override string Describe(GameData d) => $"Neow offers {d.RelicName(Relic)}";
}

public enum GiveItem
{
    Card,
    Relic,
    Potion
}

public sealed record NeowGivesCond(int Relic, GiveItem What, int Item) : Cond
{
    public override SimParts Needs => SimParts.NeowOutcomes;

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "neow_gives");
        w.WriteNumber("relic", Relic);
        w.WriteString("what", What.ToString().ToLowerInvariant());
        w.WriteNumber("item", Item);
    }

    public override bool Matches(SimResult r)
    {
        int offer = Array.IndexOf(r.Neow, Relic);
        if (offer < 0 || offer >= r.NeowOutcomes.Length)
            return false;
        return r.NeowOutcomes[offer].Of(What).Contains(Item);
    }

    public string ItemName(GameData d) => What switch
    {
        GiveItem.Card => d.CardName(Item),
        GiveItem.Relic => d.RelicName(Item),
        _ => d.PotionName(Item)
    };

    public override string Describe(GameData d) => $"Neow offers {d.RelicName(Relic)} -> {ItemName(d)}";
}

// Neow offers Relic (Neow's Bones), taking it gives the relic Via, and that gives the item.
public sealed record NeowNestedCond(int Relic, int Via, GiveItem What, int Item) : Cond
{
    public override SimParts Needs => SimParts.NeowOutcomes;

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "neow_nested");
        w.WriteNumber("relic", Relic);
        w.WriteNumber("via", Via);
        w.WriteString("what", What.ToString().ToLowerInvariant());
        w.WriteNumber("item", Item);
    }

    // For some order of taking the relics. Cond.AllMatch asks for one order that suits them all.
    public override bool Matches(SimResult r)
    {
        int offer = Array.IndexOf(r.Neow, Relic);
        if (offer < 0 || offer >= r.NeowOutcomes.Length)
            return false;
        NeowOutcome o = r.NeowOutcomes[offer];
        return Enumerable.Range(0, o.Relics.Length).Any(first => Holds(o, first));
    }

    // Whether this holds when the relic at index takenFirst of the outcome is the one taken first.
    public bool Holds(NeowOutcome outcome, int takenFirst)
    {
        int i = Array.IndexOf(outcome.Relics, Via);
        if (i < 0 || i >= outcome.Nested.Length)
            return false;
        return (i == takenFirst ? outcome.Nested[i].First : outcome.Nested[i].Second).Of(What).Contains(Item);
    }

    public string ItemName(GameData d) => What switch
    {
        GiveItem.Card => d.CardName(Item),
        GiveItem.Relic => d.RelicName(Item),
        _ => d.PotionName(Item)
    };

    public override string Describe(GameData d) => $"Neow offers {d.RelicName(Relic)} -> {d.RelicName(Via)} -> {ItemName(d)}";
}

public sealed record ActCond(int Slot, int Act) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "act");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("act", Act);
    }

    public override bool Matches(SimResult r) => Slot < r.Acts.Length && r.Acts[Slot] == Act;

    public override string Describe(GameData d) => $"Act {Slot + 1} is {d.ActName(Act)}";
}

public sealed record BossCond(int Slot, int Encounter) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "boss");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("enc", Encounter);
    }

    public override bool Matches(SimResult r) => Slot < r.Bosses.Length && r.Bosses[Slot] == Encounter;

    public override string Describe(GameData d) => $"Act {Slot + 1} boss is {d.EncounterName(Encounter)}";
}

public sealed record SecondBossCond(int Encounter) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "second_boss");
        w.WriteNumber("enc", Encounter);
    }

    public override bool Matches(SimResult r) => r.SecondBoss == Encounter;

    public override string Describe(GameData d) => $"Second final boss is {d.EncounterName(Encounter)}";
}

public sealed record AncientCond(int Slot, int Ancient) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "ancient");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("ancient", Ancient);
    }

    public override bool Matches(SimResult r) => Slot < r.Ancients.Length && r.Ancients[Slot] == Ancient;

    public override string Describe(GameData d) => $"Act {Slot + 1} ancient is {d.AncientName(Ancient)}";
}

public sealed record AncientRelicCond(int Slot, int Relic) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "ancient_relic");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("relic", Relic);
    }

    public override bool Matches(SimResult r) => Slot < r.AncientRelics.Length && r.AncientRelics[Slot].Contains(Relic);

    public override string Describe(GameData d)
    {
        string? owner = d.AncientOwnerOfRelic(Slot, Relic);
        string prefix = owner != null ? $"Act {Slot + 1} ({owner})" : $"Act {Slot + 1} Ancient";
        return $"{prefix} offers {d.RelicName(Relic)}";
    }
}

public sealed record EventCond(int Slot, int Event, int WithinFirst) : Cond
{
    public override (int Value, int Max, Func<int, Cond> With)? WithinStepper =>
        (WithinFirst, 15, n => this with { WithinFirst = n });

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "event");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("event", Event);
        w.WriteNumber("within", WithinFirst);
    }

    public override bool Matches(SimResult r) => Within(r.Events, Slot, Event, WithinFirst);

    public override string Describe(GameData d) => $"Act {Slot + 1}: {d.EventName(Event)} is {First(WithinFirst)} event{(WithinFirst == 1 ? "" : "s")}";
}

public sealed record EncounterCond(int Slot, bool Elite, int Encounter, int WithinFirst) : Cond
{
    public override (int Value, int Max, Func<int, Cond> With)? WithinStepper =>
        (WithinFirst, 15, n => this with { WithinFirst = n });

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", Elite ? "elite" : "normal");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("enc", Encounter);
        w.WriteNumber("within", WithinFirst);
    }

    public override bool Matches(SimResult r) => Within(Elite ? r.Elites : r.Normals, Slot, Encounter, WithinFirst);

    public override string Describe(GameData d) =>
        $"Act {Slot + 1}: {d.EncounterName(Encounter)} is {First(WithinFirst)} {(Elite ? "elite" : "fight")}{(WithinFirst == 1 ? "" : "s")}";
}

public sealed record RelicCond(bool Shared, int Relic, int WithinFirst) : Cond
{
    public override (int Value, int Max, Func<int, Cond> With)? WithinStepper =>
        (WithinFirst, 10, n => this with { WithinFirst = n });

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "relic");
        w.WriteBoolean("shared", Shared);
        w.WriteNumber("relic", Relic);
        w.WriteNumber("within", WithinFirst);
    }

    public override bool Matches(SimResult r) => (Shared ? r.SharedRelics : r.PlayerRelics).Any(d => d.Relics.Take(WithinFirst).Contains(Relic));

    public override string Describe(GameData d) => $"{d.RelicName(Relic)} is {First(WithinFirst)} of its rarity ({(Shared ? "shared" : "your")} bag)";
}

public sealed record TreasureRelicCond(int Relic, int WithinFirst) : Cond
{
    public override (int Value, int Max, Func<int, Cond> With)? WithinStepper =>
        (WithinFirst, 6, n => this with { WithinFirst = n });

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "treasure_relic");
        w.WriteNumber("relic", Relic);
        w.WriteNumber("within", WithinFirst);
    }

    public override bool Matches(SimResult r) => r.TreasureRelics.Take(WithinFirst).Contains(Relic);

    public override string Describe(GameData d) => $"{d.RelicName(Relic)} is {First(WithinFirst)} chest relic{(WithinFirst == 1 ? "" : "s")}";
}

public sealed record ShopItemCond(GiveItem What, int Item) : Cond
{
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "shop_item");
        w.WriteString("what", What.ToString().ToLowerInvariant());
        w.WriteNumber("item", Item);
    }

    public override bool Matches(SimResult r) => What switch
    {
        GiveItem.Card => r.FirstShop.Cards.Contains(Item) || r.FirstShop.Colorless.Contains(Item),
        GiveItem.Relic => r.FirstShop.Relics.Contains(Item),
        _ => r.FirstShop.Potions.Contains(Item)
    };

    public string ItemName(GameData d) => What switch
    {
        GiveItem.Card => d.CardName(Item),
        GiveItem.Relic => d.RelicName(Item),
        _ => d.PotionName(Item)
    };

    public override string Describe(GameData d) => $"First shop sells {ItemName(d)}";
}

public sealed record MapCountCond(int Slot, int PointType, string TypeName, int Min, int Max) : Cond
{
    public override SimParts Needs => SimParts.Maps;

    public override (int Min, int Max, int Limit, Func<int, int, Cond> With)? RangeStepper =>
        (Min, Max, 15, (min, max) => this with { Min = min, Max = max });

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "map_count");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("point_type", PointType);
        w.WriteNumber("min", Min);
        w.WriteNumber("max", Max);
    }

    public override bool Matches(SimResult r)
    {
        if (Slot >= r.Maps.Length || r.Maps[Slot] is not { } map)
            return false;
        int n = map.Count(PointType);
        return n >= Min && n <= Max;
    }

    public override string Describe(GameData d) => $"Act {Slot + 1} map has {(Min == Max ? $"{Min}" : $"{Min}..{Max}")} total {TypeName}";
}

public sealed record MapPathCond(int Slot, int PointType, string TypeName, int Min, int Max) : Cond
{
    public override SimParts Needs => SimParts.Maps;

    public override (int Min, int Max, int Limit, Func<int, int, Cond> With)? RangeStepper =>
        (Min, Max, 10, (min, max) => this with { Min = min, Max = max });

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "map_path_count");
        w.WriteNumber("slot", Slot);
        w.WriteNumber("point_type", PointType);
        w.WriteNumber("min", Min);
        w.WriteNumber("max", Max);
    }

    public override bool Matches(SimResult r)
    {
        if (Slot >= r.Maps.Length || r.Maps[Slot] is not { } map)
            return false;
        int n = map.MaxOnPath(PointType);
        return n >= Min && n <= Max;
    }

    public override string Describe(GameData d) => $"Act {Slot + 1} best path has {(Min == Max ? $"{Min}" : $"{Min}..{Max}")} {TypeName}";
}

public sealed record RewardCardCond(int Card, int? AfterNeow = null) : Cond
{
    public override SimParts Needs => AfterNeow.HasValue ? SimParts.NeowOutcomes : SimParts.Reward;

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "reward_card");
        w.WriteNumber("card", Card);
        if (AfterNeow is { } relic)
            w.WriteNumber("after_neow", relic);
    }

    public override bool Matches(SimResult r)
    {
        if (AfterNeow is { } relic)
        {
            int idx = Array.IndexOf(r.Neow, relic);
            return idx >= 0 && idx < r.NeowOutcomes.Length && r.NeowOutcomes[idx].FirstCards.Contains(Card);
        }

        return r.Cards.Contains(Card);
    }

    public override string Describe(GameData d) =>
        AfterNeow is { } relic
            ? $"Floor 1 reward (after {d.RelicName(relic)}) has {d.CardName(Card)}"
            : $"Floor 1 card reward has {d.CardName(Card)}";
}

public sealed record RewardPotionCond(bool Present) : Cond
{
    public override SimParts Needs => SimParts.Reward;

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "reward_potion");
        w.WriteBoolean("present", Present);
    }

    public override bool Matches(SimResult r) => r.Potion >= 0 == Present;

    public override string Describe(GameData d) => Present ? "First fight drops a potion" : "First fight drops no potion";
}

public sealed record AnyOfCond(Cond[] Conds) : Cond
{
    public override SimParts Needs => Conds.Aggregate(SimParts.None, (acc, c) => acc | c.Needs);

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "any_of");
        w.WriteStartArray("conds");
        foreach (Cond c in Conds)
        {
            w.WriteStartObject();
            c.Write(w);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    public override bool Matches(SimResult r) => Conds.Any(c => c.Matches(r));

    public override string Describe(GameData d) => $"ANY OF: {string.Join("  OR  ", Conds.Select(c => c.Describe(d)))}";

    public bool Equals(AnyOfCond? other) => other != null && Conds.AsSpan().SequenceEqual(other.Conds);

    public override int GetHashCode()
    {
        HashCode hc = new();
        foreach (Cond c in Conds)
            hc.Add(c);
        return hc.ToHashCode();
    }
}
