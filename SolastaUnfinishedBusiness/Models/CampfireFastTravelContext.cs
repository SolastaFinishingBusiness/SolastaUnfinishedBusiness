using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Builders;
using TA;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

// Fast travel for the campfire gather. A fast travel point within 5 cells of the campfire is used when there is one;
// otherwise a temporary one is made. The whole party, dead, dying and unconscious characters included, is moved to it,
// and only when somebody is outside the long rest range. CampfireRestContext runs it before an ordinary campfire long
// rest when the party is out of range (NeedsTravel): the whole party fast travels to the campfire's point, with no
// walk.
//
// The destination is a map waypoint, as the game's fast travel only goes to one:
// - a fast travel waypoint already within ExistingWaypointRadius cells of the campfire is used as it is;
// - otherwise, when the party is out of range, a temporary waypoint is added next to the campfire's user with the
//   game's own GameLocation.AddWaypoint
//   (the game builds its waypoints from the map's WorldNodes when a location loads; none of them is saved, so a
//   temporary one can't reach a save). It is removed once the fast travel to it ends, a long rest starts, another
//   campfire is used, or the location unloads.
// The destination is set where the campfire's functor runs, which is on every computer in the lockstep: in
// multiplayer every computer must have the waypoint, as the game's fast travel looks it up by position on each one.
//
// The travel is the game's own networked fast travel, unchanged (ICommandService.RequestFastTravel, a lockstep
// command; then the map's MapWaypointItem.PrepareFastTravel and GameLocationMapManager.FastTravel, all on every
// computer): every computer shows the map with the leader (the first party member) going along the route, with the
// game's checks for traps and watching enemies, then the whole party is placed in formation at the point. The game
// leaves nobody out: dead, dying and unconscious characters and guests are placed too (SetupPartyOnDestination copies
// the party and guests without any condition check). A leader already on the point travels a route of no steps (the
// game's own samePosition case). A dead, dying or unconscious leader stops the gather before it travels, with a
// message naming them and their state (DescribeDownedLeader).
internal static class CampfireFastTravelContext
{
    // an existing fast travel waypoint this close to the campfire's user is used instead of making one
    private const float ExistingWaypointRadius = 5f;

    // the game's long rest distance (GameRestingManager.CanExecuteLocationRest: every two living party members within
    // this many cells)
    private const float RestDistance = 10f;

    private const string WaypointName = "FinishingBusinessCampfireWaypoint";

    // the destination of the last campfire used, on every computer
    private static Destination _destination;

    // fast travels that placed the party (SetupPartyOnDestination ran to its end), and whether the last one was cut
    // short by watching enemies (GameLocationMapManager.interrupted, which ExecutePath resets at each start), on every
    // computer; the asking computer uses them to know its travel really arrived
    private static int _arrivals;
    private static bool _lastArrivalInterrupted;

    // where the last fast travel that placed the party went, on every computer. In multiplayer another player's fast
    // travel, agreed before the campfire's and queued at an earlier command tick, can still run first here: the game
    // doesn't refuse a second travel request (FastTravelManager.NetworkedFastTravel), and the travels run in tick order.
    // Its arrival isn't the gather's, so the gather compares where an arrival went with its own waypoint (found by
    // reading the game's code, not seen in a game).
    private static int3? _lastArrivalTarget;

    // SetupPartyOnDestination is running: the game clears IsFastTraveling and asks for the exploration state at the
    // start of that arrival step (IL_009b), and only then places the party and fades back in over later frames, so
    // "not travelling, exploring" doesn't mean the travel is over while this is set (otherwise the gather
    // reports a failed travel although the party arrived)
    private static bool _arriving;

    #region Destination

    // FunctorStartLongRest on every computer: the campfire's fast travel destination, an existing waypoint within
    // ExistingWaypointRadius cells or, when the party is out of range, a temporary one. Positions and waypoints are the
    // same on every computer, so all of them pick the same one.
    internal static void PrepareDestination(GameLocationCharacter user)
    {
        RemoveDestination();

        var gameLocation = ServiceRepository.GetService<IGameLocationService>()?.GameLocation;

        if (user == null || !gameLocation || !Main.Settings.GatherPartyForCampfireRest)
        {
            return;
        }

        // no second fast travel point is made next to one that is already there; a waypoint
        // hidden by the map (its node inactive) doesn't count, as the game doesn't offer it either. Of those, one the
        // party can fast travel to now (revealed, and in the leader's area when the waypoint asks for that: TryStart's
        // conditions) comes first, so a nearer unusable point doesn't hide a usable one. When
        // none is usable the nearest is kept, and TryStart then fails the gather: no temporary point
        // is made beside an existing one. MapWaypoints has the same order on every computer, so all pick the same one.
        var leader = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters.FirstOrDefault();
        var existing = gameLocation.MapWaypoints
            .Where(x => x.Definition is { AllowFastTravel: true, LocationExit: false } && x.Shown &&
                        Distance(x.Position, user.LocationPosition) <= ExistingWaypointRadius)
            .OrderBy(x => x.Revealed && leader != null && IsInWaypointArea(x, leader) ? 0 : 1)
            .ThenBy(x => Distance(x.Position, user.LocationPosition))
            .FirstOrDefault();

        if (existing != null)
        {
            _destination = new Destination { GameLocation = gameLocation, User = user, Waypoint = existing };

            return;
        }

        // in range: no fast travel will be needed, so no temporary point
        if (!NeedsTravel(user))
        {
            return;
        }

        // the free cell is found as for another party member, so the user's own cell isn't picked
        var other = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters
            .FirstOrDefault(x => x != user) ?? user;
        var waypoint = AddTemporaryWaypoint(gameLocation, user, other);

        if (waypoint != null)
        {
            _destination = new Destination
            {
                GameLocation = gameLocation, User = user, Waypoint = waypoint, Temporary = true
            };
        }
    }

    // a waypoint next to the campfire's user, on a free cell (a real one stands on an open cell, and the fast travel
    // paths the leader to it), found as the game finds a place for another party member there
    private static GameMapWaypoint AddTemporaryWaypoint(
        GameLocation gameLocation, GameLocationCharacter user, GameLocationCharacter other)
    {
        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var worldLocation = ServiceRepository.GetService<IGameLocationService>()?.WorldLocation;

        if (positioning == null || characterService == null || !worldLocation)
        {
            return null;
        }

        var positions = new List<int3>();
        var sizes = new List<RulesetActor.SizeParameters>();

        positioning.ComputeFormationPlacementPositions([other], user.LocationPosition, user.Orientation,
            characterService.PartyFormation.FormationPositions, CellHelpers.PlacementMode.PlanningStation, positions,
            sizes, 10, false, false);

        if (positions.Count == 0)
        {
            return null;
        }

        var definition = ScriptableObject.CreateInstance<MapWaypointDefinition>();

        definition.name = WaypointName;
        definition.guiPresentation =
            GuiPresentationBuilder.Build(null, "Screen/&CampfireFastTravelWaypointTitle", Gui.NoLocalization);
        definition.AllowFastTravel = true;
        definition.LongRestAvailable = true;

        // under the location's scene, so it goes away with the location; placed before it is bound, as
        // GameMapWaypoint.Bind reads its cell from its position
        var gameObject = new GameObject(WaypointName);

        gameObject.transform.SetParent(worldLocation.transform, false);
        gameObject.transform.position = positioning.GetWorldPositionFromGridPosition(positions[0]);

        var node = gameObject.AddComponent<WorldNode>();

        node.mapWaypointDefinition = definition;
        node.revealedByDefault = true;

        // the game's own registration (it appends the new waypoint)
        gameLocation.AddWaypoint(node);

        return gameLocation.MapWaypoints.LastOrDefault();
    }

    // the party is out of the long rest range, so the gather needs its fast travel: the game's rule fails (two living
    // party members more than RestDistance cells apart), or a party member, dead, dying or unconscious included (whom
    // the game's rule skips, so they'd be left behind), is more than RestDistance cells from the campfire's user
    internal static bool NeedsTravel(GameLocationCharacter user)
    {
        return FindOutOfRange(user) != null;
    }

    // the party member that puts the party out of the long rest range (NeedsTravel), or null when it is in range: the
    // first one more than RestDistance cells from the campfire's user, otherwise, of the first two living members too
    // far apart, the one further from the user
    private static GameLocationCharacter FindOutOfRange(GameLocationCharacter user)
    {
        var party = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters;

        if (user == null || party == null)
        {
            return null;
        }

        var far = party.FirstOrDefault(x => Distance(x.LocationPosition, user.LocationPosition) > RestDistance);

        if (far != null)
        {
            return far;
        }

        var living = party.Where(x => x.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false }).ToList();

        for (var i = 0; i < living.Count; i++)
        {
            for (var j = i + 1; j < living.Count; j++)
            {
                if (Distance(living[i].LocationPosition, living[j].LocationPosition) > RestDistance)
                {
                    return Distance(living[i].LocationPosition, user.LocationPosition) >=
                           Distance(living[j].LocationPosition, user.LocationPosition)
                        ? living[i]
                        : living[j];
                }
            }
        }

        return null;
    }

    // after the travel, a party member still out of the long rest range (the game couldn't place them): the message
    // naming them, e.g. "Kushiel is out of range for a long rest.", or null when the
    // whole party is in range
    internal static string DescribeOutOfRange(GameLocationCharacter user)
    {
        var character = FindOutOfRange(user);

        return character == null ? null : Gui.Format("Message/&CampfireCharacterOutOfRange", character.Name);
    }

    // the game routes a fast travel from the first party member (MapWaypointItem.PrepareFastTravel, ExecutePath); when
    // that leader is dead, dying or unconscious the gather doesn't try, and the message names the leader and their
    // state, e.g. "Kushiel is dead. Unable to fast travel.". The message, or null when the leader can lead.
    internal static string DescribeDownedLeader()
    {
        var leader = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters.FirstOrDefault();

        if (leader?.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: true } character)
        {
            return null;
        }

        var key = character.IsDead
            ? "Message/&CampfireLeaderDead"
            : character.IsDying
                ? "Message/&CampfireLeaderDying"
                : "Message/&CampfireLeaderUnconscious";

        return Gui.Format(key, leader.Name);
    }

    // drops the destination; a temporary waypoint leaves the map again
    internal static void RemoveDestination()
    {
        var destination = _destination;

        _destination = null;

        if (destination is not { Temporary: true })
        {
            return;
        }

        var node = destination.Waypoint.worldNode;

        // a location that unloaded took its waypoints and the node with it
        if (destination.GameLocation)
        {
            destination.GameLocation.MapWaypoints.Remove(destination.Waypoint);
        }

        destination.Waypoint.Unbind();

        if (node)
        {
            Object.Destroy(node.gameObject);
        }

        Object.Destroy(destination.Waypoint.Definition);
    }

    private static float Distance(int3 a, int3 b)
    {
        return (a - b).magnitude;
    }

    #endregion

    #region Travel

    // the gather on the computer that started it: the whole party fast travels to the campfire's point, then travelled
    // runs: true only when the game placed the party there and enemies didn't cut the travel short; false when it
    // hadn't begun in time, the game couldn't travel (no path, its waypoint lookup failed) or it was interrupted. False
    // when the game can't fast travel now; the gather then fails, as there is no walk.
    internal static bool TryStart(GameLocationCharacter user, Func<bool> isWanted, Action<bool> travelled)
    {
        if (_destination is not { } destination || destination.User != user || !destination.GameLocation)
        {
            return false;
        }

        var waypoint = destination.Waypoint;
        var mapService = ServiceRepository.GetService<IGameLocationMapService>();
        var leader = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters.FirstOrDefault();

        // the game's own conditions for this fast travel: the map offers the waypoint (shown and revealed), its
        // same-area rule for the party's leader, nothing blocking fast travel now (running functors or actions
        // blocking saves, a battle)
        if (leader == null || !waypoint.Shown || !waypoint.Revealed ||
            !IsInWaypointArea(waypoint, leader) ||
            mapService == null || mapService.IsPreparingFastTravel || mapService.IsFastTraveling ||
            Gui.Battle != null ||
            ServiceRepository.GetService<IFastTravelService>() is not { CanFastTravel: true } ||
            ServiceRepository.GetService<ICommandService>() is not { } commands)
        {
            return false;
        }

        commands.RequestFastTravel(waypoint.Position);

        var driver = new GameObject("FinishingBusinessCampfireFastTravel").AddComponent<TravelDriver>();

        driver.IsWanted = isWanted;
        driver.Travelled = travelled;
        driver.ArrivalsAtStart = _arrivals;
        driver.Target = waypoint.Position;

        return true;
    }

    // MapWaypointDefinition.FastTravelFromSameSectorOnly: the game only offers such a waypoint to a party whose leader
    // is in its sector (MapWaypointItem.Bind)
    private static bool IsInWaypointArea(GameMapWaypoint waypoint, GameLocationCharacter leader)
    {
        if (!waypoint.Definition.FastTravelFromSameSectorOnly)
        {
            return true;
        }

        var accessor = new GridAccessor(waypoint.Position);
        var sector = accessor.sector;

        accessor.FetchSector(leader.LocationPosition);

        return accessor.sector == sector;
    }

    // GameLocationMapManager.SetupPartyOnDestination runs a step: the travel's arrival is under way
    internal static void OnArrivalStep()
    {
        _arriving = true;
    }

    // GameLocationMapManager.SetupPartyOnDestination's end, on every computer: a fast travel placed the party at
    // gridPosition (its waypoint's Position: MapWaypointItem.ExecuteTravel, the only caller of the map manager's
    // FastTravel, passes it, and FastTravel hands it on to this arrival step unchanged). A
    // temporary campfire waypoint has done its job once the travel to it arrived; another travel's arrival leaves it,
    // as the campfire's own travel may still be queued after it (it then goes at the other lockstep points: a long
    // rest, the next campfire, the location unloading).
    internal static void OnFastTravelEnded(int3 gridPosition)
    {
        _arriving = false;
        _arrivals++;
        _lastArrivalInterrupted =
            ServiceRepository.GetService<IGameLocationMapService>() is GameLocationMapManager { interrupted: true };
        _lastArrivalTarget = gridPosition;

        if (_destination is { Temporary: true } destination && _lastArrivalTarget == destination.Waypoint.Position)
        {
            RemoveDestination();
        }
    }

    // the gather ended without a rest: in single player the temporary waypoint goes at once. In multiplayer it stays
    // until a lockstep point removes it on every computer (a long rest, the fast travel to it, the next campfire, the
    // location unloading), as a player could still fast travel to it from the map and every computer must still have it
    // then.
    internal static void OnGatherAbandoned()
    {
        if (!Global.IsMultiplayer)
        {
            RemoveDestination();
        }
    }

    // leaving or joining a room, or loading
    internal static void Reset()
    {
        _arriving = false;
        RemoveDestination();
    }

    #endregion

    private sealed class Destination
    {
        internal GameLocation GameLocation;

        // made for this campfire, removed again afterwards
        internal bool Temporary;

        // the character who used the campfire
        internal GameLocationCharacter User;
        internal GameMapWaypoint Waypoint;
    }

    // waits for the campfire fast travel on the computer that asked for it. The travel command runs at a later tick;
    // once the game's travel has begun there is no time limit, and the gather only goes on once the game is back to
    // exploring, so nothing starts while the party is still travelling. It finishes as soon as the arrival step has
    // run to its end (travelled unless enemies interrupted it), or, for a travel that never reaches that step (no
    // route, its waypoint lookup failed), once the game is exploring again with no arrival step running (not
    // travelled). A travel that hasn't begun here after
    // StartTimeoutSeconds is reported as not travelled, and the gather is given up without sending anything more:
    // the command may only be late (a stalled tick), not dropped, so it may still run. It goes away without going on
    // when the gather was dropped meanwhile (another rest screen opened, a reset); a travel already queued then still
    // runs, as an ordinary whole-party fast travel.
    private sealed class TravelDriver : MonoBehaviour
    {
        private const float StartTimeoutSeconds = 20f;

        internal int ArrivalsAtStart;
        internal Func<bool> IsWanted;

        // the campfire waypoint's position: only an arrival there is this gather's
        internal int3 Target;
        internal Action<bool> Travelled;
        private bool _began;
        private float _startTime;

        private void Start()
        {
            _startTime = Time.unscaledTime;
        }

        private void Update()
        {
            if (IsWanted?.Invoke() == false)
            {
                Destroy(gameObject);

                return;
            }

            // an arrival step ran to its end since this travel was asked for
            if (_arrivals > ArrivalsAtStart)
            {
                if (_lastArrivalTarget == Target)
                {
                    Finish(!_lastArrivalInterrupted);

                    return;
                }

                // another fast travel, queued before this one, arrived first (multiplayer): not this gather's. Wait
                // for this one from here, with a new start time limit, as it only starts now. If it never runs (the
                // game dropped it), the limit ends the gather without a rest.
                ArrivalsAtStart = _arrivals;
                _began = false;
                _startTime = Time.unscaledTime;

                return;
            }

            var mapService = ServiceRepository.GetService<IGameLocationMapService>();
            var travelling = _arriving || mapService is { IsPreparingFastTravel: true } or { IsFastTraveling: true };

            if (!_began)
            {
                _began = travelling;

                if (!_began && Time.unscaledTime - _startTime >= StartTimeoutSeconds)
                {
                    Finish(false);
                }

                return;
            }

            // the travel ended without reaching its arrival step
            if (!travelling && IsExploring())
            {
                Finish(false);
            }
        }

        private void Finish(bool travelled)
        {
            Destroy(gameObject);
            Travelled?.Invoke(travelled);
        }

        private static bool IsExploring()
        {
            return ServiceRepository.GetService<IGameLocationService>()?.GameLocation?.FiniteStateMachine
                ?.CurrentState is LocationState_Exploration;
        }
    }
}
