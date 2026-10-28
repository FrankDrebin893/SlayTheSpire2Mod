using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace RasmusCardBalance;

[ModInitializer(nameof(Initialize))]
public static class MainFile
{
    public const string ModId = "RasmusCardBalance";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        Harmony harmony = new(ModId);

        harmony.PatchAll(Assembly.GetExecutingAssembly());
    }
}
