using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

public sealed record SearchProgress(long Tried, double ElapsedSeconds, bool Running, int Threads, IReadOnlyList<string> Results)
{
    public double SeedsPerSecond => Tried / Math.Max(ElapsedSeconds, 0.001);
}

public interface ISeedSearch : IDisposable
{
    // Reports progress; the C# fallback also does a slice of its work here.
    SearchProgress Poll();
}

// The Rust seed engine (seed-engine/, built to sts2_seed_engine.dll next to the mod dll).
public sealed class NativeEngine : IDisposable
{
    private const string Lib = "sts2_seed_engine";
    private const uint ExpectedAbi = 2;

    private static bool? _available;
    private IntPtr _handle;

    static NativeEngine()
    {
        // The game only probes its own folders, so point the loader at the mod folder.
        NativeLibrary.SetDllImportResolver(typeof(NativeEngine).Assembly, Resolve);
    }

    private NativeEngine(IntPtr handle)
    {
        _handle = handle;
    }

    // False when the library is missing (other platforms) or was built for another mod version.
    public static bool IsAvailable
    {
        get
        {
            if (_available == null)
            {
                try
                {
                    uint abi = sts2_abi_version();
                    _available = abi == ExpectedAbi;
                    if (abi != ExpectedAbi)
                        MainFile.Logger.Error($"Seed engine has ABI {abi}, expected {ExpectedAbi}. Using the slow C# search.");
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    _available = false;
                    MainFile.Logger.Info($"Seed engine library not loaded ({e.Message}). Using the slow C# search.");
                }
            }

            return _available.Value;
        }
    }

    public static NativeEngine Create(string snapshotJson)
    {
        byte[] json = Encoding.UTF8.GetBytes(snapshotJson);
        IntPtr handle = sts2_engine_create(json, (nuint)json.Length);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Seed engine rejected the game data: {LastError()}");
        return new NativeEngine(handle);
    }

    public SimResult Simulate(string seed)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(seed);
        return SimResult.FromJson(ReadText((buffer, cap) => sts2_engine_simulate(_handle, bytes, (nuint)bytes.Length, buffer, cap)));
    }

    public ISeedSearch StartSearch(string filterJson, int threads, int maxResults)
    {
        byte[] filter = Encoding.UTF8.GetBytes(filterJson);
        IntPtr search = sts2_search_start(_handle, filter, (nuint)filter.Length, (uint)threads, (uint)maxResults);
        if (search == IntPtr.Zero)
            throw new InvalidOperationException($"Seed engine could not start the search: {LastError()}");
        return new NativeSearch(search);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            sts2_engine_free(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private sealed class NativeSearch(IntPtr handle) : ISeedSearch
    {
        private IntPtr _search = handle;

        public SearchProgress Poll()
        {
            using JsonDocument doc = JsonDocument.Parse(ReadText((buffer, cap) => sts2_search_poll(_search, buffer, cap)));
            JsonElement r = doc.RootElement;
            return new SearchProgress(
                r.GetProperty("tried").GetInt64(),
                r.GetProperty("elapsed_ms").GetInt64() / 1000.0,
                r.GetProperty("running").GetBoolean(),
                r.GetProperty("threads").GetInt32(),
                r.GetProperty("results").EnumerateArray().Select(s => s.GetString()!).ToList());
        }

        public void Dispose()
        {
            if (_search != IntPtr.Zero)
            {
                sts2_search_free(_search);
                _search = IntPtr.Zero;
            }
        }
    }

    // Calls a native function that fills a buffer and returns the full length of its text,
    // growing the buffer until the text fits.
    private static string ReadText(Func<byte[], nuint, long> call)
    {
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            long length = call(buffer, (nuint)buffer.Length);
            if (length < 0)
                throw new InvalidOperationException($"Seed engine error: {LastError()}");
            if (length <= buffer.Length)
                return Encoding.UTF8.GetString(buffer, 0, (int)length);
            buffer = new byte[length];
        }
    }

    private static string LastError()
    {
        byte[] buffer = new byte[1024];
        long length = sts2_last_error(buffer, (nuint)buffer.Length);
        return Encoding.UTF8.GetString(buffer, 0, (int)Math.Clamp(length, 0, buffer.Length));
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (name != Lib)
            return IntPtr.Zero;

        string folder = Path.GetDirectoryName(assembly.Location) ?? "";
        foreach (string file in new[] { $"{Lib}.dll", $"lib{Lib}.so", $"lib{Lib}.dylib" })
        {
            if (NativeLibrary.TryLoad(Path.Combine(folder, file), out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }

    [DllImport(Lib)]
    private static extern uint sts2_abi_version();

    [DllImport(Lib)]
    private static extern long sts2_last_error(byte[] buffer, nuint cap);

    [DllImport(Lib)]
    private static extern IntPtr sts2_engine_create(byte[] json, nuint length);

    [DllImport(Lib)]
    private static extern void sts2_engine_free(IntPtr engine);

    [DllImport(Lib)]
    private static extern long sts2_engine_simulate(IntPtr engine, byte[] seed, nuint seedLength, byte[] buffer, nuint cap);

    [DllImport(Lib)]
    private static extern IntPtr sts2_search_start(IntPtr engine, byte[] filter, nuint filterLength, uint threads, uint maxResults);

    [DllImport(Lib)]
    private static extern long sts2_search_poll(IntPtr search, byte[] buffer, nuint cap);

    [DllImport(Lib)]
    private static extern void sts2_search_free(IntPtr search);
}
