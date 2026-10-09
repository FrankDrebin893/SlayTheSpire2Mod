using System.Text.Json;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// A seed the player wants to find again, with the run setup it was looked at under.
public sealed record Bookmark(string Seed, string Character, string CharacterName, int Ascension, string Note, DateTime Saved)
{
    public string Setup => $"{CharacterName} A{Ascension}";

    public bool IsFor(GameData d) => Character == d.Character.Id.Entry && Ascension == d.Ascension;
}

// A set of filters that was searched for or saved. CondsJson is the CondCodec form.
public sealed record SavedSearch(string Character, string CharacterName, int Ascension, bool Kept, DateTime Used, string CondsJson)
{
    public string Setup => $"{CharacterName} A{Ascension}";
}

// The bookmarks and searches the seed search screen remembers between sessions, in one file in
// the game's user data folder. It must not live in the mod folder, where the mod loader reads
// every .json as a manifest.
public static class SavedData
{
    public const int MaxRecent = 12;

    private static readonly string FilePath = Path.Combine(Godot.OS.GetUserDataDir(), "RasmusSlayTheSpire2Mod", "seed_search.json");
    private static readonly List<Bookmark> BookmarkList = [];
    private static readonly List<SavedSearch> SearchList = [];
    private static bool _loaded;

    public static event Action? BookmarksChanged;
    public static event Action? SearchesChanged;

    // Newest first.
    public static IReadOnlyList<Bookmark> Bookmarks
    {
        get
        {
            Load();
            return BookmarkList;
        }
    }

    // Most recently used first.
    public static IReadOnlyList<SavedSearch> Searches
    {
        get
        {
            Load();
            return SearchList;
        }
    }

    public static Bookmark? FindBookmark(string seed, GameData d) => Bookmarks.FirstOrDefault(b => b.Seed == seed && b.IsFor(d));

    public static void ToggleBookmark(string seed, GameData d)
    {
        if (FindBookmark(seed, d) is { } existing)
            BookmarkList.Remove(existing);
        else
            BookmarkList.Insert(0, new Bookmark(seed, d.Character.Id.Entry, d.Character.Title.GetFormattedText(), d.Ascension, "", DateTime.UtcNow));
        Save();
        BookmarksChanged?.Invoke();
    }

    public static void SetNote(Bookmark bookmark, string note)
    {
        int index = BookmarkList.IndexOf(bookmark);
        if (index < 0 || bookmark.Note == note)
            return;

        BookmarkList[index] = bookmark with { Note = note };
        Save();
        BookmarksChanged?.Invoke();
    }

    // Puts these filters at the top of the list. Searches that are not kept fall off the end.
    public static void RecordSearch(GameData d, IReadOnlyList<Cond> conds, bool keep = false)
    {
        Load();
        string json = CondCodec.ToJson(conds, d);
        string character = d.Character.Id.Entry;
        int index = SearchList.FindIndex(s => s.Character == character && s.Ascension == d.Ascension && s.CondsJson == json);
        bool kept = keep || (index >= 0 && SearchList[index].Kept);
        if (index >= 0)
            SearchList.RemoveAt(index);
        SearchList.Insert(0, new SavedSearch(character, d.Character.Title.GetFormattedText(), d.Ascension, kept, DateTime.UtcNow, json));
        Trim();
        Save();
        SearchesChanged?.Invoke();
    }

    public static void SetKept(SavedSearch search, bool kept)
    {
        int index = SearchList.IndexOf(search);
        if (index < 0 || search.Kept == kept)
            return;

        SearchList[index] = search with { Kept = kept };
        Trim();
        Save();
        SearchesChanged?.Invoke();
    }

    public static void RemoveSearch(SavedSearch search)
    {
        if (!SearchList.Remove(search))
            return;

        Save();
        SearchesChanged?.Invoke();
    }

    private static void Trim()
    {
        int recent = 0;
        SearchList.RemoveAll(s => !s.Kept && ++recent > MaxRecent);
    }

    private static void Load()
    {
        if (_loaded)
            return;

        _loaded = true;
        if (!File.Exists(FilePath))
            return;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            JsonElement root = doc.RootElement;
            foreach (JsonElement b in root.GetProperty("bookmarks").EnumerateArray())
            {
                BookmarkList.Add(new Bookmark(Str(b, "seed"), Str(b, "character"), Str(b, "character_name"), b.GetProperty("ascension").GetInt32(),
                    Str(b, "note"), b.GetProperty("saved").GetDateTime()));
            }

            foreach (JsonElement s in root.GetProperty("searches").EnumerateArray())
            {
                string conds = s.GetProperty("conds").GetRawText();
                // Fails here rather than when the search is listed.
                CondCodec.Texts(conds);
                SearchList.Add(new SavedSearch(Str(s, "character"), Str(s, "character_name"), s.GetProperty("ascension").GetInt32(),
                    s.GetProperty("kept").GetBoolean(), s.GetProperty("used").GetDateTime(), conds));
            }
        }
        catch (Exception e)
        {
            BookmarkList.Clear();
            SearchList.Clear();
            string backup = FilePath + ".bak";
            MainFile.Logger.Error($"Saved seeds and searches could not be read; starting empty and keeping the old file as {backup}. {e}");
            try
            {
                File.Move(FilePath, backup, true);
            }
            catch (Exception moveError)
            {
                MainFile.Logger.Error($"The unreadable file could not be set aside. {moveError}");
            }
        }
    }

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Written beside the real file and moved over it, so a crash cannot leave half a file.
            string temp = FilePath + ".tmp";
            using (FileStream stream = File.Create(temp))
            using (Utf8JsonWriter w = new(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);

                w.WriteStartArray("bookmarks");
                foreach (Bookmark b in BookmarkList)
                {
                    w.WriteStartObject();
                    w.WriteString("seed", b.Seed);
                    w.WriteString("character", b.Character);
                    w.WriteString("character_name", b.CharacterName);
                    w.WriteNumber("ascension", b.Ascension);
                    w.WriteString("note", b.Note);
                    w.WriteString("saved", b.Saved);
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteStartArray("searches");
                foreach (SavedSearch s in SearchList)
                {
                    w.WriteStartObject();
                    w.WriteString("character", s.Character);
                    w.WriteString("character_name", s.CharacterName);
                    w.WriteNumber("ascension", s.Ascension);
                    w.WriteBoolean("kept", s.Kept);
                    w.WriteString("used", s.Used);
                    w.WritePropertyName("conds");
                    w.WriteRawValue(s.CondsJson);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            File.Move(temp, FilePath, true);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Saved seeds and searches could not be written to {FilePath}. {e}");
        }
    }
}
