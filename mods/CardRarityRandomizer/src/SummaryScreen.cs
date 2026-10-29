using Godot;

namespace CardRarityRandomizer;

// A full-screen overlay on its own CanvasLayer, built from stock Godot nodes only.
public static class SummaryScreen
{
    private static readonly Color Common = new(0.72f, 0.74f, 0.78f);
    private static readonly Color Uncommon = new(0.35f, 0.75f, 1f);
    private static readonly Color Rare = new(1f, 0.78f, 0.25f);

    private static CanvasLayer? _currentLayer;

    private static Color ColorOf(string rarity) => rarity switch
    {
        "Rare" => Rare,
        "Uncommon" => Uncommon,
        _ => Common,
    };

    public static void Toggle()
    {
        if (_currentLayer != null && GodotObject.IsInstanceValid(_currentLayer))
        {
            _currentLayer.QueueFree();
            _currentLayer = null;
            return;
        }

        Show(RarityState.Changes);
    }

    public static void Show(IReadOnlyList<RarityChange> changes)
    {
        var root = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (root == null) return;

        if (_currentLayer != null && GodotObject.IsInstanceValid(_currentLayer))
        {
            _currentLayer.QueueFree();
            _currentLayer = null;
        }

        var layer = Build(changes);
        _currentLayer = layer;
        root.CallDeferred(Node.MethodName.AddChild, layer);
    }

    private static CanvasLayer Build(IReadOnlyList<RarityChange> changes)
    {
        var layer = new CanvasLayer { Layer = 100 };
        layer.TreeExited += () =>
        {
            if (_currentLayer == layer) _currentLayer = null;
        };

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.8f) };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.Center);
        panel.CustomMinimumSize = new Vector2(920, 680);
        panel.GrowHorizontal = Control.GrowDirection.Both;
        panel.GrowVertical = Control.GrowDirection.Both;
        panel.Position = new Vector2(-460, -340);
        layer.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 14);
        panel.AddChild(box);

        var title = new Label { Text = "Card Rarity Randomizer", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 32);
        box.AddChild(title);

        var counts = changes.GroupBy(c => $"{c.From} -> {c.To}").OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}");
        var subLabel = new Label
        {
            Text = changes.Count == 0
                ? "Active run has normal rarities. [Press F8 during run to open Card Library]"
                : $"{changes.Count} cards changed rarity this run.   {string.Join("   ", counts)}   [F8: Open Card Library]",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        box.AddChild(subLabel);

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 470)
        };
        box.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        scroll.AddChild(list);

        if (changes.Count == 0)
        {
            list.AddChild(new Label
            {
                Text = "\nNo rarities changed for this run or not in an active run.\n(Press F8 during a run to open the Card Library / Compendium.)",
                HorizontalAlignment = HorizontalAlignment.Center
            });
        }
        else
        {
            foreach (var pool in changes.GroupBy(c => c.Pool).OrderBy(g => g.Key))
            {
                var header = new Label { Text = pool.Key.Replace("CardPool", "") };
                header.AddThemeFontSizeOverride("font_size", 22);
                list.AddChild(header);

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
        }

        // Bottom action button row
        var btnRow = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btnRow.AddThemeConstantOverride("separation", 16);
        box.AddChild(btnRow);

        var compendiumBtn = new Button
        {
            Text = "📖 Open Card Library (Compendium)",
            CustomMinimumSize = new Vector2(300, 46),
            FocusMode = Control.FocusModeEnum.None
        };
        compendiumBtn.AddThemeStyleboxOverride("normal", MakeBox(new Color(0.15f, 0.32f, 0.38f), new Color(0.4f, 0.8f, 0.85f)));
        compendiumBtn.AddThemeStyleboxOverride("hover", MakeBox(new Color(0.2f, 0.42f, 0.5f), new Color(0.6f, 0.95f, 1f)));
        compendiumBtn.AddThemeColorOverride("font_color", new Color(0.85f, 0.95f, 1f));
        compendiumBtn.Pressed += () =>
        {
            layer.QueueFree();
            _currentLayer = null;
            TogglePatch.OpenCompendiumCardLibrary();
        };
        btnRow.AddChild(compendiumBtn);

        var close = new Button
        {
            Text = "Start the run",
            CustomMinimumSize = new Vector2(200, 46),
            FocusMode = Control.FocusModeEnum.None
        };
        close.Pressed += () =>
        {
            layer.QueueFree();
            _currentLayer = null;
        };
        btnRow.AddChild(close);

        return layer;
    }

    private static StyleBoxFlat MakeBox(Color fill, Color border)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border
        };
        box.SetBorderWidthAll(2);
        box.SetCornerRadiusAll(6);
        box.ContentMarginLeft = box.ContentMarginRight = 14;
        box.ContentMarginTop = box.ContentMarginBottom = 6;
        return box;
    }

    private static int Rank(string rarity) => rarity switch { "Rare" => 2, "Uncommon" => 1, _ => 0 };

    private static Label Colored(string rarity)
    {
        var label = new Label { Text = rarity, CustomMinimumSize = new Vector2(110, 0) };
        label.AddThemeColorOverride("font_color", ColorOf(rarity));
        return label;
    }
}
