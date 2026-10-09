using System.Diagnostics;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

namespace RasmusSlayTheSpire2Mod.SeedInspector;

// Case-insensitive "name contains" filters. An empty filter matches everything.
public sealed class SeedFilter
{
    public string NeowOffer { get; init; } = "";
    public string Act1Boss { get; init; } = "";
    public string Act2Ancient { get; init; } = "";
    public string Act3Ancient { get; init; } = "";
    public string Act1Event { get; init; } = "";
    public string RewardCard { get; init; } = "";

    public bool NeedsCardReward => RewardCard.Length > 0;

    public bool IsEmpty => NeowOffer.Length + Act1Boss.Length + Act2Ancient.Length + Act3Ancient.Length + Act1Event.Length + RewardCard.Length == 0;

    public bool Matches(SeedPreview preview) =>
        Has(preview.NeowOffers, NeowOffer)
        && Has(preview.Bosses.Take(1), Act1Boss)
        && Has(preview.Ancients.Skip(1).Take(1), Act2Ancient)
        && Has(preview.Ancients.Skip(2).Take(1), Act3Ancient)
        && Has(preview.Act1Events.Take(1), Act1Event)
        && Has(preview.FirstCardReward ?? [], RewardCard);

    private static bool Has(IEnumerable<string> names, string filter) =>
        filter.Length == 0 || names.Any(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

// Tries random seeds against a filter. Runs in short slices on the main thread, because the
// game code it calls shares static state with the rest of the game.
public sealed class SeedSearch(SeedFilter filter, CharacterModel character, int ascension, string act1Key)
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    public int Tried { get; private set; }

    public bool Failed { get; private set; }

    public double SeedsPerSecond => Tried / Math.Max(_elapsed.Elapsed.TotalSeconds, 0.001);

    // Returns the first matching seed found within the time budget, or null.
    public SeedPreview? Step(double budgetMs)
    {
        Stopwatch slice = Stopwatch.StartNew();
        do
        {
            SeedPreview? preview = SeedSimulator.Simulate(SeedHelper.GetRandomSeed(), character, ascension, act1Key, filter.NeedsCardReward);
            Tried++;
            if (preview == null)
            {
                Failed = true;
                return null;
            }

            if (filter.Matches(preview))
                return preview;
        } while (slice.Elapsed.TotalMilliseconds < budgetMs);

        return null;
    }
}
