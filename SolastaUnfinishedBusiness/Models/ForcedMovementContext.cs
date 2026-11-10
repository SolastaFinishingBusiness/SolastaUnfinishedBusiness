using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Forced movement through areas (issue #29, task 4; user handoff 2026-11-10).
//
// What was wrong. A push or pull (Repelling Blast, Grasp of Hadar, Thorn Whip, Thunderwave, ...) is one move: the game's
// CharacterActionPushed starts one animation and, when it ends, StopMoving -> FinishMoveTo changes the creature's cell
// once, from where it started to where it lands. Areas such as Spike Growth or Spirit Guardians are worked out by
// GameLocationEnvironmentManager.OccupationChanged, which runs after each change of cell and finds the creatures of an
// area through the map's occupant lists. So only the landing cell ever counted: a creature pushed two cells into Spike
// Growth took one lot of damage instead of two, and one pushed right through an area to a clear cell took none.
//
// What this does. The cells a push goes through are recorded when the push is worked out (Record), and just before the
// game changes the creature's cell to the landing cell (BeforeMoveCommitted, from FinishMoveTo) the creature is stepped
// through those cells in order. Each step moves it in the occupant lists, exactly as the game does, and then runs the
// game's own OccupationChanged for areas. The game's rules therefore decide everything, the same as for walking:
// - entering an area applies its "on enter" effect once;
// - each further cell inside applies its "on move" effect (Spike Growth, Mantle of Thorns);
// - leaving removes the creature from the area, with no damage, so a later entry counts as a new entry;
// - target restrictions, saving throws, resistances and immunities are the area's own.
// The landing cell is not handled here: the game's FinishMoveTo does it right after, as it always did, and it sees what
// the steps left behind (a creature already inside an area gets "on move", not a second "on enter").
//
// What it leaves alone, on purpose:
// - Nothing but areas runs for the cells in between. The rest of what a real change of cell does
//   (FinalizeNewPosition: pressure plates and trap volumes, falling, sight, the AI, the auras the creature carries) is
//   skipped for those cells, see SkipFinalizeNewPosition, and still happens once for the landing cell.
// - The animation, the push action and what follows it (grapple checks, Cauterizing Flames) are unchanged: this is not a
//   second action and adds no waiting.
// - Teleports, swaps and the wall push have no cells in between. A grapple drag already moves cell by cell.
//
// Multiplayer. Everything here follows from the game state alone: the cells come from the push solver at the moment the
// push is made, on every computer alike, and never from aiming previews, which only the aiming player's computer runs
// (see Record). The extra saving throws and damage rolls happen at the same point of the same action on every computer.
// Not yet checked on two computers.
internal static class ForcedMovementContext
{
    // a push never goes this far; only stops the search for cells if a solver should misbehave
    private const int MaxSteps = 60;

    // "[ForcedMove]" lines per launch (Logging > "Forced movement through areas")
    private const int MaxLines = 300;

    // The cells of the latest push worked out for each creature. Held weakly, so a creature that leaves the game takes
    // its entry with it. An entry is only used if the push that commits starts and ends where it says (see
    // BeforeMoveCommitted), and is replaced whenever a new push is worked out for the creature.
    private static readonly ConditionalWeakTable<GameLocationCharacter, Route> Routes = new();

    // how many ApplyMotionForm calls are running (they don't nest in practice; a count is simply safe)
    private static int _motionFormDepth;

    // true while Record asks the solver for shorter pushes, so those calls aren't recorded themselves
    private static bool _solvingSteps;

    private static int _lines;

    // True only while MoveOccupant changes the creature's cell for a step in between. The patch on
    // GameLocationPositioningManager.FinalizeNewPosition then does nothing, so the step updates the occupant lists and
    // the creature's cell and nothing else.
    internal static bool SkipFinalizeNewPosition { get; private set; }

    private static bool IsLogOn =>
        Main.Settings is { EnableAdditionalLogging: true, LogForcedMovement: true } && _lines <= MaxLines;

    // ApplyMotionForm (the game applying a push, pull or other motion to a creature) is starting or has ended. Only
    // pushes worked out inside it are recorded.
    internal static void EnterMotionForm()
    {
        _motionFormDepth++;
    }

    internal static void ExitMotionForm()
    {
        if (_motionFormDepth > 0)
        {
            _motionFormDepth--;
        }
    }

    // Called after every run of the push solver (GameLocationEnvironmentManager.ComputePushDestination with a world
    // position as its source, which the other two forms call; either the game's solver or the mod's vertical one).
    //
    // The solver walks cell by cell and stops at the first cell the creature can't go to, but only reports where it
    // ended. Both solvers decide each step from the steps before it alone, so the cell after k steps is what the same
    // solver returns when asked for a distance of k. Asking for 1, 2, ... until the answer is the real destination
    // gives the cells in between, including any slide along a wall, without copying either solver here.
    //
    // Only inside ApplyMotionForm: the solver also runs for aiming previews and for the AI's shove scoring, on one
    // computer only and for pushes that may never happen. Recording those could give two computers different cells.
    internal static void Record(
        bool result,
        Vector3 sourceCenter,
        GameLocationCharacter target,
        int distance,
        bool reverse,
        IGameLocationPositioningService positioningService,
        int3 destination)
    {
        if (_solvingSteps || _motionFormDepth == 0 || target == null)
        {
            return;
        }

        // a new push for this creature replaces whatever was recorded for it, also when it turns out to have no
        // cells in between
        Routes.Remove(target);

        var start = target.LocationPosition;

        if (!result || destination == start)
        {
            return;
        }

        var environmentService = ServiceRepository.GetService<IGameLocationEnvironmentService>();

        if (environmentService == null || positioningService == null)
        {
            return;
        }

        var cells = new List<int3>();
        var complete = false;

        _solvingSteps = true;

        try
        {
            for (var steps = 1; steps <= MaxSteps; steps++)
            {
                if (!environmentService.ComputePushDestination(
                        sourceCenter, target, steps, reverse, positioningService, out var cell, out _))
                {
                    break;
                }

                if (cell == destination)
                {
                    complete = true;

                    break;
                }

                // No progress: the shorter push ends where an earlier one did without reaching the destination. That
                // happens for a pull onto the caster's own cell (MotionContext.PullOntoCaster), whose last step no
                // plain distance asks for. Such a push keeps the old behaviour: the landing cell only.
                if (cell == start || cells.Contains(cell))
                {
                    break;
                }

                cells.Add(cell);
            }
        }
        finally
        {
            _solvingSteps = false;
        }

        // a push of one cell has nothing in between
        if (!complete || cells.Count == 0)
        {
            return;
        }

        Routes.Add(target, new Route(start, destination, cells));

        if (IsLogOn)
        {
            Log($"{Name(target)} {(reverse ? "pull" : "push")} {distance} recorded " +
                $"{Cell(start)} -> {Cell(destination)} through {string.Join(" ", cells.Select(Cell))}");
        }
    }

    // Called at the start of GameLocationCharacter.FinishMoveTo, which is where a move changes the creature's cell.
    // If this is the push recorded for the creature, step it through the cells in between first.
    internal static void BeforeMoveCommitted(GameLocationCharacter character, int3 destination)
    {
        if (character == null || !Routes.TryGetValue(character, out var route))
        {
            return;
        }

        // Walking, teleports and everything else that isn't a push: leave the entry for the push it belongs to. (A
        // push that is dropped before it moves, as queued pushes on one creature can be, also leaves it; it is
        // replaced by the next push worked out for the creature, and is only ever used under the checks below.)
        if (!character.Pushed)
        {
            return;
        }

        Routes.Remove(character);

        // Not the push that was recorded: the creature has moved since, or this push goes somewhere else. The cells
        // would be wrong, so do nothing and let the game handle the landing cell as before.
        if (route.Start != character.LocationPosition || route.Destination != destination)
        {
            return;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null ||
            ServiceRepository.GetService<IGameLocationEnvironmentService>()
                is not GameLocationEnvironmentManager environmentManager)
        {
            return;
        }

        var current = route.Start;
        var displaced = false;

        foreach (var cell in route.Cells)
        {
            // A creature that died or dropped on an earlier cell is not affected by the cells after it. Only the
            // steps it completed count. (Its body still ends on the landing cell, where the animation takes it.)
            if (!CanBeAffected(character))
            {
                if (IsLogOn)
                {
                    Log($"{Name(character)} stopped being affected at {Cell(current)}; later cells skipped");
                }

                break;
            }

            var hitPoints = character.RulesetCharacter.CurrentHitPoints;

            MoveOccupant(positioningService, character, current, cell);
            current = cell;

            // The game's own handler for "a creature changed cell": adds the creature to the areas it is now in
            // (applying "on enter"), applies "on move" for the areas it was already in, and removes it from the
            // areas it left. The creature counts as moving because Pushed is set for the whole push.
            environmentManager.OccupationChanged(character);

            if (IsLogOn)
            {
                Log($"{Name(character)} at {Cell(cell)}: areas [{Areas(character)}], " +
                    $"hit points {hitPoints} -> {character.RulesetCharacter?.CurrentHitPoints}");
            }

            // An area's effect moved the creature itself (a teleport, say). The recorded cells no longer describe
            // where it is: stop, and don't put it back.
            if (character.LocationPosition != cell)
            {
                displaced = true;

                break;
            }
        }

        // Put the creature back on the cell it started from, in the occupant lists only. FinishMoveTo then makes the
        // real move from start to landing cell exactly as before this change: one Moved event with the true start,
        // one FinalizeNewPosition for the landing cell, and the areas there worked out from what the steps left.
        if (!displaced && current != route.Start)
        {
            MoveOccupant(positioningService, character, current, route.Start);
        }
    }

    // Changes the creature's cell in the map's occupant lists (which areas use to find creatures) and on the creature
    // itself, through the game's own CharacterMoved, with the rest of a change of cell switched off.
    private static void MoveOccupant(
        IGameLocationPositioningService positioningService,
        GameLocationCharacter character,
        int3 from,
        int3 to)
    {
        SkipFinalizeNewPosition = true;

        try
        {
            positioningService.CharacterMoved(character, from, to);
        }
        finally
        {
            SkipFinalizeNewPosition = false;
        }
    }

    // the same test the game's push makes before it moves a creature
    private static bool CanBeAffected(GameLocationCharacter character)
    {
        return GameLocationCharacter.IsValidCharacter(character) &&
               character.RulesetCharacter is { IsDeadOrDyingOrUnconscious: false };
    }

    private static string Name(GameLocationCharacter character)
    {
        return $"{character.Name} ({character.Guid})";
    }

    private static string Cell(int3 cell)
    {
        return $"({cell.x},{cell.y},{cell.z})";
    }

    private static string Areas(GameLocationCharacter character)
    {
        return string.Join(", ",
            character.AffectingGlobalEffects.Select(effect => effect.SourceDefinition?.Name ?? effect.GetType().Name));
    }

    // logging only: never changes what happens in the game
    private static void Log(string message)
    {
        _lines++;

        Main.Info(_lines > MaxLines
            ? $"[ForcedMove] {MaxLines} lines this launch; no more"
            : $"[ForcedMove] {message}");
    }

    private sealed class Route(int3 start, int3 destination, List<int3> cells)
    {
        internal int3 Start { get; } = start;
        internal int3 Destination { get; } = destination;
        internal List<int3> Cells { get; } = cells;
    }
}
