using Godot;

namespace CardRarityRandomizer;

// A full-screen overlay on its own CanvasLayer, built from stock Godot nodes only.
public static class SummaryScreen
{
    private static readonly Color Common = new(0.72f, 0.74f, 0.78f);
    private static readonly Color Uncommon = new(0.35f, 0.75f, 1f);
    private static readonly Color Rare = new(1f, 0.78f, 0.25f);

    private static Color ColorOf(string rarity) => rarity switch
    {
        "Rare" => Rare,
        "Uncommon" => Uncommon,
        _ => Common,
    };

    public static void Show(IReadOnlyList<RarityChange> changes)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) return;
        root.CallDeferred(Node.MethodName.AddChild, Build(changes));
    }

    private static CanvasLayer Build(IReadOnlyList<RarityChange> changes)
    {
        var layer = new CanvasLayer { Layer = 100 };

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.75f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.CustomMinimumSize = new Vector2(900, 640);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.Position = new Vector2(-450, -320);
        layer.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 10);
        panel.AddChild(box);

        var title = new Label { Text = "Card Rarity Randomizer", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 34);
        box.AddChild(title);

        var counts = changes.GroupBy(c => $"{c.From} -> {c.To}").OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
        box.AddChild(new Label
        {
            Text = changes.Count == 0 ? "No rarities changed." : $"{changes.Count} cards changed rarity this run.   " + string.Join("   ", counts),
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 480) };
        box.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);

        foreach (var pool in changes.GroupBy(c => c.Pool).OrderBy(g => g.Key))
        {
            var header = new Label { Text = pool.Key.Replace("CardPool", "") };
            header.AddThemeFontSizeOverride("font_size", 24);
            list.AddChild(header);
            // Biggest jumps first: Common -> Rare and Rare -> Common lead the list.
            foreach (var c in pool.OrderByDescending(c => Math.Abs(Rank(c.To) - Rank(c.From))).ThenBy(c => c.Name))
            {
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = c.Name, CustomMinimumSize = new Vector2(380, 0) });
                row.AddChild(Colored(c.From));
                row.AddChild(new Label { Text = "  ->  " });
                row.AddChild(Colored(c.To));
                list.AddChild(row);
            }
        }

        var close = new Button { Text = "Start the run", CustomMinimumSize = new Vector2(0, 48) };
        close.Pressed += layer.QueueFree;
        box.AddChild(close);
        return layer;
    }

    private static int Rank(string rarity) => rarity switch { "Rare" => 2, "Uncommon" => 1, _ => 0 };

    private static Label Colored(string rarity)
    {
        var label = new Label { Text = rarity, CustomMinimumSize = new Vector2(110, 0) };
        label.AddThemeColorOverride("font_color", ColorOf(rarity));
        return label;
    }
}
