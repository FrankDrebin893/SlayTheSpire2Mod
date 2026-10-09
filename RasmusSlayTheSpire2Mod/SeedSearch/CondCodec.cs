using System.Text;
using System.Text.Json;
using MegaCrit.Sts2.Core.Map;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Filters in the form they are saved in. The ids in a Cond only mean something for one GameData,
// so each is stored as the entry of its model and looked up again in whatever data is current.
// Every filter also carries its description, so a saved search can be listed without that lookup.
public static class CondCodec
{
    public static string ToJson(IEnumerable<Cond> conds, GameData d)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter w = new(stream))
        {
            w.WriteStartArray();
            foreach (Cond cond in conds)
            {
                w.WriteStartObject();
                w.WriteString("text", cond.Describe(d));
                Write(w, cond, d);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string[] Texts(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("text").GetString() ?? "").ToArray();
    }

    // The filters that exist in this data, and how many had to be left out because they refer to
    // something it does not have: another character's card, locked content, an act slot too many.
    public static (List<Cond> Conds, int Dropped) Read(string json, GameData d)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        List<Cond> conds = [];
        int dropped = 0;
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            Cond? cond;
            try
            {
                cond = ReadOne(e, d);
            }
            catch (Exception)
            {
                cond = null;
            }

            if (cond == null)
                dropped++;
            else if (!conds.Contains(cond))
                conds.Add(cond);
        }

        return (conds, dropped);
    }

    private static void Write(Utf8JsonWriter w, Cond cond, GameData d)
    {
        switch (cond)
        {
            case NeowOfferCond c:
                w.WriteString("kind", "neow_offer");
                w.WriteString("relic", d.Relics[c.Relic].Id.Entry);
                break;
            case NeowGivesCond c:
                w.WriteString("kind", "neow_gives");
                w.WriteString("relic", d.Relics[c.Relic].Id.Entry);
                w.WriteString("what", c.What.ToString());
                w.WriteString("item", c.What switch
                {
                    GiveItem.Card => d.Cards[c.Item].Id.Entry,
                    GiveItem.Relic => d.Relics[c.Item].Id.Entry,
                    _ => d.Potions[c.Item].Id.Entry
                });
                break;
            case ActCond c:
                w.WriteString("kind", "act");
                w.WriteNumber("slot", c.Slot);
                w.WriteString("act", d.Acts[c.Act].Id.Entry);
                break;
            case BossCond c:
                w.WriteString("kind", "boss");
                w.WriteNumber("slot", c.Slot);
                w.WriteString("enc", d.Encounters[c.Encounter].Id.Entry);
                break;
            case SecondBossCond c:
                w.WriteString("kind", "second_boss");
                w.WriteString("enc", d.Encounters[c.Encounter].Id.Entry);
                break;
            case AncientCond c:
                w.WriteString("kind", "ancient");
                w.WriteNumber("slot", c.Slot);
                w.WriteString("ancient", d.Ancients[c.Ancient].Id.Entry);
                break;
            case EventCond c:
                w.WriteString("kind", "event");
                w.WriteNumber("slot", c.Slot);
                w.WriteString("event", d.Events[c.Event].Id.Entry);
                w.WriteNumber("within", c.WithinFirst);
                break;
            case EncounterCond c:
                w.WriteString("kind", c.Elite ? "elite" : "normal");
                w.WriteNumber("slot", c.Slot);
                w.WriteString("enc", d.Encounters[c.Encounter].Id.Entry);
                w.WriteNumber("within", c.WithinFirst);
                break;
            case RelicCond c:
                w.WriteString("kind", "relic");
                w.WriteBoolean("shared", c.Shared);
                w.WriteString("relic", d.Relics[c.Relic].Id.Entry);
                w.WriteNumber("within", c.WithinFirst);
                break;
            case MapCountCond c:
                w.WriteString("kind", "map_count");
                w.WriteNumber("slot", c.Slot);
                w.WriteString("point_type", ((MapPointType)c.PointType).ToString());
                w.WriteNumber("min", c.Min);
                w.WriteNumber("max", c.Max);
                break;
            case RewardCardCond c:
                w.WriteString("kind", "reward_card");
                w.WriteString("card", d.Cards[c.Card].Id.Entry);
                break;
            case RewardPotionCond c:
                w.WriteString("kind", "reward_potion");
                w.WriteBoolean("present", c.Present);
                break;
            default:
                throw new NotSupportedException($"Filter {cond.GetType().Name} cannot be saved.");
        }
    }

    private static Cond? ReadOne(JsonElement e, GameData d)
    {
        string Str(string name) => e.GetProperty(name).GetString() ?? "";
        int Int(string name) => e.GetProperty(name).GetInt32();
        int? Slot() => Int("slot") is var s && s >= 0 && s < d.SlotActs.Length ? s : null;
        int? Encounter() => d.Encounters.Find(Str("enc"));
        // A relic Neow can offer here.
        int? Offer() => d.Relics.Find(Str("relic")) is { } r && d.NeowRelics.Contains(r) ? r : null;

        string kind = Str("kind");
        switch (kind)
        {
            case "neow_offer":
                return Offer() is { } offered ? new NeowOfferCond(offered) : null;
            case "neow_gives":
            {
                if (Offer() is not { } relic || !Enum.TryParse(Str("what"), true, out GiveItem what))
                    return null;
                int? item = what switch
                {
                    GiveItem.Card => d.Cards.Find(Str("item")),
                    GiveItem.Relic => d.Relics.Find(Str("item")),
                    _ => d.Potions.Find(Str("item"))
                };
                return item is { } i ? new NeowGivesCond(relic, what, i) : null;
            }
            case "act":
                return Slot() is { } actSlot && d.Acts.Find(Str("act")) is { } act && d.SlotActs[actSlot].Contains(act) ? new ActCond(actSlot, act) : null;
            case "boss":
                return Slot() is { } bossSlot && Encounter() is { } boss ? new BossCond(bossSlot, boss) : null;
            case "second_boss":
                return d.DoubleBoss && Encounter() is { } second ? new SecondBossCond(second) : null;
            case "ancient":
                return Slot() is { } ancientSlot && d.Ancients.Find(Str("ancient")) is { } ancient ? new AncientCond(ancientSlot, ancient) : null;
            case "event":
                return Slot() is { } eventSlot && d.Events.Find(Str("event")) is { } ev ? new EventCond(eventSlot, ev, Int("within")) : null;
            case "elite" or "normal":
                return Slot() is { } fightSlot && Encounter() is { } fight ? new EncounterCond(fightSlot, kind == "elite", fight, Int("within")) : null;
            case "relic":
            {
                bool shared = e.GetProperty("shared").GetBoolean();
                return d.Relics.Find(Str("relic")) is { } r && (shared ? d.SharedBag : d.PlayerBag).Contains(r) ? new RelicCond(shared, r, Int("within")) : null;
            }
            case "map_count":
            {
                if (Slot() is not { } mapSlot || !Enum.TryParse(Str("point_type"), out MapPointType point))
                    return null;
                int index = Array.FindIndex(Visuals.MapTypes, t => t.Type == (int)point);
                return index < 0 ? null : new MapCountCond(mapSlot, (int)point, Visuals.MapTypes[index].Name, Int("min"), Int("max"));
            }
            case "reward_card":
                return d.Cards.Find(Str("card")) is { } card && d.RewardCards.Contains(card) ? new RewardCardCond(card) : null;
            case "reward_potion":
                return new RewardPotionCond(e.GetProperty("present").GetBoolean());
            default:
                return null;
        }
    }
}
