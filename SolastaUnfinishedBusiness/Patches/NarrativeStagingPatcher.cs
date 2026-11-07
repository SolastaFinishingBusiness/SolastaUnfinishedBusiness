using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// The hooks of CutsceneStagingContext. Each is wrapped so that a camera problem can't stop a conversation.
[UsedImplicitly]
public static class NarrativeStagingPatcher
{
    [HarmonyPatch(typeof(NarrativeState), nameof(NarrativeState.BuildCamera))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildCamera_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeState __instance)
        {
            CutsceneStagingContext.OnCameraBuilt(__instance);
        }
    }

    // after the game's own restoration of the shot, and before the state's camera animation starts from it
    [HarmonyPatch(typeof(NarrativeSequence), nameof(NarrativeSequence.ActivateCameraShot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ActivateCameraShot_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeSequence __instance, string cameraShotName)
        {
            MultiplayerContext.Guarded("a cutscene shot",
                () => CutsceneStagingContext.OnShotActivated(__instance, cameraShotName));
        }
    }

    [HarmonyPatch(typeof(NarrativeStateAnswerChoice), nameof(NarrativeStateAnswerChoice.End))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AnswerChoiceEnd_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            MultiplayerContext.Guarded("a cutscene prompt's end", CutsceneStagingContext.ReleaseLook);
        }
    }

    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.ConcludeNarrativeSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ConcludeNarrativeSequence_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            MultiplayerContext.Guarded("a cutscene's end", CutsceneStagingContext.End);
        }
    }

    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.AbortNarrativeSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AbortNarrativeSequence_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            MultiplayerContext.Guarded("a cutscene's abort", CutsceneStagingContext.End);
        }
    }
}
