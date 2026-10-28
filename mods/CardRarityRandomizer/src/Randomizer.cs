using System.Security.Cryptography;
using System.Text;

namespace CardRarityRandomizer;

// Builds the shuffled rarity table. Only Common, Uncommon and Rare cards take part, and they
// shuffle inside their own card pool, so every character keeps the same number of cards of each
// rarity. Basic, Ancient, Curse, Status, Quest and Event cards never move.
public static class Randomizer
{
    private static readonly string[] Shuffled = { "Common", "Uncommon", "Rare" };

    public sealed record Entry(string CardId, string Name, string Pool, string Rarity);

    public static List<RarityChange> Build(IEnumerable<Entry> cards, string seed)
    {
        var changes = new List<RarityChange>();
        foreach (var pool in cards.Where(c => Shuffled.Contains(c.Rarity)).GroupBy(c => c.Pool).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var cardsInPool = pool.OrderBy(c => c.CardId, StringComparer.Ordinal).ToList();
            var rarities = cardsInPool.Select(c => c.Rarity).ToList();
            var rng = new Random(StableHash(seed + "|" + pool.Key));
            for (var i = rarities.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (rarities[i], rarities[j]) = (rarities[j], rarities[i]);
            }
            for (var i = 0; i < cardsInPool.Count; i++)
            {
                var c = cardsInPool[i];
                if (rarities[i] != c.Rarity)
                    changes.Add(new RarityChange(c.CardId, c.Name, c.Pool, c.Rarity, rarities[i]));
            }
        }
        return changes;
    }

    // string.GetHashCode is randomized per process; the same seed must give the same table on reload.
    private static int StableHash(string text) =>
        BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);
}
