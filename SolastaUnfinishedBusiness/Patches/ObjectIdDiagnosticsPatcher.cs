using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// TEMPORARY: feeds ObjectIdDiagnosticsContext (the object-ID desync diagnostics). Every patch here only records; none
// changes what the game does or returns.
[UsedImplicitly]
public static class ObjectIdDiagnosticsPatcher
{
    //PATCH: every ruleset object registration, with the counter before and the ID given or kept
    [HarmonyPatch(typeof(RulesetEntity), nameof(RulesetEntity.Register))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEntity_Register
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.BeforeRegister();
            }
        }

        [UsedImplicitly]
        public static void Postfix(RulesetEntity __instance, bool generateGuid)
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.AfterRegister(__instance, generateGuid);
            }
        }
    }

    //PATCH: an ID handed out other than through Register (none known)
    [HarmonyPatch(typeof(RulesetEntityManager), nameof(RulesetEntityManager.GenerateGuid))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEntityManager_GenerateGuid
    {
        [UsedImplicitly]
        public static void Postfix(ulong __result)
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.AfterGenerateGuid(__result);
            }
        }
    }

    //PATCH: objects leaving the registry (their IDs are never given back)
    [HarmonyPatch(typeof(RulesetEntityManager), nameof(RulesetEntityManager.UnregisterEntity))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEntityManager_UnregisterEntity
    {
        [UsedImplicitly]
        public static void Prefix(RulesetEntity rulesetEntity)
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.Unregistered(rulesetEntity);
            }
        }
    }

    //PATCH: the counter saved into or loaded from a save
    [HarmonyPatch(typeof(RulesetEntityManager), nameof(RulesetEntityManager.SerializeAttributes))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEntityManager_SerializeAttributes
    {
        [UsedImplicitly]
        public static void Postfix(IAttributesSerializer serializer)
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.CounterSerialized(serializer?.Mode.ToString() ?? "serialized");
            }
        }
    }

    //PATCH: the counter set back to its start
    [HarmonyPatch(typeof(RulesetEntityManager), nameof(RulesetEntityManager.ResetCurrentGuid))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEntityManager_ResetCurrentGuid
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.CounterReset();
            }
        }
    }

    //PATCH: a character revealed, recorded before the game's reveal and the mod's own reveal postfix (the fire-monster
    // light, a candidate cause) run, with whether the character was already visible (the game then does nothing)
    [HarmonyPatch(typeof(GameLocationCharacterManager), nameof(GameLocationCharacterManager.RevealCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationCharacterManager_RevealCharacter
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter character)
        {
            if (ObjectIdDiagnosticsContext.Active)
            {
                ObjectIdDiagnosticsContext.Revealed(character);
            }
        }
    }

    //PATCH: a map starts loading (a game load or a map change)
    [HarmonyPatch(typeof(GameLocationManager), nameof(GameLocationManager.LoadLocationAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationManager_LoadLocationAsync
    {
        [UsedImplicitly]
        public static void Prefix(string locationDefinitionName, bool fromSavedGame)
        {
            ObjectIdDiagnosticsContext.Transition($"map load {locationDefinitionName} fromSave={fromSavedGame}");
        }
    }

    //PATCH: a map starts unloading
    [HarmonyPatch(typeof(GameLocationManager), nameof(GameLocationManager.UnloadLocationAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationManager_UnloadLocationAsync
    {
        [UsedImplicitly]
        public static void Prefix(string locationDefinitionName)
        {
            ObjectIdDiagnosticsContext.Transition($"map unload {locationDefinitionName}");
        }
    }

    //PATCH: a saved game finished loading
    [HarmonyPatch(typeof(Game), nameof(Game.PostLoad))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Game_PostLoad
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            ObjectIdDiagnosticsContext.Transition("game loaded");
        }
    }
}
