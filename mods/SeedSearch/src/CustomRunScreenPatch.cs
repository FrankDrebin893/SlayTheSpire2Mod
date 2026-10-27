using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.CustomRun;
using Timer = Godot.Timer;

namespace RasmusSlayTheSpire2Mod.SeedSearch;

// Adds the Search Seed button once the Custom Run screen has resolved its own nodes.
[HarmonyPatch(typeof(NCustomRunScreen), nameof(NCustomRunScreen._Ready))]
public static class CustomRunScreenPatch
{
    private static readonly FieldInfo SeedInputField = AccessTools.Field(typeof(NCustomRunScreen), "_seedInput");

    static void Postfix(NCustomRunScreen __instance)
    {
        try
        {
            AddButton(__instance);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Search Seed button could not be added to the Custom Run screen. The game probably updated. {e}");
        }
    }

    private static void AddButton(NCustomRunScreen screen)
    {
        LineEdit seedInput = (LineEdit)SeedInputField.GetValue(screen)!;
        Button button = new()
        {
            Name = "SearchSeedButton",
            Text = "Search Seed",
            TooltipText = "Find a seed by what it gives you",
            FocusMode = Control.FocusModeEnum.None
        };
        if (seedInput.GetThemeFont("font") is { } font)
            button.AddThemeFontOverride("font", font);
        button.AddThemeFontSizeOverride("font_size", 22);
        button.Pressed += () => Open(screen);

        StyleBoxFlat Box(Color fill, Color border)
        {
            StyleBoxFlat box = new() { BgColor = fill, BorderColor = border };
            box.SetBorderWidthAll(3);
            box.SetCornerRadiusAll(8);
            box.ContentMarginLeft = box.ContentMarginRight = 16;
            return box;
        }

        // Teal like the game's own Randomize button.
        Color gold = new(0.95f, 0.78f, 0.35f);
        button.AddThemeStyleboxOverride("normal", Box(new Color(0.15f, 0.36f, 0.4f), new Color(0.09f, 0.22f, 0.25f)));
        button.AddThemeStyleboxOverride("hover", Box(new Color(0.2f, 0.47f, 0.52f), gold));
        button.AddThemeStyleboxOverride("pressed", Box(new Color(0.11f, 0.27f, 0.3f), gold));

        // To the right of the seed box, in the gap before the modifier list. Positioned by hand
        // against the seed box, because that sits in a scene layout this mod cannot extend.
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

        // Polling the lobby is simpler and sturdier than hooking every way the character or the
        // lobby type can change. The search is singleplayer only and needs a picked character.
        Timer timer = new() { WaitTime = 0.3, Autostart = true };
        timer.Timeout += () => button.Visible = SeedSearchScreen.CanOpen(screen);
        screen.AddChild(timer);
        button.Visible = false;
    }

    private static void Open(NCustomRunScreen screen)
    {
        try
        {
            if (SeedSearchScreen.CanOpen(screen))
                SeedSearchScreen.Open(screen);
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Seed search screen could not be opened. The game probably updated. {e}");
        }
    }
}
