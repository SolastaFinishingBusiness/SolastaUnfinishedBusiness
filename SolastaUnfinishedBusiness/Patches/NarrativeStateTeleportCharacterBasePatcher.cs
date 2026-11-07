using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeStateTeleportCharacterBasePatcher
{
    //PATCH: heroes without a role follow when a scripted teleport moves the whole party (PARTYSIZE)
    [HarmonyPatch(typeof(NarrativeStateTeleportCharacterBase), nameof(NarrativeStateTeleportCharacterBase.Begin))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Begin_Patch
    {
        // where everyone stood, for a hero who has to give up their place to someone this teleport sends there
        [UsedImplicitly]
        public static void Prefix(
            NarrativeStateTeleportCharacterBase __instance, out Dictionary<WorldLocationCharacter, Vector3> __state)
        {
            __state = __instance.NarrativeSequence.PlayerActors
                .Where(x => x)
                .Distinct()
                .ToDictionary(x => x, x => x.transform.position);
        }

        [UsedImplicitly]
        public static void Postfix(
            NarrativeStateTeleportCharacterBase __instance, Dictionary<WorldLocationCharacter, Vector3> __state)
        {
            MultiplayerContext.Guarded("a cutscene party teleport",
                () => MultiplayerContext.PlaceHeroesLeftByTeleport(__instance, __state));
        }
    }
}
