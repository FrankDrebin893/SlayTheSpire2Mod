using System.Text;
using System.Text.Json;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Relics of one rarity, in the order a grab bag hands them out.
public sealed class RelicDeque
{
    public int Rarity;
    public int[] Relics = [];
}

public sealed class MapResult
{
    // [col, row, MapPointType], column by column.
    public int[][] Points = [];
    // [col, row, col, row] between grid points, sorted.
    public int[][] Edges = [];
    public int MaxPathElites;
    public int MaxPathRests;
    public int MaxPathShops;
    public int MaxPathUnknowns;

    public int Count(int pointType) => Points.Count(p => p[2] == pointType);

    public int MaxOnPath(int pointType) => pointType switch
    {
        GameData.MapElite => MaxPathElites,
        GameData.MapRest => MaxPathRests,
        GameData.MapShop => MaxPathShops,
        GameData.MapUnknown => MaxPathUnknowns,
        _ => 0
    };

    public void ComputeMaxPaths()
    {
        MaxPathElites = CalcMaxPath(GameData.MapElite);
        MaxPathRests = CalcMaxPath(GameData.MapRest);
        MaxPathShops = CalcMaxPath(GameData.MapShop);
        MaxPathUnknowns = CalcMaxPath(GameData.MapUnknown);
    }

    private int CalcMaxPath(int targetType)
    {
        if (Points.Length == 0)
            return 0;
        int[] order = Enumerable.Range(0, Points.Length)
            .OrderBy(i => Points[i][1])
            .ThenBy(i => Points[i][0])
            .ToArray();
        int[] dp = new int[Points.Length];
        int best = 0;
        foreach (int i in order)
        {
            int c = Points[i][0];
            int r = Points[i][1];
            int selfVal = Points[i][2] == targetType ? 1 : 0;
            int bestPred = 0;
            foreach (int[] edge in Edges)
            {
                if (edge[2] == c && edge[3] == r)
                {
                    int predIdx = Array.FindIndex(Points, p => p[0] == edge[0] && p[1] == edge[1]);
                    if (predIdx >= 0 && dp[predIdx] > bestPred)
                        bestPred = dp[predIdx];
                }
            }

            dp[i] = bestPred + selfVal;
            if (dp[i] > best)
                best = dp[i];
        }

        return best;
    }
}

public sealed class ShopResult
{
    public int[] Cards = [];
    public int SaleIndex;
    public int[] Colorless = [];
    public int[] Relics = [];
    public int[] Potions = [];
}

// What one Neow relic hands out when it is taken, assuming it is the first thing to draw from the
// streams it uses. Empty for relics that roll nothing.
public sealed class NeowOutcome
{
    public int[] Cards = [];
    public int[] Relics = [];
    public int[] Potions = [];
    // One per entry of Relics for Neow's Bones, whose relics roll things of their own.
    public NestedOutcome[] Nested = [];
    // Floor 1 combat reward after taking this Neow relic.
    public int FirstGold;
    public int FirstPotion = -1;
    public int[] FirstCards = [];

    public bool IsEmpty => Cards.Length + Relics.Length + Potions.Length == 0;

    public int[] Of(GiveItem what) => what switch
    {
        GiveItem.Card => Cards,
        GiveItem.Relic => Relics,
        _ => Potions
    };

    public bool SameAs(NeowOutcome o) =>
        Cards.AsSpan().SequenceEqual(o.Cards)
        && Relics.AsSpan().SequenceEqual(o.Relics)
        && Potions.AsSpan().SequenceEqual(o.Potions);

    public bool SameFirstReward(int gold, int potion, int[] cards) =>
        FirstGold == gold && FirstPotion == potion && FirstCards.AsSpan().SequenceEqual(cards);
}

// What a relic given by Neow's Bones gives. The player takes the relics of Neow's Bones in either
// order, and the one taken second carries on from the streams the first one used.
public sealed class NestedOutcome
{
    public NeowOutcome First = new();
    public NeowOutcome Second = new();
}

// What a seed generates at the start of a run, as ids into the tables of a GameData. Produced by
// the Rust engine (FromJson) and by SeedSimulator, in the same shape so the two can be compared.
// -1 stands for "none".
public sealed class SimResult
{
    public string Seed = "";
    public int[] Acts = [];
    public int[] Bosses = [];
    public int SecondBoss = -1;
    public int[] Ancients = [];
    public int[][] AncientRelics = [];
    public int[][] Events = [];
    public int[][] Normals = [];
    public int[][] Elites = [];
    public List<RelicDeque> SharedRelics = [];
    public List<RelicDeque> PlayerRelics = [];
    public int[] TreasureRelics = [];
    public ShopResult FirstShop = new();
    // Two positive options, then the cursed one.
    public int[] Neow = [];
    // One per offer in Neow; empty when not simulated.
    public NeowOutcome[] NeowOutcomes = [];
    public int Gold;
    public int Potion = -1;
    public int[] Cards = [];
    // Null entries were not simulated (or the game would have failed to build the map).
    public MapResult?[] Maps = [];

    public static SimResult FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        JsonElement shopElem = r.GetProperty("first_shop");
        return new SimResult
        {
            Seed = r.GetProperty("seed").GetString() ?? "",
            Acts = Ints(r.GetProperty("acts")),
            Bosses = Ints(r.GetProperty("bosses")),
            SecondBoss = r.GetProperty("second_boss").GetInt32(),
            Ancients = Ints(r.GetProperty("ancients")),
            AncientRelics = r.TryGetProperty("ancient_relics", out JsonElement ar) ? IntLists(ar) : [],
            Events = IntLists(r.GetProperty("events")),
            Normals = IntLists(r.GetProperty("normals")),
            Elites = IntLists(r.GetProperty("elites")),
            SharedRelics = Deques(r.GetProperty("shared_relics")),
            PlayerRelics = Deques(r.GetProperty("player_relics")),
            TreasureRelics = r.TryGetProperty("treasure_relics", out JsonElement tr) ? Ints(tr) : [],
            FirstShop = new ShopResult
            {
                Cards = Ints(shopElem.GetProperty("cards")),
                SaleIndex = shopElem.GetProperty("sale_index").GetInt32(),
                Colorless = Ints(shopElem.GetProperty("colorless")),
                Relics = Ints(shopElem.GetProperty("relics")),
                Potions = Ints(shopElem.GetProperty("potions"))
            },
            Neow = Ints(r.GetProperty("neow")),
            NeowOutcomes = r.GetProperty("neow_outcomes").EnumerateArray()
                .Select(o =>
                {
                    NeowOutcome outcome = Given(o);
                    outcome.Nested = o.GetProperty("nested").EnumerateArray()
                        .Select(n => new NestedOutcome { First = Given(n.GetProperty("first")), Second = Given(n.GetProperty("second")) })
                        .ToArray();
                    if (o.TryGetProperty("first_gold", out JsonElement fg))
                        outcome.FirstGold = fg.GetInt32();
                    if (o.TryGetProperty("first_potion", out JsonElement fp))
                        outcome.FirstPotion = fp.GetInt32();
                    if (o.TryGetProperty("first_cards", out JsonElement fc))
                        outcome.FirstCards = Ints(fc);
                    return outcome;
                })
                .ToArray(),
            Gold = r.GetProperty("gold").GetInt32(),
            Potion = r.GetProperty("potion").GetInt32(),
            Cards = Ints(r.GetProperty("cards")),
            Maps = r.GetProperty("maps").EnumerateArray()
                .Select(m => m.ValueKind == JsonValueKind.Null
                    ? null
                    : new MapResult
                    {
                        Points = IntLists(m.GetProperty("points")),
                        Edges = IntLists(m.GetProperty("edges")),
                        MaxPathElites = m.TryGetProperty("max_path_elites", out JsonElement me) ? me.GetInt32() : 0,
                        MaxPathRests = m.TryGetProperty("max_path_rests", out JsonElement mr) ? mr.GetInt32() : 0,
                        MaxPathShops = m.TryGetProperty("max_path_shops", out JsonElement ms) ? ms.GetInt32() : 0,
                        MaxPathUnknowns = m.TryGetProperty("max_path_unknowns", out JsonElement mu) ? mu.GetInt32() : 0
                    })
                .ToArray()
        };
    }

    public string ToJson()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            Write(w);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("seed", Seed);
        w.WriteInts("acts", Acts);
        w.WriteInts("bosses", Bosses);
        w.WriteNumber("second_boss", SecondBoss);
        w.WriteInts("ancients", Ancients);
        w.WriteIntLists("ancient_relics", AncientRelics);
        w.WriteIntLists("events", Events);
        w.WriteIntLists("normals", Normals);
        w.WriteIntLists("elites", Elites);
        WriteDeques(w, "shared_relics", SharedRelics);
        WriteDeques(w, "player_relics", PlayerRelics);
        w.WriteInts("treasure_relics", TreasureRelics);
        w.WriteStartObject("first_shop");
        w.WriteInts("cards", FirstShop.Cards);
        w.WriteNumber("sale_index", FirstShop.SaleIndex);
        w.WriteInts("colorless", FirstShop.Colorless);
        w.WriteInts("relics", FirstShop.Relics);
        w.WriteInts("potions", FirstShop.Potions);
        w.WriteEndObject();
        w.WriteInts("neow", Neow);
        w.WriteStartArray("neow_outcomes");
        foreach (NeowOutcome o in NeowOutcomes)
        {
            w.WriteStartObject();
            WriteGiven(w, o);
            w.WriteStartArray("nested");
            foreach (NestedOutcome n in o.Nested)
            {
                w.WriteStartObject();
                w.WriteStartObject("first");
                WriteGiven(w, n.First);
                w.WriteEndObject();
                w.WriteStartObject("second");
                WriteGiven(w, n.Second);
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("first_gold", o.FirstGold);
            w.WriteNumber("first_potion", o.FirstPotion);
            w.WriteInts("first_cards", o.FirstCards);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteNumber("gold", Gold);
        w.WriteNumber("potion", Potion);
        w.WriteInts("cards", Cards);
        w.WriteStartArray("maps");
        foreach (MapResult? map in Maps)
        {
            if (map == null)
            {
                w.WriteNullValue();
                continue;
            }

            w.WriteStartObject();
            w.WriteIntLists("points", map.Points);
            w.WriteIntLists("edges", map.Edges);
            w.WriteNumber("max_path_elites", map.MaxPathElites);
            w.WriteNumber("max_path_rests", map.MaxPathRests);
            w.WriteNumber("max_path_shops", map.MaxPathShops);
            w.WriteNumber("max_path_unknowns", map.MaxPathUnknowns);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    // Names the first field in which two results differ, or null when they agree.
    public static string? FirstDifference(SimResult a, SimResult b)
    {
        return Diff("acts", a.Acts, b.Acts)
            ?? Diff("shared relics", Flatten(a.SharedRelics), Flatten(b.SharedRelics))
            ?? Diff("player relics", Flatten(a.PlayerRelics), Flatten(b.PlayerRelics))
            ?? Diff("events", a.Events, b.Events)
            ?? Diff("normal encounters", a.Normals, b.Normals)
            ?? Diff("elite encounters", a.Elites, b.Elites)
            ?? Diff("bosses", a.Bosses, b.Bosses)
            ?? Diff("ancients", a.Ancients, b.Ancients)
            ?? Diff("ancient relics", a.AncientRelics, b.AncientRelics)
            ?? Diff("second boss", [a.SecondBoss], [b.SecondBoss])
            ?? Diff("treasure relics", a.TreasureRelics, b.TreasureRelics)
            ?? Diff("first shop", FlattenShop(a.FirstShop), FlattenShop(b.FirstShop))
            ?? Diff("neow", a.Neow, b.Neow)
            ?? Diff("neow outcomes", Flatten(a.NeowOutcomes), Flatten(b.NeowOutcomes))
            ?? Diff("first reward", [a.Gold, a.Potion, .. a.Cards], [b.Gold, b.Potion, .. b.Cards])
            ?? DiffMaps(a.Maps, b.Maps);
    }

    private static int[] FlattenShop(ShopResult s) =>
        [.. s.Cards, -1, s.SaleIndex, -1, .. s.Colorless, -1, .. s.Relics, -1, .. s.Potions];

    private static string? DiffMaps(MapResult?[] a, MapResult?[] b)
    {
        if (a.Length != b.Length)
            return $"map count: {a.Length} vs {b.Length}";
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == null || b[i] == null)
            {
                if (a[i] != b[i])
                    return $"act {i + 1} map: only one side built it";
                continue;
            }

            string? diff = Diff($"act {i + 1} map points", a[i]!.Points, b[i]!.Points)
                ?? Diff($"act {i + 1} map edges", a[i]!.Edges, b[i]!.Edges)
                ?? Diff($"act {i + 1} map max paths",
                    [a[i]!.MaxPathElites, a[i]!.MaxPathRests, a[i]!.MaxPathShops, a[i]!.MaxPathUnknowns],
                    [b[i]!.MaxPathElites, b[i]!.MaxPathRests, b[i]!.MaxPathShops, b[i]!.MaxPathUnknowns]);
            if (diff != null)
                return diff;
        }

        return null;
    }

    private static int[][] Flatten(NeowOutcome[] outcomes) =>
        outcomes.Select(o => (int[]) [
            .. Flatten(o),
            -3, o.FirstGold, o.FirstPotion, .. o.FirstCards,
            .. o.Nested.SelectMany(n => (int[]) [-2, .. Flatten(n.First), -2, .. Flatten(n.Second)])
        ]).ToArray();

    private static int[] Flatten(NeowOutcome o) => [.. o.Cards, -1, .. o.Relics, -1, .. o.Potions];

    private static NeowOutcome Given(JsonElement o) =>
        new() { Cards = Ints(o.GetProperty("cards")), Relics = Ints(o.GetProperty("relics")), Potions = Ints(o.GetProperty("potions")) };

    private static void WriteGiven(Utf8JsonWriter w, NeowOutcome o)
    {
        w.WriteInts("cards", o.Cards);
        w.WriteInts("relics", o.Relics);
        w.WriteInts("potions", o.Potions);
    }

    private static int[][] Flatten(List<RelicDeque> deques) => deques.Select(d => (int[]) [d.Rarity, .. d.Relics]).ToArray();

    private static string? Diff(string name, int[][] a, int[][] b)
    {
        if (a.Length != b.Length)
            return $"{name}: {a.Length} lists vs {b.Length}";
        for (int i = 0; i < a.Length; i++)
        {
            string? diff = Diff($"{name}[{i}]", a[i], b[i]);
            if (diff != null)
                return diff;
        }

        return null;
    }

    private static string? Diff(string name, int[] a, int[] b) =>
        a.AsSpan().SequenceEqual(b) ? null : $"{name}: [{string.Join(",", a)}] vs [{string.Join(",", b)}]";

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    private static int[][] IntLists(JsonElement e) => e.EnumerateArray().Select(Ints).ToArray();

    private static List<RelicDeque> Deques(JsonElement e) =>
        e.EnumerateArray().Select(d => new RelicDeque { Rarity = d.GetProperty("rarity").GetInt32(), Relics = Ints(d.GetProperty("relics")) }).ToList();

    private static void WriteDeques(Utf8JsonWriter w, string name, List<RelicDeque> deques)
    {
        w.WriteStartArray(name);
        foreach (RelicDeque d in deques)
        {
            w.WriteStartObject();
            w.WriteNumber("rarity", d.Rarity);
            w.WriteInts("relics", d.Relics);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}

internal static class JsonWriterExtensions
{
    public static void WriteInts(this Utf8JsonWriter w, string name, IEnumerable<int> values)
    {
        w.WriteStartArray(name);
        foreach (int v in values)
            w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    public static void WriteInts(this Utf8JsonWriter w, IEnumerable<int> values)
    {
        w.WriteStartArray();
        foreach (int v in values)
            w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    public static void WriteIntLists(this Utf8JsonWriter w, string name, IEnumerable<IEnumerable<int>> lists)
    {
        w.WriteStartArray(name);
        foreach (IEnumerable<int> list in lists)
            w.WriteInts(list);
        w.WriteEndArray();
    }
}
