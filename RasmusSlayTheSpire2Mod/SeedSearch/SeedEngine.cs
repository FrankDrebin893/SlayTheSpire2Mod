using System.Diagnostics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// What the search screen talks to: the game data for the current run setup, plus either the
// Rust engine or, when that is missing or disagrees with the game, the C# simulator.
public sealed class SeedEngine
{
    private const int SelfCheckSeeds = 20;

    private static SeedEngine? _cached;

    private readonly NativeEngine? _native;

    public GameData Data { get; }

    // Shown in the screen header.
    public string Status { get; }

    public bool IsNative => _native != null;

    private SeedEngine(GameData data, NativeEngine? native, string status)
    {
        Data = data;
        _native = native;
        Status = status;
    }

    // Reuses the previous engine while the exported game data is unchanged, so ids held by the
    // screen's filters stay valid and the self-check is not repeated.
    public static SeedEngine For(CharacterModel character, int ascension, string act1Key)
    {
        GameData data = GameData.Build(character, ascension, act1Key);
        if (_cached != null && _cached.Data.Character.Id == character.Id && _cached.Data.SnapshotJson == data.SnapshotJson)
            return _cached;

        _cached?._native?.Dispose();
        _cached = Create(data);
        return _cached;
    }

    private static SeedEngine Create(GameData data)
    {
        if (!NativeEngine.IsAvailable)
            return new SeedEngine(data, null, "C# fallback (slow): Rust engine not available");

        try
        {
            NativeEngine native = NativeEngine.Create(data.SnapshotJson);
            string? mismatch = SelfCheck(data, native, SelfCheckSeeds);
            if (mismatch == null)
                return new SeedEngine(data, native, $"Rust engine, {ThreadCount} threads");

            native.Dispose();
            MainFile.Logger.Error($"Seed engine disagrees with the game ({mismatch}). The game probably updated. Using the slow C# search.");
            return new SeedEngine(data, null, "C# fallback (slow): Rust engine failed its self-check, see godot.log");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Seed engine could not be started. Using the slow C# search. {e}");
            return new SeedEngine(data, null, "C# fallback (slow): Rust engine failed to start, see godot.log");
        }
    }

    private static int ThreadCount => Math.Max(1, System.Environment.ProcessorCount - 1);

    // Simulates random seeds with both implementations. Returns the first disagreement, or null.
    public static string? SelfCheck(GameData data, NativeEngine native, int seeds, List<SimResult>? collect = null)
    {
        for (int i = 0; i < seeds; i++)
        {
            string seed = SeedHelper.GetRandomSeed();
            SimResult? expected = SeedSimulator.Simulate(data, seed, SimParts.All);
            if (expected == null)
                return $"seed {seed}: the game's own generation failed";
            collect?.Add(expected);
            string? diff = SimResult.FirstDifference(expected, native.Simulate(seed));
            if (diff != null)
                return $"seed {seed}, game vs engine, {diff}";
        }

        return null;
    }

    public SimResult? Simulate(string rawSeed)
    {
        string seed = SeedHelper.CanonicalizeSeed(rawSeed);
        if (seed.Length == 0)
            return null;
        try
        {
            return _native != null ? _native.Simulate(seed) : SeedSimulator.Simulate(Data, seed, SimParts.All);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Seed engine could not simulate seed {seed}. {e}");
            return null;
        }
    }

    public ISeedSearch StartSearch(IReadOnlyList<Cond> conds, int maxResults) =>
        _native != null
            ? _native.StartSearch(Cond.ToJson(conds), ThreadCount, maxResults)
            : new FallbackSearch(Data, conds, maxResults);

    // Tries random seeds with the game's own generation, a few milliseconds per poll, on the
    // main thread because the game code shares static state with the rest of the game.
    private sealed class FallbackSearch(GameData data, IReadOnlyList<Cond> conds, int maxResults) : ISeedSearch
    {
        private const double SliceMs = 8;

        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private readonly List<string> _results = [];
        private readonly SimParts _parts = conds.Aggregate(SimParts.None, (parts, c) => parts | c.Needs);
        private long _tried;
        private bool _running = true;

        public SearchProgress Poll()
        {
            Stopwatch slice = Stopwatch.StartNew();
            while (_running && slice.Elapsed.TotalMilliseconds < SliceMs)
            {
                SimResult? result = SeedSimulator.Simulate(data, SeedHelper.GetRandomSeed(), _parts);
                _tried++;
                if (result == null)
                    Stop();
                else if (conds.All(c => c.Matches(result)) && !_results.Contains(result.Seed))
                {
                    _results.Add(result.Seed);
                    if (_results.Count >= maxResults)
                        Stop();
                }
            }

            return new SearchProgress(_tried, _elapsed.Elapsed.TotalSeconds, _running, 1, _results.ToList());
        }

        public void Dispose() => Stop();

        private void Stop()
        {
            _running = false;
            _elapsed.Stop();
        }
    }
}
