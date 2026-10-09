using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Development aid, inert unless the environment variable RASMUS_SEED_SELFCHECK is set to a seed
// count. On reaching the main menu it compares the Rust engine with the game's own generation
// for every character at ascension 0 and 10, and logs the outcome to godot.log.
// RASMUS_SEED_FIXTURES=<folder> also writes the compared cases there as golden fixtures for
// `cargo test` (seed-engine/tests/fixtures), and RASMUS_SEED_QUIT=1 quits the game afterwards.
[HarmonyPatch(typeof(NMainMenu), nameof(NMainMenu._Ready))]
public static class DevSelfCheck
{
    private static bool _ran;

    static void Postfix(NMainMenu __instance)
    {
        if (_ran || !int.TryParse(System.Environment.GetEnvironmentVariable("RASMUS_SEED_SELFCHECK"), out int seeds))
            return;

        _ran = true;
        try
        {
            Run(seeds);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Seed engine self-check crashed. {e}");
        }

        if (System.Environment.GetEnvironmentVariable("RASMUS_SEED_QUIT") == "1")
            __instance.GetTree().Quit();
    }

    private static void Run(int seeds)
    {
        if (!NativeEngine.IsAvailable)
        {
            MainFile.Logger.Error("Seed engine self-check: FAIL, the native library is not available.");
            return;
        }

        string? fixtures = System.Environment.GetEnvironmentVariable("RASMUS_SEED_FIXTURES");
        int failures = 0;
        bool benchmarked = false;
        foreach (CharacterModel character in ModelDb.AllCharacters)
        {
            foreach (int ascension in new[] { 0, 10 })
            {
                string name = $"{character.Id.Entry.ToLowerInvariant()}_a{ascension}";
                GameData data = GameData.Build(character, ascension, "random");
                using NativeEngine native = NativeEngine.Create(data.SnapshotJson);
                List<SimResult> cases = [];
                string? mismatch = SeedEngine.SelfCheck(data, native, seeds, cases);
                if (mismatch == null)
                {
                    MainFile.Logger.Info($"Seed engine self-check: pass {name}, {seeds} seeds");
                    if (!string.IsNullOrEmpty(fixtures))
                        WriteFixture(Path.Combine(fixtures, $"{name}.json"), data, cases);
                }
                else
                {
                    failures++;
                    MainFile.Logger.Error($"Seed engine self-check: FAIL {name}: {mismatch}");
                }

                if (!benchmarked && ascension == 10)
                {
                    benchmarked = true;
                    Benchmark(native);
                }
            }
        }

        MainFile.Logger.Info($"Seed engine self-check: DONE, {failures} failures");
    }

    private static void Benchmark(NativeEngine native)
    {
        // Conditions that never match, so the search runs flat out for the whole second.
        (string Name, Cond Cond)[] runs =
        [
            ("neow", new NeowOfferCond(999999)),
            ("up-front", new BossCond(0, -2)),
            ("reward", new RewardCardCond(999999)),
            ("map", new MapCountCond(0, GameData.MapElite, "elites", 99, 99))
        ];
        foreach ((string name, Cond cond) in runs)
        {
            using ISeedSearch search = native.StartSearch(Cond.ToJson([cond]), Math.Max(1, System.Environment.ProcessorCount - 1), 1);
            Thread.Sleep(1000);
            SearchProgress p = search.Poll();
            MainFile.Logger.Info($"Seed engine benchmark: {name} filter, {p.SeedsPerSecond:N0} seeds/s on {p.Threads} threads");
        }
    }

    private static void WriteFixture(string path, GameData data, List<SimResult> cases)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter w = new(stream);
        w.WriteStartObject();
        w.WritePropertyName("snapshot");
        w.WriteRawValue(data.SnapshotJson);
        w.WriteStartArray("cases");
        foreach (SimResult c in cases)
            c.Write(w);
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
