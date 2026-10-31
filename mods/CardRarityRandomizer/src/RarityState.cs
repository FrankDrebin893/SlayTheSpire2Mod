namespace CardRarityRandomizer;

public sealed record RarityChange(string CardId, string Name, string Pool, string From, string To);

// The rarity table for the current run: card id (ModelId.Entry) -> new rarity name.
public static class RarityState
{
    private static readonly Dictionary<string, string> Overrides = new();
    private static bool _active;

    public static event Action? StateChanged;

    public static bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            StateChanged?.Invoke();
        }
    }

    public static string? Seed { get; private set; }
    public static IReadOnlyList<RarityChange> Changes { get; private set; } = Array.Empty<RarityChange>();

    public static bool TryGet(string cardId, out string rarity) => Overrides.TryGetValue(cardId, out rarity!);

    public static void Set(string? seed, IReadOnlyList<RarityChange> changes)
    {
        Overrides.Clear();
        foreach (var c in changes) Overrides[c.CardId] = c.To;
        Seed = seed;
        Changes = changes;
        StateChanged?.Invoke();
    }

    public static void Clear()
    {
        Overrides.Clear();
        Changes = Array.Empty<RarityChange>();
        Seed = null;
        Active = false;
        StateChanged?.Invoke();
    }
}
