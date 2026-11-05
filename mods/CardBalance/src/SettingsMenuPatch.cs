using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace RasmusCardBalance;

// Adds a "Card Balance" section with one checkbox per tweak to the bottom of Settings > General.
// Built from stock nodes and added in a postfix on NSettingsScreen._Ready, so the screen's own
// code is untouched. Tweaks apply at startup, so the section says a restart is needed.
[HarmonyPatch(typeof(NSettingsScreen), nameof(NSettingsScreen._Ready))]
public static class SettingsMenuPatch
{
    private const string SectionName = "RasmusCardBalanceSection";

    static void Postfix(NSettingsScreen __instance)
    {
        try
        {
            var content = __instance.GetNodeOrNull<NSettingsPanel>("%GeneralSettings")?.Content;
            if (content == null)
            {
                MainFile.Logger.Error("Settings screen has no %GeneralSettings panel; Card Balance toggles not added. The game probably updated.");
                return;
            }

            content.GetNodeOrNull(SectionName)?.QueueFree();
            content.AddChild(BuildSection());
        }
        catch (Exception e)
        {
            MainFile.Logger.Error($"Could not add Card Balance toggles to the settings screen: {e}");
        }
    }

    private static VBoxContainer BuildSection()
    {
        var section = new VBoxContainer { Name = SectionName };
        section.AddThemeConstantOverride("separation", 6);
        section.AddChild(new HSeparator());
        section.AddChild(new Label { Text = "Card Balance (applies after restarting the game)" });

        var note = new Label { Text = "", Modulate = new Color(1f, 0.8f, 0.4f), Visible = false };

        void RefreshNote()
        {
            var pending = Tweaks.All.Any(Settings.NeedsRestart);
            note.Visible = pending;
            note.Text = "Restart the game to apply changes. In multiplayer every player needs matching settings.";
        }

        foreach (var tweak in Tweaks.All)
        {
            var box = new CheckBox
            {
                Text = $"{tweak.Name}: {tweak.Description}",
                TooltipText = "Takes effect after restarting the game.",
                ButtonPressed = Settings.IsEnabled(tweak),
            };
            box.Toggled += on =>
            {
                Settings.SetEnabled(tweak, on);
                RefreshNote();
            };
            section.AddChild(box);
        }

        section.AddChild(note);
        RefreshNote();
        return section;
    }
}
