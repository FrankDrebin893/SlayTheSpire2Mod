namespace RasmusCardBalance;

// One balance tweak the player can switch on or off. To add a tweak: write its patch class in src/
// and add one entry to Tweaks.All. The settings menu, persistence and conditional patching pick it up.
public sealed record Tweak(string Id, string Name, string Description, Type PatchType, bool DefaultEnabled = true);

public static class Tweaks
{
    public static readonly IReadOnlyList<Tweak> All = new List<Tweak>
    {
        new("master_planner", "Master Planner", "Costs 1 energy instead of 2 (0 when upgraded).", typeof(MasterPlannerPatch)),
        new("unceasing_top", "Unceasing Top", "Unplayable cards (curses, statuses) don't count as a hand; if only those are left, it draws.", typeof(UnceasingTopPatch)),
        new("calculated_gamble", "Calculated Gamble", "Upgrading removes Exhaust instead of adding Retain.", typeof(CalculatedGamblePatch)),
        new("frozen_eye", "Frozen Eye", "Re-added as a shop relic; draw pile cards are shown in order.", typeof(FrozenEyePatch)),
    };
}
