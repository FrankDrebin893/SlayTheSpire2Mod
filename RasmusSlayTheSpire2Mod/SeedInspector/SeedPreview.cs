namespace RasmusSlayTheSpire2Mod.SeedInspector;

// What a seed gives at the start of a run, as display names in the game's current language.
public sealed class SeedPreview
{
    public required string Seed { get; init; }
    public required IReadOnlyList<string> Acts { get; init; }
    public required IReadOnlyList<string> Bosses { get; init; }
    public string? SecondBoss { get; init; }
    // One entry per act; the first is the act 1 ancient (Neow once unlocked).
    public required IReadOnlyList<string> Ancients { get; init; }
    public required IReadOnlyList<string> NeowOffers { get; init; }
    public required IReadOnlyList<string> Act1Events { get; init; }
    public required string Act1Map { get; init; }
    // Null when the card reward was not simulated.
    public IReadOnlyList<string>? FirstCardReward { get; init; }
}
