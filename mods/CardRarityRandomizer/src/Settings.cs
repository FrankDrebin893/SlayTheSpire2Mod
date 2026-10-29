using System.Text.Json;
using System.Text.Json.Nodes;

namespace CardRarityRandomizer;

// The on/off switch for new runs, and which runs were started with it on.
//
// Stored in settings.json under <user data>/CardRarityRandomizer/, not in the mod folder: the
// mod loader reads every .json in a mod folder as a manifest.
//
// The switch only decides for new runs. Each run's decision is locked in at start under its seed,
// so a saved run reloads with the rarities it started with even if the switch was flipped since.
public static class Settings
{
    private const int MaxRuns = 100;

    private static readonly string FilePath =
        Path.Combine(Godot.OS.GetUserDataDir(), MainFile.ModId, "settings.json");

    private static bool _loaded;
    private static bool _enabled = true;
    // Oldest first, so the list can be trimmed from the front.
    private static readonly List<KeyValuePair<string, bool>> Runs = new();

    public static event Action? Changed;

    public static bool Enabled
    {
        get
        {
            Load();
            return _enabled;
        }
        set
        {
            Load();
            if (_enabled == value) return;
            _enabled = value;
            Save();
            MainFile.Logger.Info($"Card Rarity Randomizer turned {(value ? "on" : "off")} for new runs.");
            Changed?.Invoke();
        }
    }

    public static void RecordRun(string seed, bool randomized)
    {
        Load();
        Runs.RemoveAll(r => r.Key == seed || r.Key.StartsWith(seed + "@"));
        Runs.Add(new(seed, randomized));
        if (Runs.Count > MaxRuns) Runs.RemoveRange(0, Runs.Count - MaxRuns);
        Save();
    }

    public static bool? RecordedRun(string seed)
    {
        Load();
        // Check newest records first
        for (int i = Runs.Count - 1; i >= 0; i--)
        {
            var r = Runs[i];
            if (r.Key == seed || r.Key.StartsWith(seed + "@"))
                return r.Value;
        }
        return null;
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var root = JsonNode.Parse(File.ReadAllText(FilePath));
            _enabled = root?["enabled"]?.GetValue<bool>() ?? true;
            if (root?["runs"] is JsonArray runs)
            {
                foreach (var run in runs)
                {
                    var key = run?["key"]?.GetValue<string>();
                    if (key != null) Runs.Add(new(key, run!["randomized"]?.GetValue<bool>() ?? true));
                }
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not read {FilePath}; the randomizer stays on. {e}");
        }
    }

    private static void Save()
    {
        try
        {
            var runs = new JsonArray();
            foreach (var r in Runs) runs.Add(new JsonObject { ["key"] = r.Key, ["randomized"] = r.Value });
            var root = new JsonObject { ["enabled"] = _enabled, ["runs"] = runs };

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Written beside the real file and moved over it, so a crash cannot leave half a file.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, true);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not write {FilePath}. {e}");
        }
    }
}
