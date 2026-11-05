using System.Text.Json;
using System.Text.Json.Nodes;

namespace RasmusCardBalance;

// Which tweaks are on, stored in settings.json under <user data>/RasmusCardBalance/, not in the mod
// folder: the mod loader reads every .json in a mod folder as a manifest.
//
// Tweaks are applied once at startup (MainFile.Initialize), so a change only takes effect after a
// game restart. Applied remembers what this session actually started with.
public static class Settings
{
    private static readonly string FilePath =
        Path.Combine(Godot.OS.GetUserDataDir(), MainFile.ModId, "settings.json");

    private static bool _loaded;
    private static readonly Dictionary<string, bool> Values = new();
    private static readonly Dictionary<string, bool> Applied = new();

    public static bool IsEnabled(Tweak tweak)
    {
        Load();
        return Values.TryGetValue(tweak.Id, out var v) ? v : tweak.DefaultEnabled;
    }

    public static void SetEnabled(Tweak tweak, bool enabled)
    {
        Load();
        if (IsEnabled(tweak) == enabled) return;
        Values[tweak.Id] = enabled;
        Save();
        MainFile.Logger.Info($"{tweak.Name} turned {(enabled ? "on" : "off")}; takes effect after a game restart.");
    }

    public static void MarkApplied(Tweak tweak, bool enabled) => Applied[tweak.Id] = enabled;

    // True when the saved choice differs from what is active in this session.
    public static bool NeedsRestart(Tweak tweak) =>
        Applied.TryGetValue(tweak.Id, out var a) && a != IsEnabled(tweak);

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            if (JsonNode.Parse(File.ReadAllText(FilePath))?["tweaks"] is JsonObject tweaks)
            {
                foreach (var (id, node) in tweaks)
                {
                    if (node is JsonValue value && value.TryGetValue<bool>(out var b)) Values[id] = b;
                }
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not read {FilePath}; all tweaks use their defaults. {e}");
        }
    }

    private static void Save()
    {
        try
        {
            var tweaks = new JsonObject();
            foreach (var (id, enabled) in Values) tweaks[id] = enabled;
            var root = new JsonObject { ["tweaks"] = tweaks };

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
