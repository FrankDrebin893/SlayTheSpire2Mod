using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace MapPathHighlight;

[HarmonyPatch(typeof(NMapPoint), "OnFocus")]
public static class MapPointFocusPatch
{
    public static void Postfix(NMapPoint __instance)
    {
        MapPathAnimator.Instance.OnPointFocused(__instance);
    }
}

[HarmonyPatch(typeof(NMapPoint), "OnUnfocus")]
public static class MapPointUnfocusPatch
{
    public static void Postfix(NMapPoint __instance)
    {
        MapPathAnimator.Instance.OnPointUnfocused(__instance);
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen._Process))]
public static class MapScreenProcessPatch
{
    public static void Postfix(NMapScreen __instance, double delta)
    {
        if (__instance.IsVisibleInTree())
        {
            MapPathAnimator.Instance.OnScreenProcess(delta);
        }
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.Close))]
public static class MapScreenClosePatch
{
    public static void Postfix()
    {
        MapPathAnimator.Instance.OnScreenClosed();
    }
}

[HarmonyPatch(typeof(NMapScreen), "OnMapPointSelectedLocally")]
public static class MapPointSelectedPatch
{
    public static void Prefix()
    {
        MapPathAnimator.Instance.ClearHighlight();
    }
}

[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetMap))]
public static class MapScreenSetMapPatch
{
    public static void Postfix()
    {
        MapPathAnimator.Instance.OnMapReset();
    }
}
