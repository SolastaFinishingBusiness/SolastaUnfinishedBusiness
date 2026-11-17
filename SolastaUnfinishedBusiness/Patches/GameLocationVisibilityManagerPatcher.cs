using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Feats;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Races;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationVisibilityManagerPatcher
{
    //PATH: supports `RaceLightSensitivityApplyOutdoorsOnly`
    //need to force lightning affinity effects recalculation if one of the dark races
    [HarmonyPatch(typeof(GameLocationVisibilityManager), nameof(GameLocationVisibilityManager.ComputeIllumination))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RevealCharacter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            LocationDefinitions.LightingState __result,
            IIlluminable illuminable,
            GameLocationCharacter previewCharacter)
        {
            // A preview only asks what the light would be, for the screen of the computer it runs on: the game's method
            // writes the lighting state only when previewCharacter is null. Hovering a location or an enemy therefore
            // must not change a character (object-ID desyncs): that happens on one computer only. The answer the caller
            // gets is unaffected. Real light updates still reach the check below, which is what notices a hero walking
            // from a bright interior to a bright exterior (the state stays Bright, so the game's setter doesn't).
            if (previewCharacter != null)
            {
                ObjectIdDiagnosticsContext.LightingPreviewSkipped();

                return;
            }

            if (__result != LocationDefinitions.LightingState.Bright ||
                illuminable is not GameLocationCharacter { RulesetCharacter: not null } glc)
            {
                return;
            }

            var originalHero = glc.RulesetCharacter.GetOriginalHero();

            if (originalHero != null &&
                (originalHero.SubRaceDefinition == RaceKoboldBuilder.SubraceDarkKobold ||
                 originalHero.SubRaceDefinition == SubraceDarkelfBuilder.SubraceDarkelf ||
                 originalHero.SubRaceDefinition == SubraceGrayDwarfBuilder.SubraceGrayDwarf))
            {
                glc.CheckLightingAffinityEffects();
            }
        }
    }

    //PATCH: re-light the creatures in an area whose sight-impaired cells changed (Darkness, Fog Cloud, smoke and other
    //heavily obscuring clouds appearing, moving or ending)
    // The game's handler (TolopologyChanged, raised by GameLocationEnvironmentManager.ApplyTopologyChangeOfGlobalEffects
    // after it has set or cleared CellFlags.Runtime.DynamicSightImpaired) only marks the light sources near the impacted
    // sectors dirty, plus line of sight. A creature's lighting is recomputed (ComputeIllumination, through UpdateLights)
    // only when it moves, is placed, changes prone state, the hour changes the outdoor light, or a dirty light source is in
    // range of it. So a creature standing still with no light source nearby (outdoors by day, no torch) keeps its old
    // lighting state when a cloud covers or uncovers it: its light affinities don't change, and the daylight rule for
    // Dark Kobolds, Dark Elves and Gray Dwarves (Races/RacialSunlightSensitivity) keeps or withholds Sunlight
    // Sensitivity until it moves (found by reading the game code; a light source nearby hides it).
    // This queues every valid creature standing in an impacted sector for the game's normal light update, so the same
    // update the game runs after a move runs now too. It covers all creatures, monsters with their own light affinities
    // included, which is the game's own rule applied consistently. Over-queueing is harmless: the update recomputes the
    // same state, and the mod's CheckLightingAffinityEffects patch only changes conditions that differ, so a creature
    // whose light didn't change uses no object ID. Every computer sees the same area changes, so they queue the same
    // creatures (the HashSet's order can still differ between computers, as with every other light update).
    [HarmonyPatch(typeof(GameLocationVisibilityManager), "TolopologyChanged")]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TolopologyChanged_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            GameLocationVisibilityManager __instance,
            List<GameSector> impactedSectors,
            List<TopologyForm.Type> impactedTopologyFlags)
        {
            // as in the game's handler: nothing before the location is ready, and only sight-impaired changes (a null
            // list means all kinds). Sight blockers (walls, doors) already reach creatures through their light sources.
            // the user can turn this fix off: Rules > "Update lighting ... when Darkness ... changes" (on by default)
            if (!Main.Settings.RefreshLightingWhenObscurementChanges ||
                !__instance.GameLocationService.LocationIsReady ||
                impactedSectors == null ||
                (impactedTopologyFlags != null && !impactedTopologyFlags.Contains(TopologyForm.Type.SightImpaired)))
            {
                return;
            }

            foreach (var character in __instance.gameLocationCharacterService.ValidCharacters)
            {
                // the game's light check samples every cell of the creature's perception box (GetAllPositionsToCheck),
                // so a large creature whose origin is in one sector can stand partly in another; test the whole box
                // Boxes are inclusive, built the same way as the game's own handler.
                var characterBox = character.LocationPerceptionBoundingBox;

                foreach (var sector in impactedSectors)
                {
                    if (sector == null ||
                        !new BoxInt(sector.MinPosition, sector.MaxPosition).Intersects(characterBox))
                    {
                        continue;
                    }

                    __instance.dirtyIlluminables.Add(character);

                    break;
                }
            }
        }
    }

    //PATCH: supports lighting and obscurement feature by allowing ranged targeting within obscured areas
    //this is mainly vanilla code except for the BEGIN/END patch block
    [HarmonyPatch(typeof(GameLocationVisibilityManager),
        nameof(GameLocationVisibilityManager.IsPositionPerceivedByCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsPositionPerceivedByCharacter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GameLocationVisibilityManager __instance,
            ref bool __result,
            Vector3 position,
            Vector3 origin,
            RulesetCharacter rulesetCharacter,
            List<SenseMode.Type> optionalRequiredSense = null)
        {
            var fromWorldPosition1 =
                __instance.gameLocationPositioningService.GetGridPositionFromWorldPosition(origin);
            var fromWorldPosition2 =
                __instance.gameLocationPositioningService.GetGridPositionFromWorldPosition(position);
            var hasImpairedSight = false;

            // BEGIN PATCH
            // impaired sight should not prevent targeting
            if (!Main.Settings.UseOfficialLightingObscurementAndVisionRules)
            {
                var gridAccessor = GridAccessor.Default;

                hasImpairedSight =
                    rulesetCharacter.ImpairedSight ||
                    (gridAccessor.RuntimeFlags(fromWorldPosition1) & CellFlags.Runtime.DynamicSightImpaired) != 0;
            }
            // END PATCH

            var magnitude = (fromWorldPosition1 - fromWorldPosition2).magnitude;

            // ReSharper disable once ForeachCanBeConvertedToQueryUsingAnotherGetEnumerator
            foreach (var senseMode in rulesetCharacter.SenseModes)
            {
                var senseType = senseMode.SenseType;

                if (senseType == SenseMode.Type.None ||
                    (optionalRequiredSense is { Count: > 0 } && !optionalRequiredSense.Contains(senseType)))
                {
                    continue;
                }

                var senseRange = senseMode.SenseRange;

                if (magnitude > senseRange ||
                    (hasImpairedSight && SenseMode.CanBeImpaired(senseType) && magnitude > 1.7999999523162842) ||
                    __instance.gameLocationPositioningService.RaycastGridSightBlocker(
                        origin, position, __instance.GameLocationService))
                {
                    continue;
                }

                __result = true;

                return false;
            }

            __result = false;

            return false;
        }
    }

    //PATH: supports Stealthy Feat behavior
    //vanilla code except for the BEGIN/END patch block
    [HarmonyPatch(typeof(GameLocationVisibilityManager), nameof(GameLocationVisibilityManager.UpdatePerception))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UpdatePerception_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationVisibilityManager __instance)
        {
            UpdatePerception(__instance);

            return false;
        }

        private static void UpdatePerception(GameLocationVisibilityManager __instance)
        {
            var scopedProfiler = new ScopedProfiler(nameof(UpdatePerception), ProfilerLog.Level.Normal);

            foreach (var validCharacter in __instance.gameLocationCharacterService.ValidCharacters)
            {
                if (validCharacter.PreviousPositionKnownToTheParty != validCharacter.PositionKnownToTheParty)
                {
                    if (validCharacter.PositionKnownToTheParty)
                    {
                        validCharacter.HasBeenPerceivedOnceByParty = true;
                    }

                    var toThePartyChanged = validCharacter.PositionKnownToThePartyChanged;

                    toThePartyChanged?.Invoke(validCharacter, validCharacter.PositionKnownToTheParty);
                }

                validCharacter.PreviousPositionKnownToTheParty = validCharacter.PositionKnownToTheParty;
                validCharacter.IsPerceivedByFoes = false;
            }

            foreach (var validCharacter in __instance.gameLocationCharacterService.ValidCharacters)
            {
                __instance.gameLocationCharacterService.PartyHasCharacter(validCharacter, true);

                validCharacter.PerceivedFoes.Clear();
                validCharacter.PerceivedAllies.Clear();

                foreach (var key in validCharacter.LineOfSightRatio.Keys)
                {
                    if (validCharacter.IsOppositeSide(key.Side))
                    {
                        if (!__instance.IsCharacterPerceivedByCharacter(key, validCharacter))
                        {
                            continue;
                        }

                        //BEGIN PATCH
                        if (OtherFeats.FeatStealthPositionsCache
                                .TryGetValue(key, out var stealthyPositions) &&
                            stealthyPositions.Contains(key.LocationPosition))
                        {
                            continue;
                        }
                        //END PATCH

                        validCharacter.PerceivedFoes.Add(key);
                        key.IsPerceivedByFoes = true;
                    }
                    else if (validCharacter != key && __instance.IsCharacterPerceivedByCharacter(key, validCharacter))
                    {
                        validCharacter.PerceivedAllies.Add(key);
                    }
                }
            }

            __instance.stealthyCharactersCache.Clear();

            foreach (var stealthyCharacter in __instance.stealthyCharacters)
            {
                __instance.stealthyCharactersCache.Add(stealthyCharacter);
            }

            foreach (var locationCharacter in __instance.stealthyCharactersCache)
            {
                locationCharacter.UpdateStealthStatus();
            }

            scopedProfiler.Dispose();
        }
    }
}
