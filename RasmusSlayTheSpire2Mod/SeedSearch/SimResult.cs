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

    public int Count(int pointType) => Points.Count(p => p[2] == pointType);
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
    public int[][] Events = [];
    public int[][] Normals = [];
    public int[][] Elites = [];
    public List<RelicDeque> SharedRelics = [];
    public List<RelicDeque> PlayerRelics = [];
    // Two positive options, then the cursed one.
    public int[] Neow = [];
    public int Gold;
    public int Potion = -1;
    public int[] Cards = [];
    // Null entries were not simulated (or the game would have failed to build the map).
    public MapResult?[] Maps = [];

    public static SimResult FromJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        return new SimResult
        {
            Seed = r.GetProperty("seed").GetString() ?? "",
            Acts = Ints(r.GetProperty("acts")),
            Bosses = Ints(r.GetProperty("bosses")),
            SecondBoss = r.GetProperty("second_boss").GetInt32(),
            Ancients = Ints(r.GetProperty("ancients")),
            Events = IntLists(r.GetProperty("events")),
            Normals = IntLists(r.GetProperty("normals")),
            Elites = IntLists(r.GetProperty("elites")),
            SharedRelics = Deques(r.GetProperty("shared_relics")),
            PlayerRelics = Deques(r.GetProperty("player_relics")),
            Neow = Ints(r.GetProperty("neow")),
            Gold = r.GetProperty("gold").GetInt32(),
            Potion = r.GetProperty("potion").GetInt32(),
            Cards = Ints(r.GetProperty("cards")),
            Maps = r.GetProperty("maps").EnumerateArray()
                .Select(m => m.ValueKind == JsonValueKind.Null
                    ? null
                    : new MapResult { Points = IntLists(m.GetProperty("points")), Edges = IntLists(m.GetProperty("edges")) })
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
        w.WriteIntLists("events", Events);
        w.WriteIntLists("normals", Normals);
        w.WriteIntLists("elites", Elites);
        WriteDeques(w, "shared_relics", SharedRelics);
        WriteDeques(w, "player_relics", PlayerRelics);
        w.WriteInts("neow", Neow);
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
            ?? Diff("second boss", [a.SecondBoss], [b.SecondBoss])
            ?? Diff("neow", a.Neow, b.Neow)
            ?? Diff("first reward", [a.Gold, a.Potion, .. a.Cards], [b.Gold, b.Potion, .. b.Cards])
            ?? DiffMaps(a.Maps, b.Maps);
    }

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

            string? diff = Diff($"act {i + 1} map points", a[i]!.Points, b[i]!.Points) ?? Diff($"act {i + 1} map edges", a[i]!.Edges, b[i]!.Edges);
            if (diff != null)
                return diff;
        }

        return null;
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
