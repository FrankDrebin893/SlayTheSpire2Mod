using System.Text;
using System.Text.Json;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// One search condition; a seed must satisfy all of them. Write produces the form the Rust engine
// reads (seed-engine/src/sim.rs, enum Cond) and Matches is the same test for the C# fallback.
public abstract record Cond
{
    public virtual SimParts Needs => SimParts.None;

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
        NeowOutcome o = r.NeowOutcomes[offer];
        return (What switch { GiveItem.Card => o.Cards, GiveItem.Relic => o.Relics, _ => o.Potions }).Contains(Item);
    }

    public string ItemName(GameData d) => What switch
    {
        GiveItem.Card => d.CardName(Item),
        GiveItem.Relic => d.RelicName(Item),
        _ => d.PotionName(Item)
    };

    public override string Describe(GameData d) => $"Neow offers {d.RelicName(Relic)} and it gives {ItemName(d)}";
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

public sealed record EventCond(int Slot, int Event, int WithinFirst) : Cond
{
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
    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "relic");
        w.WriteBoolean("shared", Shared);
        w.WriteNumber("relic", Relic);
        w.WriteNumber("within", WithinFirst);
    }

    public override bool Matches(SimResult r) => (Shared ? r.SharedRelics : r.PlayerRelics).Any(d => d.Relics.Take(WithinFirst).Contains(Relic));

    public override string Describe(GameData d) => $"{d.RelicName(Relic)} is {First(WithinFirst)} of its rarity ({(Shared ? "shared" : "your")} relic bag)";
}

public sealed record MapCountCond(int Slot, int PointType, string TypeName, int Min, int Max) : Cond
{
    public override SimParts Needs => SimParts.Maps;

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

    public override string Describe(GameData d) => $"Act {Slot + 1} map has {(Min == Max ? $"{Min}" : $"{Min} to {Max}")} {TypeName}";
}

public sealed record RewardCardCond(int Card) : Cond
{
    public override SimParts Needs => SimParts.Reward;

    public override void Write(Utf8JsonWriter w)
    {
        w.WriteString("kind", "reward_card");
        w.WriteNumber("card", Card);
    }

    public override bool Matches(SimResult r) => r.Cards.Contains(Card);

    public override string Describe(GameData d) => $"First card reward has {d.CardName(Card)}";
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
