using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// What one seed generates, shown as rows of game icons, with the act maps beside them.
public sealed class PreviewPanel
{
    private const int ListPreview = 6;
    private const int BagPreview = 8;

    private readonly GameData _data;
    private readonly IReadOnlyList<Cond> _conds;
    private readonly Action<Visual> _hover;
    private readonly VBoxContainer _detail = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Choice _mapAct;
    private readonly Control _mapCanvas = new();
    private readonly Dictionary<int, Texture2D?> _mapIcons = new();
    private SimResult? _shown;
    private bool _showAllLists;

    public HBoxContainer Root { get; } = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    public PreviewPanel(GameData data, IReadOnlyList<Cond> conds, Action<Visual> hover)
    {
        _data = data;
        _conds = conds;
        _hover = hover;
        Root.AddThemeConstantOverride("separation", 14);

        ScrollContainer scroll = new()
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 3f,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _detail.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_detail);
        Root.AddChild(scroll);

        VBoxContainer mapColumn = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2f };
        mapColumn.AddThemeConstantOverride("separation", 8);
        Root.AddChild(mapColumn);
        HBoxContainer mapBar = new();
        mapBar.AddThemeConstantOverride("separation", 10);
        mapColumn.AddChild(mapBar);
        mapBar.AddChild(new Label { Text = "Map of" });
        _mapAct = new Choice(Enumerable.Range(0, _data.SlotActs.Length).Select(slot => (slot, $"Act {slot + 1}")));
        _mapAct.Changed += _mapCanvas.QueueRedraw;
        mapBar.AddChild(_mapAct.Root);
        HFlowContainer legend = Visuals.Flow();
        for (int i = 0; i < Visuals.MapTypes.Length; i++)
            legend.AddChild(Visuals.Pill(Visuals.MapType(i), _hover));
        mapColumn.AddChild(legend);

        PanelContainer mapPanel = Visuals.Panel(Visuals.InsetColor);
        mapPanel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _mapCanvas.Draw += DrawMap;
        mapPanel.AddChild(_mapCanvas);
        mapColumn.AddChild(mapPanel);
    }

    public void Show(SimResult? result, string emptyMessage)
    {
        _shown = result;
        Visuals.Clear(_detail);
        if (result == null)
            _detail.AddChild(Visuals.MutedLabel(emptyMessage));
        else
            Fill(result);
        _mapCanvas.QueueRedraw();
    }

    public string BuildSummaryText(SimResult r)
    {
        StringBuilder sb = new();
        sb.AppendLine($"STS2 Seed: {r.Seed} ({_data.Character.Title.GetFormattedText()}, Ascension {_data.Ascension})");
        if (r.Neow.Length > 0)
            sb.AppendLine($"Neow: {string.Join(", ", r.Neow.Select(_data.RelicName))}");
        sb.AppendLine($"Floor 1 Reward: {string.Join(", ", r.Cards.Select(_data.CardName))} | Potion: {_data.PotionName(r.Potion)} | {r.Gold}g");
        if (r.FirstShop.Relics.Length > 0 || r.FirstShop.Cards.Length > 0)
        {
            sb.AppendLine($"First Shop Relics: {string.Join(", ", r.FirstShop.Relics.Select(_data.RelicName))}");
            sb.AppendLine($"First Shop Cards: {string.Join(", ", r.FirstShop.Cards.Select((c, idx) => idx == r.FirstShop.SaleIndex ? $"{_data.CardName(c)} (SALE)" : _data.CardName(c)))} | Colorless: {string.Join(", ", r.FirstShop.Colorless.Select(_data.CardName))}");
        }

        if (r.TreasureRelics.Length > 0)
            sb.AppendLine($"Treasure Chests: {string.Join(" -> ", r.TreasureRelics.Select(_data.RelicName))}");
        for (int i = 0; i < r.Acts.Length; i++)
        {
            string ancOffers = i > 0 && i < r.AncientRelics.Length && r.AncientRelics[i].Length > 0
                ? $" ({string.Join(", ", r.AncientRelics[i].Select(_data.RelicName))})"
                : "";
            sb.AppendLine($"Act {i + 1} ({_data.ActName(r.Acts[i])}): Boss {_data.EncounterName(r.Bosses[i])}, Ancient {_data.AncientName(r.Ancients[i])}{ancOffers}");
        }

        return sb.ToString().Trim();
    }

    private Visual VRelic(int id, bool cursed = false) =>
        Visuals.Relic(_data, id, cursed, Cond.TargetsRelic(_conds, id));

    private Visual VCard(int id) =>
        Visuals.Card(_data, id, Cond.TargetsCard(_conds, id));

    private Visual VPotion(int id) =>
        Visuals.Potion(_data, id, Cond.TargetsPotion(_conds, id));

    private Visual VEncounter(int id, RoomType room) =>
        Visuals.Encounter(_data, id, room, Cond.TargetsEncounter(_conds, id));

    private Visual VEvent(int id) =>
        Visuals.Event(_data, id, Cond.TargetsEvent(_conds, id));

    private Visual VAncient(int id) =>
        Visuals.Ancient(_data, id, Cond.TargetsAncient(_conds, id));

    private void Fill(SimResult r)
    {
        HBoxContainer titleRow = new();
        titleRow.AddThemeConstantOverride("separation", 12);
        Label seedLabel = Visuals.SectionTitle($"Seed: {r.Seed}", 28);
        seedLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titleRow.AddChild(seedLabel);

        Button expandBtn = new()
        {
            Text = _showAllLists ? "Compact lists" : "Show full queues & bags",
            ToggleMode = true,
            ButtonPressed = _showAllLists,
            FocusMode = Control.FocusModeEnum.None
        };
        expandBtn.Toggled += on =>
        {
            _showAllLists = on;
            Show(_shown, "");
        };
        titleRow.AddChild(expandBtn);
        _detail.AddChild(titleRow);

        if (r.Neow.Length == 3)
        {
            _detail.AddChild(Heading("Neow offers (Act 1 Ancient)"));
            for (int i = 0; i < r.Neow.Length; i++)
                _detail.AddChild(NeowOffer(r, r.Neow[i], i < r.NeowOutcomes.Length ? r.NeowOutcomes[i] : new NeowOutcome(), cursed: i == 2));
        }

        _detail.AddChild(Heading("Floor 1 combat reward (baseline)"));
        _detail.AddChild(RewardRow(r.Cards, r.Potion, r.Gold));

        if (r.FirstShop.Cards.Length > 0 || r.FirstShop.Relics.Length > 0)
            _detail.AddChild(ShopSection(r.FirstShop));

        if (r.TreasureRelics.Length > 0)
        {
            _detail.AddChild(Heading("Treasure chest relics (in order)"));
            HFlowContainer chests = Visuals.Flow(6);
            for (int i = 0; i < r.TreasureRelics.Length; i++)
                chests.AddChild(Captioned($"Chest {i + 1}", Visuals.Tile(VRelic(r.TreasureRelics[i]), _hover, 96, 86)));
            _detail.AddChild(chests);
        }

        for (int i = 0; i < r.Acts.Length; i++)
            _detail.AddChild(Act(r, i));

        _detail.AddChild(Heading("Your relic bag (front -> back)"));
        AddBag(r.PlayerRelics);
        _detail.AddChild(Heading("Shared relic bag (front -> back)"));
        AddBag(r.SharedRelics);
    }

    private Control RewardRow(int[] cards, int potion, int gold)
    {
        HFlowContainer reward = Visuals.Flow(6);
        foreach (int card in cards)
            reward.AddChild(Visuals.Tile(VCard(card), _hover, 96, 86));
        reward.AddChild(Visuals.Tile(VPotion(potion), _hover, 96, 86));
        reward.AddChild(Centered(new Label { Text = $"{gold} gold" }));
        return reward;
    }

    private Control ShopSection(ShopResult shop)
    {
        PanelContainer panel = Visuals.Panel(Visuals.InsetColor, 10);
        VBoxContainer col = new();
        col.AddThemeConstantOverride("separation", 6);
        panel.AddChild(col);
        col.AddChild(Heading("First shop inventory"));

        HFlowContainer cardsRow = Visuals.Flow(6);
        for (int i = 0; i < shop.Cards.Length; i++)
        {
            string tag = i == shop.SaleIndex ? "50% SALE" : $"Card {i + 1}";
            cardsRow.AddChild(Captioned(tag, Visuals.Tile(VCard(shop.Cards[i]), _hover, 94, 84)));
        }

        for (int i = 0; i < shop.Colorless.Length; i++)
            cardsRow.AddChild(Captioned("Colorless", Visuals.Tile(VCard(shop.Colorless[i]), _hover, 94, 84)));
        col.AddChild(cardsRow);

        HFlowContainer itemsRow = Visuals.Flow(6);
        for (int i = 0; i < shop.Relics.Length; i++)
        {
            string tag = i == 2 ? "Shop Relic" : $"Relic {i + 1}";
            itemsRow.AddChild(Captioned(tag, Visuals.Tile(VRelic(shop.Relics[i]), _hover, 94, 84)));
        }

        for (int i = 0; i < shop.Potions.Length; i++)
            itemsRow.AddChild(Captioned($"Potion {i + 1}", Visuals.Tile(VPotion(shop.Potions[i]), _hover, 94, 84)));
        col.AddChild(itemsRow);
        return panel;
    }

    private Control NeowOffer(SimResult sim, int relic, NeowOutcome outcome, bool cursed)
    {
        Visual visual = VRelic(relic, cursed);
        PanelContainer panel = Visuals.Panel(Visuals.InsetColor, 10);
        // The relic and its text on the left, what it gives on the right.
        HBoxContainer top = new();
        top.AddThemeConstantOverride("separation", 10);
        panel.AddChild(top);
        Button icon = Visuals.Icon(visual, _hover, 64);
        icon.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        top.AddChild(icon);
        VBoxContainer text = new() { CustomMinimumSize = new Vector2(240, 0) };
        top.AddChild(text);
        VBoxContainer column = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 4);
        top.AddChild(column);
        Label name = new() { Text = visual.Highlighted ? $"★ {visual.Name}" : visual.Name, ClipText = true };
        if (cursed)
            name.AddThemeColorOverride("font_color", Visuals.Cursed);
        else if (visual.Highlighted)
            name.AddThemeColorOverride("font_color", Visuals.HighlightBorder);
        text.AddChild(name);
        text.AddChild(Visuals.MutedLabel(_data.RelicTooltip(relic).Split('\n').Skip(1).FirstOrDefault() ?? "", 14));

        if (!outcome.IsEmpty && relic >= 0)
        {
            // Scroll Boxes and Kaleidoscope hand out their cards in groups of three to pick from.
            string title = _data.Relics[relic] switch
            {
                ScrollBoxes => "Gives one of two bundles:",
                Kaleidoscope => "Gives a pick from each row:",
                HeftyTablet or LeadPaperweight or LostCoffer => "Gives a pick of:",
                LeafyPoultice => "Transforms Strike & Defend into:",
                NewLeaf => "Transforms starter card into:",
                _ => "Gives:"
            };
            column.AddChild(Visuals.MutedLabel(title, 14));
            int group = _data.Relics[relic] is ScrollBoxes or Kaleidoscope ? 3 : int.MaxValue;
            foreach (int[] cards in outcome.Cards.Chunk(Math.Min(group, Math.Max(1, outcome.Cards.Length))))
            {
                HFlowContainer row = Visuals.Flow(4);
                foreach (int card in cards)
                    row.AddChild(Visuals.Tile(VCard(card), _hover, 92, 84));
                column.AddChild(row);
            }

            if (outcome.Relics.Length + outcome.Potions.Length > 0)
                column.AddChild(GivenRow([], outcome.Relics, outcome.Potions));

            // The relics of Neow's Bones roll things of their own.
            for (int i = 0; i < Math.Min(outcome.Nested.Length, outcome.Relics.Length); i++)
            {
                NestedOutcome nested = outcome.Nested[i];
                if (nested.First.IsEmpty && nested.Second.IsEmpty)
                    continue;
                string given = _data.RelicName(outcome.Relics[i]);
                column.AddChild(Visuals.MutedLabel($"{given} gives:", 14));
                column.AddChild(GivenRow(nested.First.Cards, nested.First.Relics, nested.First.Potions));
                if (!nested.Second.SameAs(nested.First))
                {
                    column.AddChild(Visuals.MutedLabel($"{given} gives (when taken second):", 14));
                    column.AddChild(GivenRow(nested.Second.Cards, nested.Second.Relics, nested.Second.Potions));
                }
            }
        }

        if (outcome.FirstCards.Length > 0 && !outcome.SameFirstReward(sim.Gold, sim.Potion, sim.Cards))
        {
            column.AddChild(Visuals.MutedLabel("Floor 1 reward after taking this:", 14));
            column.AddChild(RewardRow(outcome.FirstCards, outcome.FirstPotion, outcome.FirstGold));
        }

        return panel;
    }

    private Control GivenRow(int[] cards, int[] relics, int[] potions)
    {
        HFlowContainer row = Visuals.Flow(4);
        foreach (int card in cards)
            row.AddChild(Visuals.Tile(VCard(card), _hover, 92, 84));
        foreach (int relic in relics)
            row.AddChild(Visuals.Tile(VRelic(relic), _hover, 92, 84));
        foreach (int potion in potions)
            row.AddChild(Visuals.Tile(VPotion(potion), _hover, 92, 84));
        return row;
    }

    private Control Act(SimResult r, int i)
    {
        PanelContainer panel = Visuals.Panel(Visuals.InsetColor, 10);
        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);
        string actMark = Cond.TargetsAct(_conds, r.Acts[i]) ? "★ " : "";
        column.AddChild(Heading($"Act {i + 1}  ·  {actMark}{_data.ActName(r.Acts[i])}"));

        HFlowContainer top = Visuals.Flow(10);
        top.AddChild(Captioned("Boss", Visuals.Tile(VEncounter(r.Bosses[i], RoomType.Boss), _hover, 124, 92)));
        if (i == r.Acts.Length - 1 && r.SecondBoss >= 0)
            top.AddChild(Captioned("2nd Boss", Visuals.Tile(VEncounter(r.SecondBoss, RoomType.Boss), _hover, 124, 92)));
        top.AddChild(Captioned("Ancient", Visuals.Tile(VAncient(r.Ancients[i]), _hover, 124, 92)));

        if (i > 0 && i < r.AncientRelics.Length && r.AncientRelics[i].Length > 0)
        {
            HFlowContainer ancOffers = Visuals.Flow(4);
            foreach (int rel in r.AncientRelics[i])
                ancOffers.AddChild(Visuals.Tile(VRelic(rel), _hover, 94, 92));
            top.AddChild(Captioned($"{_data.AncientName(r.Ancients[i])} offers", ancOffers));
        }

        if (i < r.Maps.Length && r.Maps[i] is { } map)
        {
            HFlowContainer counts = Visuals.Flow(4);
            counts.CustomMinimumSize = new Vector2(260, 0);
            for (int t = 0; t < Visuals.MapTypes.Length; t++)
            {
                int pt = Visuals.MapTypes[t].Type;
                int total = map.Count(pt);
                int maxPath = map.MaxOnPath(pt);
                string text = maxPath > 0 ? $"{total} (≤{maxPath}/path)" : $"{total}";
                counts.AddChild(Visuals.Pill(Visuals.MapType(t), _hover, text));
            }

            top.AddChild(Captioned("Map (total / max on 1 path)", counts));
        }

        column.AddChild(top);
        int evTake = _showAllLists ? r.Events[i].Length : ListPreview;
        int normTake = _showAllLists ? r.Normals[i].Length : ListPreview;
        int eliteTake = _showAllLists ? r.Elites[i].Length : 4;
        column.AddChild(Strip("Events", r.Events[i].Take(evTake).Select(VEvent), r.Events[i].Length > evTake));
        column.AddChild(Strip("Fights", r.Normals[i].Take(normTake).Select(e => VEncounter(e, RoomType.Monster)), r.Normals[i].Length > normTake));
        column.AddChild(Strip("Elites", r.Elites[i].Take(eliteTake).Select(e => VEncounter(e, RoomType.Elite)), r.Elites[i].Length > eliteTake));
        return panel;
    }

    private void AddBag(List<RelicDeque> deques)
    {
        foreach (RelicDeque deque in deques)
        {
            RelicRarity rarity = (RelicRarity)deque.Rarity;
            if (rarity is not (RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop))
                continue;

            int take = _showAllLists ? deque.Relics.Length : BagPreview;
            HBoxContainer row = new();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(Caption(rarity.ToString()));
            HFlowContainer icons = Visuals.Flow(4);
            foreach (int relic in deque.Relics.Take(take))
                icons.AddChild(Visuals.Icon(VRelic(relic), _hover));
            if (deque.Relics.Length > take)
                icons.AddChild(Centered(Visuals.MutedLabel($"+{deque.Relics.Length - take} more")));
            row.AddChild(icons);
            _detail.AddChild(row);
        }
    }

    private Control Strip(string caption, IEnumerable<Visual> visuals, bool more)
    {
        HBoxContainer row = new();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Caption(caption));
        HFlowContainer flow = Visuals.Flow(4);
        foreach (Visual visual in visuals)
            flow.AddChild(Visuals.Pill(visual, _hover));
        if (more)
            flow.AddChild(Centered(Visuals.MutedLabel("…")));
        row.AddChild(flow);
        return row;
    }

    private static Label Heading(string text)
    {
        Label label = new() { Text = text };
        label.AddThemeFontSizeOverride("font_size", 22);
        return label;
    }

    private static Label Caption(string text)
    {
        Label label = Visuals.MutedLabel(text);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.CustomMinimumSize = new Vector2(90, 0);
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return label;
    }

    private static Control Captioned(string caption, Control content)
    {
        VBoxContainer box = new();
        box.AddThemeConstantOverride("separation", 2);
        Label label = Visuals.MutedLabel(caption, 14);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        box.AddChild(label);
        box.AddChild(content);
        return box;
    }

    private static Control Centered(Control control)
    {
        control.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return control;
    }

    private void DrawMap()
    {
        int slot = _mapAct.Value;
        if (_shown == null || slot >= _shown.Maps.Length || _shown.Maps[slot] is not { } map || map.Points.Length == 0)
            return;

        // Upright like the in-game map: the act starts at the bottom and the boss is at the top.
        int maxRow = map.Points.Max(p => p[1]);
        Vector2 size = _mapCanvas.Size;
        const float pad = 24;
        float dx = (size.X - 2 * pad) / 6;
        float dy = (size.Y - 2 * pad) / Math.Max(1, maxRow - 1);
        Vector2 At(int col, int row) => new(pad + col * dx, size.Y - pad - (row - 1) * dy);

        foreach (int[] e in map.Edges)
            _mapCanvas.DrawLine(At(e[0], e[1]), At(e[2], e[3]), new Color(1, 1, 1, 0.28f), 2, true);
        float radius = Math.Clamp(Math.Min(dx, dy) * 0.42f, 6, 17);
        foreach (int[] p in map.Points)
        {
            Vector2 at = At(p[0], p[1]);
            Color color = Visuals.MapTypes.FirstOrDefault(t => t.Type == p[2], (Type: 0, Room: RoomType.Unassigned, Name: "", Color: Visuals.Muted)).Color;
            if (!_mapIcons.TryGetValue(p[2], out Texture2D? icon))
                _mapIcons[p[2]] = icon = Visuals.MapIcon(p[2]);

            // A room icon on a dark disc ringed in the colour of its type; a plain dot without art.
            if (icon == null || radius < 9)
            {
                _mapCanvas.DrawCircle(at, radius * 0.7f, color);
                continue;
            }

            _mapCanvas.DrawCircle(at, radius, Visuals.Background);
            _mapCanvas.DrawArc(at, radius, 0, Mathf.Tau, 24, color, 2, true);
            float half = radius * 0.8f;
            _mapCanvas.DrawTextureRect(icon, new Rect2(at - new Vector2(half, half), new Vector2(half * 2, half * 2)), false);
        }
    }
}
