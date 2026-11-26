using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class LoadSaveLinePatcher
{
    //PATCH: shows whether a save depends on the mod on the Save and Load screens
    // LoadSaveLine is used by both the main menu's Load panel and the in-game Save/Load modal.
    [HarmonyPatch(typeof(LoadSaveLine), nameof(LoadSaveLine.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(LoadSaveLine __instance)
        {
            SaveDependencyBadge.Unbind(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(LoadSaveLine __instance)
        {
            SaveDependencyBadge.Bind(__instance);
        }
    }

    [HarmonyPatch(typeof(LoadSaveLine), nameof(LoadSaveLine.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(LoadSaveLine __instance)
        {
            SaveDependencyBadge.Unbind(__instance);
        }
    }
}
