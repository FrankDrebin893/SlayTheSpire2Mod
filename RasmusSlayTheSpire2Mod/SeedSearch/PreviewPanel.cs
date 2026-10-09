using Godot;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// What one seed generates, shown as rows of game icons, with the act maps underneath.
public sealed class PreviewPanel
{
    private const int ListPreview = 6;
    private const int BagPreview = 8;

    private readonly GameData _data;
    private readonly Action<Visual> _hover;
    private readonly VBoxContainer _detail = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly OptionButton _mapAct = new();
    private readonly Control _mapCanvas = new();
    private readonly Dictionary<int, Texture2D?> _mapIcons = new();
    private SimResult? _shown;

    public VBoxContainer Root { get; } = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

    public PreviewPanel(GameData data, Action<Visual> hover)
    {
        _data = data;
        _hover = hover;
        Root.AddThemeConstantOverride("separation", 8);

        ScrollContainer scroll = new()
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 2f,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _detail.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_detail);
        Root.AddChild(scroll);

        HBoxContainer mapBar = new();
        mapBar.AddThemeConstantOverride("separation", 10);
        Root.AddChild(mapBar);
        mapBar.AddChild(new Label { Text = "Map of" });
        for (int slot = 0; slot < _data.SlotActs.Length; slot++)
            _mapAct.AddItem($"Act {slot + 1}", slot);
        _mapAct.ItemSelected += _ => _mapCanvas.QueueRedraw();
        mapBar.AddChild(_mapAct);
        HFlowContainer legend = Visuals.Flow();
        legend.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        for (int i = 0; i < Visuals.MapTypes.Length; i++)
            legend.AddChild(Visuals.Pill(Visuals.MapType(i), _hover));
        mapBar.AddChild(legend);

        PanelContainer mapPanel = Visuals.Panel(Visuals.InsetColor);
        mapPanel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _mapCanvas.Draw += DrawMap;
        mapPanel.AddChild(_mapCanvas);
        Root.AddChild(mapPanel);
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

    private void Fill(SimResult r)
    {
        _detail.AddChild(Visuals.SectionTitle(r.Seed, 30));

        if (r.Neow.Length == 3)
        {
            _detail.AddChild(Heading("Neow offers"));
            for (int i = 0; i < r.Neow.Length; i++)
                _detail.AddChild(NeowOffer(r.Neow[i], i < r.NeowOutcomes.Length ? r.NeowOutcomes[i] : new NeowOutcome(), cursed: i == 2));
        }

        _detail.AddChild(Heading("First fight reward"));
        HFlowContainer reward = Visuals.Flow();
        foreach (int card in r.Cards)
            reward.AddChild(Visuals.Tile(Visuals.Card(_data, card), _hover));
        reward.AddChild(Visuals.Tile(Visuals.Potion(_data, r.Potion), _hover));
        reward.AddChild(Centered(new Label { Text = $"{r.Gold} gold" }));
        _detail.AddChild(reward);

        for (int i = 0; i < r.Acts.Length; i++)
            _detail.AddChild(Act(r, i));

        _detail.AddChild(Heading("Your relic bag"));
        AddBag(r.PlayerRelics);
        _detail.AddChild(Heading("Shared relic bag"));
        AddBag(r.SharedRelics);

        _detail.AddChild(Visuals.MutedLabel(
            "Lists show what comes first. Each Neow offer shows what it gives if you take it; relics found inside a capsule or Neow's Bones can roll things of their own, which shifts what comes after them. " +
            "The fight reward assumes a normal first fight and a Neow pick that gives no cards, relics or potions. Custom modifiers are not taken into account."));
    }

    private Control NeowOffer(int relic, NeowOutcome outcome, bool cursed)
    {
        Visual visual = Visuals.Relic(_data, relic, cursed);
        PanelContainer panel = Visuals.Panel(Visuals.InsetColor, 10);
        // The relic and its text on the left, what it gives on the right.
        HBoxContainer top = new();
        top.AddThemeConstantOverride("separation", 10);
        panel.AddChild(top);
        Button icon = Visuals.Icon(visual, _hover, 64);
        icon.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        top.AddChild(icon);
        VBoxContainer text = new() { CustomMinimumSize = new Vector2(250, 0) };
        top.AddChild(text);
        VBoxContainer column = new() { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", 4);
        top.AddChild(column);
        Label name = new() { Text = visual.Name, ClipText = true };
        if (cursed)
            name.AddThemeColorOverride("font_color", Visuals.Cursed);
        text.AddChild(name);
        text.AddChild(Visuals.MutedLabel(visual.Describe?.Invoke() ?? "", 14));

        if (outcome.IsEmpty || relic < 0)
            return panel;

        // Scroll Boxes and Kaleidoscope hand out their cards in groups of three to pick from.
        string title = _data.Relics[relic] switch
        {
            ScrollBoxes => "Gives one of two bundles",
            Kaleidoscope => "Gives a pick from each row",
            HeftyTablet or LeadPaperweight or LostCoffer => "Gives a pick of",
            LeafyPoultice => "Turns a Strike and a Defend into",
            NewLeaf => "Turns a starter card into",
            _ => "Gives"
        };
        column.AddChild(Visuals.MutedLabel(title, 14));
        int group = _data.Relics[relic] is ScrollBoxes or Kaleidoscope ? 3 : int.MaxValue;
        foreach (int[] cards in outcome.Cards.Chunk(Math.Min(group, Math.Max(1, outcome.Cards.Length))))
        {
            HFlowContainer row = Visuals.Flow(4);
            foreach (int card in cards)
                row.AddChild(Visuals.Tile(Visuals.Card(_data, card), _hover, 92, 84));
            column.AddChild(row);
        }

        if (outcome.Relics.Length + outcome.Potions.Length > 0)
        {
            HFlowContainer row = Visuals.Flow(4);
            foreach (int given in outcome.Relics)
                row.AddChild(Visuals.Tile(Visuals.Relic(_data, given), _hover, 92, 84));
            foreach (int potion in outcome.Potions)
                row.AddChild(Visuals.Tile(Visuals.Potion(_data, potion), _hover, 92, 84));
            column.AddChild(row);
        }

        return panel;
    }

    private Control Act(SimResult r, int i)
    {
        PanelContainer panel = Visuals.Panel(Visuals.InsetColor, 10);
        VBoxContainer column = new();
        column.AddThemeConstantOverride("separation", 6);
        panel.AddChild(column);
        column.AddChild(Heading($"Act {i + 1}  ·  {_data.ActName(r.Acts[i])}"));

        HFlowContainer top = Visuals.Flow(10);
        top.AddChild(Captioned("Boss", Visuals.Tile(Visuals.Encounter(_data, r.Bosses[i], RoomType.Boss), _hover, 130, 96)));
        if (i == r.Acts.Length - 1 && r.SecondBoss >= 0)
            top.AddChild(Captioned("Then", Visuals.Tile(Visuals.Encounter(_data, r.SecondBoss, RoomType.Boss), _hover, 130, 96)));
        top.AddChild(Captioned("Ancient", Visuals.Tile(Visuals.Ancient(_data, r.Ancients[i]), _hover, 130, 96)));
        if (i < r.Maps.Length && r.Maps[i] is { } map)
        {
            HFlowContainer counts = Visuals.Flow(4);
            counts.CustomMinimumSize = new Vector2(280, 0);
            for (int t = 0; t < Visuals.MapTypes.Length; t++)
                counts.AddChild(Visuals.Pill(Visuals.MapType(t), _hover, $"{map.Count(Visuals.MapTypes[t].Type)}"));
            top.AddChild(Captioned("Map", counts));
        }

        column.AddChild(top);
        column.AddChild(Strip("Events", r.Events[i].Take(ListPreview).Select(e => Visuals.Event(_data, e)), r.Events[i].Length > ListPreview));
        column.AddChild(Strip("Fights", r.Normals[i].Take(ListPreview).Select(e => Visuals.Encounter(_data, e, RoomType.Monster)), r.Normals[i].Length > ListPreview));
        column.AddChild(Strip("Elites", r.Elites[i].Take(3).Select(e => Visuals.Encounter(_data, e, RoomType.Elite)), r.Elites[i].Length > 3));
        return panel;
    }

    private void AddBag(List<RelicDeque> deques)
    {
        foreach (RelicDeque deque in deques)
        {
            RelicRarity rarity = (RelicRarity)deque.Rarity;
            if (rarity is not (RelicRarity.Common or RelicRarity.Uncommon or RelicRarity.Rare or RelicRarity.Shop))
                continue;

            HBoxContainer row = new();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(Caption(rarity.ToString()));
            HFlowContainer icons = Visuals.Flow(4);
            foreach (int relic in deque.Relics.Take(BagPreview))
                icons.AddChild(Visuals.Icon(Visuals.Relic(_data, relic), _hover));
            if (deque.Relics.Length > BagPreview)
                icons.AddChild(Centered(Visuals.MutedLabel("…")));
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
        int slot = _mapAct.ItemCount == 0 ? 0 : _mapAct.GetSelectedId();
        if (_shown == null || slot >= _shown.Maps.Length || _shown.Maps[slot] is not { } map || map.Points.Length == 0)
            return;

        // Rows run bottom to top like the in-game map, drawn sideways here to fit a wide panel:
        // the act starts at the left edge and the boss is at the right.
        int maxRow = map.Points.Max(p => p[1]);
        Vector2 size = _mapCanvas.Size;
        const float pad = 24;
        float dx = (size.X - 2 * pad) / Math.Max(1, maxRow - 1);
        float dy = (size.Y - 2 * pad) / 6;
        Vector2 At(int col, int row) => new(pad + (row - 1) * dx, pad + col * dy);

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
