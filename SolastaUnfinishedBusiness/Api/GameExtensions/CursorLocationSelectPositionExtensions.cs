using System.Collections;
using SolastaUnfinishedBusiness.Models;
using TA;

namespace SolastaUnfinishedBusiness.Api.GameExtensions;

internal static class CursorLocationSelectPositionExtensions
{
    // this is vanilla code where only change is call MyIsCellPerceivedByCharacter instead of default one
    public static IEnumerator MyComputeValidPositions(
        this CursorLocationSelectPosition __instance,
        LocationDefinitions.LightingState additionalBlockedState = LocationDefinitions.LightingState.Darkness,
        int maxDistance = 0)
    {
        var boxInt = new BoxInt(__instance.ActionParams.ActingCharacter.LocationPosition, int3.zero, int3.zero);

        if (maxDistance == 0)
        {
            maxDistance = (int)__instance.maxDistance;
        }

        boxInt.Inflate(maxDistance);

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
        var visibilityService = ServiceRepository.GetService<IGameLocationVisibilityService>();
        var onlyFeedbackGroundCells = __instance.isTeleportingSpell;

        foreach (var int3 in boxInt.EnumerateAllPositionsWithin())
        {
            var locationCharacter = __instance.ActionParams?.ActingCharacter;

            if (locationCharacter == null)
            {
                break;
            }

            // must use vanilla distance calculation here
            if (int3.Distance(__instance.centerPosition, int3) <= maxDistance &&
                positioningService.CanPlaceCharacter(locationCharacter, int3, CellHelpers.PlacementMode.Station) &&
                positioningService.CanCharacterStayAtPosition_Floor(
                    locationCharacter, int3, onlyCheckCellsWithRealGround: onlyFeedbackGroundCells))
            {
                if (visibilityService.MyIsCellPerceivedByCharacter(int3, locationCharacter,
                        additionalBlockedLightingState: additionalBlockedState,
                        requireLineOfSight: __instance.requiresVisibilityForPosition))
                {
                    __instance.validPositionsCache.Add(int3);
                }
            }

            if (__instance.stopwatch.Elapsed.TotalMilliseconds > 0.5)
            {
                yield return null;
            }
        }
    }

    /// <summary>
    ///     Whether a creature that a feature walks to a picked spot (Instinctive Pounce, Second Wind's move, Withdraw,
    ///     Remarkable Athlete, Maneuvering Attack, Strategic Reposition) may end its move at this position.
    /// </summary>
    /// <remarks>
    ///     The floor part is the game's own picker rule (CursorLocationSelectPosition.ComputeValidPositions): only a
    ///     teleport must end on real ground, so for these walks a flier may stop in mid-air and a walker needs a floor.
    ///     The wall part follows the game's own movement cursor (CursorLocationExplorationCharacter
    ///     .RefreshMovementHelper): a creature with the OnWalls motion range, which the game sets for any movement
    ///     affinity that can move on walls (Spider Climb and others), may also end on a climbable wall
    ///     (CanCharacterStayAtPosition_Walls, not limited to easy climbs). Behind the setting
    ///     EnableAllyMovementToWalls, on by default (user, 2026-11-15: "allow walls as designations for characters
    ///     who can walk on walls", default on; #66). Never for a teleport, which keeps the game's real-ground rule.
    /// </remarks>
    internal static bool CanEndMoveAt(
        this CursorLocationSelectPosition cursor,
        IGameLocationPositioningService positioningService,
        GameLocationCharacter character,
        int3 position)
    {
        if (positioningService.CanCharacterStayAtPosition_Floor(
                character, position, onlyCheckCellsWithRealGround: cursor.isTeleportingSpell))
        {
            return true;
        }

        return Main.Settings.EnableAllyMovementToWalls &&
               !cursor.isTeleportingSpell &&
               character.CanMoveInSituation(RulesetCharacter.MotionRange.OnWalls) &&
               positioningService.CanCharacterStayAtPosition_Walls(character, position, false);
    }
}
