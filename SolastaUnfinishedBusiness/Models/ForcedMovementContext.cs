using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
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
// What this does. The cells a push goes through are recorded when the push is worked out (Record) and tied to the push
// action made for it (BindToAction, PushStarting). Just before the game changes the creature's cell to the landing cell
// (BeforeMoveCommitted, from FinishMoveTo) the creature is stepped through those cells in order. Each step moves it in the occupant lists, exactly as the game does, and then runs the
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

    // A route goes through three places, so that it can only ever be used by the push it was worked out for (Codex's
    // review of PR #31: one "latest route" per creature let a second push, worked out while the first was still on
    // its way, replace or lose the first one's cells, depending on timing).
    //
    // 1. Solved: worked out inside the ApplyMotionForm call that is running, waiting for that same call to hand its
    //    push to the action service. Emptied when the call ends, so nothing waits longer than that.
    private static readonly Dictionary<GameLocationCharacter, Route> Solved = [];

    // 2. Bound: tied to the parameters of the push action that was queued for it. The action may start much later,
    //    or never; held weakly, so parameters that are dropped take the route with them.
    private static readonly ConditionalWeakTable<CharacterActionParams, Route> Bound = new();

    // 3. Active: the route of the push action that has started for a creature. Every push action that starts sets or
    //    clears it (PushStarting), so a push without a route never inherits an earlier one's. Held weakly as well.
    private static readonly ConditionalWeakTable<GameLocationCharacter, Route> Active = new();

    // The cells the mod's own solver went through in the run that just ended, handed from the patch that ran it to
    // Record (see TakeSolverSteps). Null when the game's solver ran.
    private static List<int3> _solverSteps;

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

        // whatever was worked out in this call and not handed to the action service was never made into a push
        if (_motionFormDepth == 0)
        {
            Solved.Clear();
            _solverSteps = null;
        }
    }

    // For the patch that runs the mod's own solver (MotionContext.ComputePushDestination, used when vertical pushes and
    // pulls are enabled): a list for the solver to put each cell it accepts in, or null when this run isn't recorded.
    // The mod's solver can report its cells itself, which is exact for every distance it supports, including a pull
    // onto the source's own cell (MotionContext.PullOntoCaster, Gravity Fissure), whose last step no plain distance
    // asks for (Codex's review of PR #31).
    internal static List<int3> TakeSolverSteps()
    {
        _solverSteps = _solvingSteps || _motionFormDepth == 0 ? null : [];

        return _solverSteps;
    }

    // Called after every run of the push solver (GameLocationEnvironmentManager.ComputePushDestination with a world
    // position as its source, which the other two forms call; either the game's solver or the mod's vertical one).
    //
    // The mod's solver hands over the cells it went through (TakeSolverSteps). The game's solver only reports where it
    // ended. It walks cell by cell, stops at the first cell the creature can't go to, and decides each step from the
    // steps before it alone, so the cell after k steps is what it returns when asked for a distance of k. Asking for
    // 1, 2, ... until the answer is the real destination gives the cells in between without copying the solver here.
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

        // the mod's solver, if it ran, left its cells here; taken at once so they can't be used for another run
        var solverSteps = _solverSteps;

        _solverSteps = null;

        // a new push for this creature in the same call replaces the one before it, also when it turns out to have
        // no cells in between
        Solved.Remove(target);

        var start = target.LocationPosition;

        if (!result || destination == start)
        {
            return;
        }

        if (solverSteps != null)
        {
            // every accepted cell in order, the last being the destination (alone, for a push of one cell)
            if (solverSteps.Count > 0 && solverSteps[solverSteps.Count - 1] == destination)
            {
                solverSteps.RemoveAt(solverSteps.Count - 1);
                Keep(target, start, destination, solverSteps, distance, reverse);
            }

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

                // No progress: the shorter push ends where an earlier one did without reaching the destination. Not
                // expected from the game's solver; such a push would keep the old behaviour, the landing cell only.
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

        // A push of one cell has nothing in between and is kept all the same: by the time it runs the creature may
        // stand somewhere else, and the move may be longer (see BeforeMoveCommitted).
        if (!complete)
        {
            return;
        }

        Keep(target, start, destination, cells, distance, reverse);
    }

    private static void Keep(
        GameLocationCharacter target, int3 start, int3 destination, List<int3> cells, int distance, bool reverse)
    {
        Solved[target] = new Route(start, destination, cells);

        if (IsLogOn)
        {
            Log($"{Name(target)} {(reverse ? "pull" : "push")} {distance} recorded " +
                $"{Cell(start)} -> {Cell(destination)} through {string.Join(" ", cells.Select(Cell))}");
        }
    }

    // Called when anything is handed to the action service (GameLocationActionManager.ExecuteAction). Inside
    // ApplyMotionForm, a push action for a creature whose push was just worked out, going to that push's destination,
    // is the push the route belongs to: the game and the mod both work a push out and queue it in one go.
    internal static void BindToAction(CharacterActionParams actionParams)
    {
        if (_motionFormDepth == 0 ||
            actionParams?.ActingCharacter is not { } character ||
            actionParams.ActionDefinition?.Id is not
                (ActionDefinitions.Id.Pushed or (ActionDefinitions.Id)ExtraActionId.PushedCustom) ||
            !Solved.TryGetValue(character, out var route))
        {
            return;
        }

        Solved.Remove(character);

        if (actionParams.Positions.Count > 0 && actionParams.Positions[0] == route.Destination)
        {
            Bound.Remove(actionParams);
            Bound.Add(actionParams, route);
        }
    }

    // Called when a push action starts running (CharacterAction.Execute). From here to the end of this push, its route
    // is the creature's, and only its: a push with no route clears what an earlier, interrupted push may have left.
    internal static void PushStarting(CharacterAction action)
    {
        if (action?.ActingCharacter is not { } character)
        {
            return;
        }

        Active.Remove(character);

        if (action.ActionParams != null && Bound.TryGetValue(action.ActionParams, out var route))
        {
            Bound.Remove(action.ActionParams);
            Active.Add(character, route);
        }
    }

    // Called when a push action has run to its end: nothing of it is left to commit.
    internal static void PushEnded(GameLocationCharacter character)
    {
        if (character != null)
        {
            Active.Remove(character);
        }
    }

    // Called at the start of GameLocationCharacter.FinishMoveTo, which is where a move changes the creature's cell.
    // If this is the push whose route is active for the creature, step it through the cells in between first.
    //
    // Returns false when the game's FinishMoveTo must not run: an area's effect moved the creature somewhere else
    // while it was being stepped through, so the push's landing cell is no longer where it goes.
    internal static bool BeforeMoveCommitted(GameLocationCharacter character, int3 destination)
    {
        if (character == null || !Active.TryGetValue(character, out var route))
        {
            return true;
        }

        // Not a push committing (the creature walks or is teleported while a route is still active, which only an
        // interrupted push can leave behind): not this route's move.
        if (!character.Pushed)
        {
            return true;
        }

        // used once, whatever happens below
        Active.Remove(character);

        // Not the move that was recorded: it goes somewhere else. Do nothing and let the game handle the landing cell
        // as before.
        if (route.Destination != destination)
        {
            return true;
        }

        // Where the creature really is now. Usually where it stood when the push was worked out, and then the
        // solver's cells are the ones it goes through.
        //
        // Not always (Codex's second review of PR #31): the game works a push out when it is made but runs it later,
        // from wherever the creature is by then, to the destination it worked out. A creature pushed two cells away
        // and then pulled by a pull worked out before that push ran (Repelling Blast and Grasp of Hadar on one
        // cast) is pulled from its new cell, across four cells instead of two. The game moves it there in one
        // straight move, so the cells it goes through are those on the straight line. No solver checked them, as
        // none checked the move the game makes.
        var start = character.LocationPosition;
        var cells = start == route.Start ? route.Cells : StraightLine(start, destination);

        if (cells.Count == 0)
        {
            return true;
        }

        var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioningService == null ||
            ServiceRepository.GetService<IGameLocationEnvironmentService>()
                is not GameLocationEnvironmentManager environmentManager)
        {
            return true;
        }

        if (IsLogOn && start != route.Start)
        {
            Log($"{Name(character)} starts from {Cell(start)}, not {Cell(route.Start)} as worked out; " +
                $"straight to {Cell(destination)} through {string.Join(" ", cells.Select(Cell))}");
        }

        var current = start;
        bool displaced;

        try
        {
            displaced = StepThrough(character, cells, positioningService, environmentManager, ref current);
        }
        catch (Exception exception)
        {
            // An area's effect threw. Whatever it applied stays applied and is not tried again; the push itself
            // must still end properly, so the error is logged here and the move is committed below as usual. Left
            // to travel up, it would stop FinishMoveTo with the creature registered on a cell in between.
            Main.Error($"[ForcedMove] {Name(character)} at {Cell(current)}: {exception}");

            displaced = character.LocationPosition != current;
        }

        if (displaced)
        {
            // The creature is where the effect put it, fully moved there by the game (a teleport runs its own
            // FinishMoveTo, which also ends this move step). Committing the push's landing cell now would drag it
            // back out, so the game's FinishMoveTo is skipped (Codex's review of PR #31).
            if (IsLogOn)
            {
                Log($"{Name(character)} was moved to {Cell(character.LocationPosition)} by an area; " +
                    $"the push to {Cell(destination)} is dropped");
            }

            return false;
        }

        // Put the creature back on the cell it started from, in the occupant lists only. FinishMoveTo then makes the
        // real move from start to landing cell exactly as before this change: one Moved event with the true start,
        // one FinalizeNewPosition for the landing cell, and the areas there worked out from what the steps left.
        if (current != start)
        {
            MoveOccupant(positioningService, character, current, start);
        }

        return true;
    }

    // The cells strictly between two cells on the straight line from one to the other: as many steps as the longest
    // axis is long, each axis advancing in proportion. Whole numbers only (halves round away from zero), so every
    // computer gets the same cells.
    private static List<int3> StraightLine(int3 from, int3 to)
    {
        var cells = new List<int3>();
        var delta = to - from;
        var steps = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y), Mathf.Abs(delta.z));

        for (var step = 1; step < steps; step++)
        {
            cells.Add(from + new int3(
                Part(delta.x, step, steps), Part(delta.y, step, steps), Part(delta.z, step, steps)));
        }

        return cells;

        // length * step / steps, rounded to the nearest whole number
        static int Part(int length, int step, int steps)
        {
            return ((2 * length * step) + (length < 0 ? -steps : steps)) / (2 * steps);
        }
    }

    // Steps the creature through the given cells. Returns true if an area's effect moved the creature itself.
    // "current" is kept up to date for the caller, also when this throws.
    private static bool StepThrough(
        GameLocationCharacter character,
        List<int3> cells,
        IGameLocationPositioningService positioningService,
        GameLocationEnvironmentManager environmentManager,
        ref int3 current)
    {
        foreach (var cell in cells)
        {
            // A creature that died or dropped on an earlier cell is not affected by the cells after it. Only the
            // steps it completed count. Its body still ends on the landing cell: the push is one animation, already
            // played to the end, so the body is drawn there, and committing an earlier cell would leave the game
            // and the screen disagreeing about where it lies. A push also doesn't stop for a creature that drops.
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
            // where it is: stop.
            if (character.LocationPosition != cell)
            {
                return true;
            }
        }

        return false;
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
