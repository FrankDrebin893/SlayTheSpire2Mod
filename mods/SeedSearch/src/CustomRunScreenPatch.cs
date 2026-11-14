using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Timer = Godot.Timer;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Adds the Search Seed button to both the Custom Run screen and the Standard Run (Character Select) screen.
[HarmonyPatch]
public static class CustomRunScreenPatch
{
    private static readonly FieldInfo SeedInputField = AccessTools.Field(typeof(NCustomRunScreen), "_seedInput");
    private static readonly FieldInfo EmbarkButtonField = AccessTools.Field(typeof(NCharacterSelectScreen), "_embarkButton");

    [HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen._Ready))]
    [HarmonyPostfix]
    static void PostfixCustomRun(NCustomRunScreen __instance)
    {
        try
        {
            AddCustomRunButton(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Search Seed button could not be added to the Custom Run screen. The game probably updated. {e}");
        }
    }

    [HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen._Ready))]
    [HarmonyPostfix]
    static void PostfixCharacterSelect(NCharacterSelectScreen __instance)
    {
        try
        {
            AddCharacterSelectButton(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Search Seed button could not be added to the Character Select screen. {e}");
        }
    }

    private static Button MakeStyledButton(string text, string tooltip, Font? font)
    {
        Button button = new()
        {
            Text = text,
            TooltipText = tooltip,
            FocusMode = Control.FocusModeEnum.None
        };
        if (font != null)
            button.AddThemeFontOverride("font", font);
        button.AddThemeFontSizeOverride("font_size", 22);

        StyleBoxFlat Box(Color fill, Color border)
        {
            StyleBoxFlat box = new() { BgColor = fill, BorderColor = border };
            box.SetBorderWidthAll(3);
            box.SetCornerRadiusAll(8);
            box.ContentMarginLeft = box.ContentMarginRight = 16;
            box.ContentMarginTop = box.ContentMarginBottom = 6;
            return box;
        }

        Color gold = new(0.95f, 0.78f, 0.35f);
        button.AddThemeStyleboxOverride("normal", Box(new Color(0.15f, 0.36f, 0.4f), new Color(0.09f, 0.22f, 0.25f)));
        button.AddThemeStyleboxOverride("hover", Box(new Color(0.2f, 0.47f, 0.52f), gold));
        button.AddThemeStyleboxOverride("pressed", Box(new Color(0.11f, 0.27f, 0.3f), gold));
        return button;
    }

    private static void AddCustomRunButton(NCustomRunScreen screen)
    {
        LineEdit seedInput = (LineEdit)SeedInputField.GetValue(screen)!;
        Button button = MakeStyledButton("Search Seed", "Find a seed by what it gives you", seedInput.GetThemeFont("font"));
        button.Name = "SearchSeedButton";
        button.Pressed += () =>
        {
            if (SeedSearchScreen.CanOpen(screen))
                SeedSearchScreen.Open(screen);
        };

        screen.AddChild(button);
        void Place()
        {
            Rect2 seed = seedInput.GetGlobalRect();
            button.Size = new Vector2(button.GetCombinedMinimumSize().X, seed.Size.Y);
            button.GlobalPosition = new Vector2(seed.End.X + 14, seed.Position.Y);
        }

        seedInput.ItemRectChanged += Place;
        screen.VisibilityChanged += Place;
        Callable.From(Place).CallDeferred();

        Timer timer = new() { WaitTime = 0.3, Autostart = true };
        timer.Timeout += () => button.Visible = SeedSearchScreen.CanOpen(screen);
        screen.AddChild(timer);
        button.Visible = false;
    }

    private static void AddCharacterSelectButton(NCharacterSelectScreen screen)
    {
        Control? embark = EmbarkButtonField?.GetValue(screen) as Control;
        Font? font = screen.GetNodeOrNull<Control>("InfoPanel/VBoxContainer/Name")?.GetThemeFont("font");

        HBoxContainer bar = new() { Name = "StandardSeedSearchBar" };
        bar.AddThemeConstantOverride("separation", 8);

        Button searchBtn = MakeStyledButton("Search Seed", "Search or preview seeds for this Standard Run (keeps achievements & unlocks enabled!)", font);
        Button clearBtn = MakeStyledButton("✕", "Clear seed and return to random seed", font);
        clearBtn.Visible = false;

        void RefreshText()
        {
            string? seed = screen.Lobby?.Seed;
            bool hasSeed = !string.IsNullOrEmpty(seed);
            searchBtn.Text = hasSeed ? $"Seed: {seed}" : "Search Seed";
            clearBtn.Visible = hasSeed;
        }

        searchBtn.Pressed += () =>
        {
            if (SeedSearchScreen.CanOpen(screen))
                SeedSearchScreen.Open(screen, _ => RefreshText());
        };
        clearBtn.Pressed += () =>
        {
            screen.Lobby?.SetSeed(null);
            RefreshText();
        };

        bar.AddChild(searchBtn);
        bar.AddChild(clearBtn);
        screen.AddChild(bar);

        void Place()
        {
            Vector2 minSize = bar.GetCombinedMinimumSize();
            bar.Size = minSize;
            if (embark != null && embark.IsInsideTree())
            {
                Rect2 r = embark.GetGlobalRect();
                bar.GlobalPosition = new Vector2(r.End.X - minSize.X - 24, r.Position.Y - minSize.Y - 12);
            }
            else
            {
                Rect2 vp = screen.GetViewportRect();
                bar.GlobalPosition = new Vector2(vp.Size.X - minSize.X - 48, vp.Size.Y - minSize.Y - 140);
            }
        }

        screen.VisibilityChanged += Place;
        Callable.From(Place).CallDeferred();

        Timer timer = new() { WaitTime = 0.3, Autostart = true };
        timer.Timeout += () =>
        {
            bool canOpen = SeedSearchScreen.CanOpen(screen);
            bar.Visible = canOpen;
            if (canOpen)
            {
                RefreshText();
                Place();
            }
        };
        screen.AddChild(timer);
        bar.Visible = false;
    }
}
