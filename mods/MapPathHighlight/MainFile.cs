using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace MapPathHighlight;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "MapPathHighlight";

    public static Logger Logger { get; } = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);
        harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.Info("MapPathHighlight initialized successfully.");
    }
}
