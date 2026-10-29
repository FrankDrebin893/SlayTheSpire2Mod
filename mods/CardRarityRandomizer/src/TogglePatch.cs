using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;

namespace CardRarityRandomizer;

public static class TogglePatch
{
    public static void Apply(Harmony harmony)
    {
        try
        {
            var charReady = typeof(NCharacterSelectScreen).GetMethod(nameof(NCharacterSelectScreen._Ready), BindingFlags.Public | BindingFlags.Instance);
            if (charReady != null)
                harmony.Patch(charReady, postfix: new HarmonyMethod(typeof(TogglePatch), nameof(CharSelectReadyPostfix)));
            else
                MainFile.Logger.Error("NCharacterSelectScreen._Ready not found.");

            var customReady = typeof(NCustomRunScreen).GetMethod(nameof(NCustomRunScreen._Ready), BindingFlags.Public | BindingFlags.Instance);
            if (customReady != null)
                harmony.Patch(customReady, postfix: new HarmonyMethod(typeof(TogglePatch), nameof(CustomRunReadyPostfix)));
            else
                MainFile.Logger.Error("NCustomRunScreen._Ready not found.");

            var topBarInput = typeof(NTopBar).GetMethod(nameof(NTopBar._Input), BindingFlags.Public | BindingFlags.Instance);
            if (topBarInput != null)
                harmony.Patch(topBarInput, postfix: new HarmonyMethod(typeof(TogglePatch), nameof(TopBarInputPostfix)));
            else
                MainFile.Logger.Error("NTopBar._Input not found.");

            MainFile.Logger.Info("Card Rarity Randomizer UI toggle patches applied.");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Failed applying UI toggle patches: {e}");
        }
    }

    public static void CharSelectReadyPostfix(NCharacterSelectScreen __instance)
    {
        try
        {
            AddToggleButton(__instance, "ConfirmButton");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not add randomizer toggle to Character Select: {e}");
        }
    }

    public static void CustomRunReadyPostfix(NCustomRunScreen __instance)
    {
        try
        {
            AddToggleButton(__instance, "ConfirmButton");
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not add randomizer toggle to Custom Run: {e}");
        }
    }

    public static void TopBarInputPostfix(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 })
        {
            SummaryScreen.Toggle();
        }
    }

    private static void AddToggleButton(Control screen, string anchorNodeName)
    {
        var anchor = screen.GetNodeOrNull<Control>(anchorNodeName);
        var button = new Button
        {
            Name = "CardRandomizerToggleButton",
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(240, 42)
        };

        void Refresh()
        {
            if (Settings.Enabled)
            {
                button.Text = "🎲 Randomizer: ON";
                button.TooltipText = "Card Rarity Randomizer is ENABLED for new runs.\nCard rarities will be shuffled inside each card pool.\nClick to turn OFF.";
                button.AddThemeStyleboxOverride("normal", MakeBox(new Color(0.24f, 0.12f, 0.36f), new Color(0.95f, 0.78f, 0.35f)));
                button.AddThemeStyleboxOverride("hover", MakeBox(new Color(0.32f, 0.16f, 0.48f), new Color(1f, 0.9f, 0.5f)));
                button.AddThemeStyleboxOverride("pressed", MakeBox(new Color(0.18f, 0.08f, 0.28f), new Color(1f, 0.9f, 0.5f)));
                button.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.65f));
            }
            else
            {
                button.Text = "🎲 Randomizer: OFF";
                button.TooltipText = "Card Rarity Randomizer is DISABLED.\nRuns will use default vanilla card rarities.\nClick to turn ON.";
                button.AddThemeStyleboxOverride("normal", MakeBox(new Color(0.12f, 0.13f, 0.15f), new Color(0.4f, 0.42f, 0.46f)));
                button.AddThemeStyleboxOverride("hover", MakeBox(new Color(0.18f, 0.19f, 0.22f), new Color(0.6f, 0.62f, 0.68f)));
                button.AddThemeStyleboxOverride("pressed", MakeBox(new Color(0.08f, 0.09f, 0.1f), new Color(0.5f, 0.52f, 0.58f)));
                button.AddThemeColorOverride("font_color", new Color(0.65f, 0.65f, 0.65f));
            }
        }

        button.Pressed += () =>
        {
            Settings.Enabled = !Settings.Enabled;
        };

        Action onSettingsChanged = Refresh;
        Settings.Changed += onSettingsChanged;
        button.TreeExiting += () => Settings.Changed -= onSettingsChanged;

        Refresh();
        screen.AddChild(button);

        void Place()
        {
            if (anchor != null && anchor.IsInsideTree())
            {
                Rect2 rect = anchor.GetGlobalRect();
                if (rect.Size.X > 0 && rect.Size.Y > 0)
                {
                    button.Size = new Vector2(rect.Size.X, 42);
                    button.GlobalPosition = new Vector2(rect.Position.X, rect.Position.Y - 52);
                    return;
                }
            }
            button.Position = new Vector2(1520, 920);
        }

        if (anchor != null)
        {
            anchor.ItemRectChanged += Place;
        }
        screen.VisibilityChanged += Place;
        Callable.From(Place).CallDeferred();
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
}
