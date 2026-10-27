using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace CardRarityRandomizer;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "CardRarityRandomizer";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);

        // The patches look their targets up by name and log an error when a game update moved
        // them, so they are applied by hand instead of through attributes.
        RarityPatches.Apply(harmony);
    }
}
