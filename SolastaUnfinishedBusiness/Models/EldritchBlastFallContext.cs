using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     Eldritch Blast with Repelling Blast or Grasp of Hadar: a creature moved over a drop only falls once the
///     cast's last push or pull on it is over, from the cell it is then on (issue #37; user, 2026-11-11: "make this
///     fall handling a toggleable option as well (that is set to on). If it is off, the default behaviour should
///     occur"). Gameplay &gt; Rules, <see cref="Settings.DeferFallDuringEldritchBlastMoves" />.
///     <para>
///         What the game does. Every move that ends (GameLocationPositioningManager.FinalizeNewPosition) calls
///         GameLocationCharacter.CheckCharacterFooting, which starts a fall at once if the creature can't stay on
///         its cell. Starting a fall also stops the creature's actions and drops the ones queued for it. So a
///         creature pushed over a ledge by the first beam fell there and then: the next beam's push never ran, or
///         (with the prompt of issue #37, which waits for the creature to come to rest) ran from the bottom of the
///         drop, and a pull that would have brought it back onto the ledge came too late.
///     </para>
///     <para>
///         What this changes, and nothing else: while more of the cast's moves are still to come, for that
///         creature or for another one the cast's other beams hit, its footing check is put off. The last move's own check runs as the game makes it. If the last
///         move doesn't happen (the caster chose no distance, the way was blocked, the cast was cut short), the
///         check that was put off is made as soon as the creature is at rest. Whether it then falls, how far, the
///         damage and any reaction to the fall are the game's, from where the creature really is. Areas crossed,
///         prone, death and everything else keep their usual timing.
///     </para>
///     <para>
///         The pose (user, 2026-11-11: "the creature should visibly move through each push and pull without
///         entering the falling pose or waiting for it to end. Only play the fall at the end of the Eldritch
///         Blast"). The game puts a pushed creature in its falling pose for the move and, on ground it can
///         stand on, has it get up again after every single push or pull, waiting for that to finish. With the
///         setting on, a push of the cast doesn't enter that pose (<see cref="SetFallingPose" />) and so has
///         no getting up to wait for (<see cref="IsFallingOrLeftInAir" />): the creature travels as it stands.
///         A real fall at the end starts its own falling pose, as every fall does. With the setting off no
///         creature is held, so each push and pull plays the game's pose and recovery as before.
///     </para>
///     <para>
///         Multiplayer. Everything is decided from the game's own state (which queued moves have run, which push
///         actions are queued or running, whether the creature is acting) inside the game's action update, which
///         in multiplayer only runs on the shared fixed steps. The setting is the host's, like every setting.
///         Not checked on two computers.
///     </para>
/// </summary>
internal static class EldritchBlastFallContext
{
    // "[ForcedMove]" lines from here per launch (Logging > "Forced movement through areas")
    private const int MaxLines = 100;

    // The creatures whose footing check is being put off. A creature is added when one of the cast's moves is about
    // to be applied to it and taken out by AfterActionUpdate once nothing more is to come and it is at rest.
    private static readonly Dictionary<GameLocationCharacter, Hold> Holds = [];

    // the hold of the creature a tagged spend-power action is applying its effect to right now (EnterLeg to ExitLeg)
    private static Hold _applying;

    private static int _lines;

    private static bool IsLogOn =>
        Main.Settings is { EnableAdditionalLogging: true, LogForcedMovement: true } && _lines <= MaxLines;

    /// <summary>
    ///     Called from the spend-power action right before it applies its effect forms to a target (after
    ///     <see cref="EldritchBlastMoveContext.BeforeMove" />). If the action is one of an Eldritch Blast cast's
    ///     queued moves, the target is held from here on, this move no longer counts as "to come", and a push the
    ///     effect queues before <see cref="ExitLeg" /> is noted as this cast's.
    /// </summary>
    internal static void EnterLeg(CharacterActionSpendPower action, GameLocationCharacter target)
    {
        _applying = null;

        // Off: no creature is ever held, so every check below finds nothing and the game runs as it always did.
        if (!Main.Settings.DeferFallDuringEldritchBlastMoves ||
            target == null ||
            !EldritchBlastMoveContext.TryGetCast(action?.ActionParams, out var cast) ||
            ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager manager)
        {
            return;
        }

        // From here this move is under way, not to come. (Both invocations aim at one creature. A power with
        // several targets would count as done from its first target on, which could only end a hold early: the
        // creature would then fall where the game makes it fall anyway.)
        cast.DoneLegs.Add(action.ActionParams);

        if (!Holds.TryGetValue(target, out var hold) || hold.Manager != manager)
        {
            hold = new Hold(target, manager);
            Holds[target] = hold;
        }

        if (!hold.Casts.Contains(cast))
        {
            hold.Casts.Add(cast);
        }

        _applying = hold;
    }

    internal static void ExitLeg()
    {
        _applying = null;
    }

    /// <summary>
    ///     Called when anything is handed to the action service. A push action for the held creature, queued while
    ///     one of the cast's moves applies its effect, is that move's push.
    /// </summary>
    internal static void PushQueued(CharacterActionParams actionParams)
    {
        if (_applying == null ||
            actionParams?.ActingCharacter != _applying.Target ||
            actionParams.ActionDefinition?.Id is not
                (ActionDefinitions.Id.Pushed or (ActionDefinitions.Id)ExtraActionId.PushedCustom))
        {
            return;
        }

        _applying.Pushes.Add(actionParams);
    }

    /// <summary>Called when a push action starts running.</summary>
    internal static void PushStarting(CharacterAction action)
    {
        if (Holds.Count == 0 ||
            action?.ActingCharacter is not { } character ||
            !Holds.TryGetValue(character, out var hold))
        {
            return;
        }

        var index = hold.Pushes.IndexOf(action.ActionParams);

        if (index < 0)
        {
            return;
        }

        // A creature's queued actions run in the order they were queued. So a push queued before this one that is
        // still listed has ended without telling (it was stopped part way) or was dropped without ever running.
        hold.Pushes.RemoveRange(0, index);
        hold.Running = action.ActionParams;

        // what is drawn for the creature, for SetFallingPose (looked up once per hold)
        if (!hold.Graphics &&
            ServiceRepository.GetService<IWorldLocationEntityFactoryService>() is { } factory &&
            factory.TryFindWorldCharacter(character, out var worldCharacter) &&
            worldCharacter)
        {
            hold.Graphics = worldCharacter.graphicsCharacter;
        }
    }

    /// <summary>
    ///     Stands in for the game's "enter the falling pose" at the start of a forced move
    ///     (WorldLocationCharacter.AnimateMove). For a push of a held creature's cast the pose is not entered;
    ///     anything else is passed on to the game unchanged. A creature that is really falling is never touched
    ///     here: its fall sets the pose itself, elsewhere (AnimateFall).
    /// </summary>
    internal static void SetFallingPose(GraphicsCharacter graphics, bool value)
    {
        if (value && Holds.Count > 0 && graphics)
        {
            foreach (var hold in Holds.Values)
            {
                if (hold.Running != null && hold.Graphics == graphics && !hold.Target.Falling)
                {
                    return;
                }
            }
        }

        graphics.IsFalling = value;
    }

    /// <summary>Called when a push action has run to its end.</summary>
    internal static void PushEnded(CharacterAction action)
    {
        if (Holds.Count == 0 ||
            action?.ActingCharacter is not { } character ||
            !Holds.TryGetValue(character, out var hold))
        {
            return;
        }

        hold.Pushes.Remove(action.ActionParams);

        if (hold.Running == action.ActionParams)
        {
            hold.Running = null;
        }
    }

    /// <summary>
    ///     Prefix of GameLocationCharacter.CheckCharacterFooting, the one place the game decides that a creature
    ///     falls (after a move, when the creature or the ground changes, when a condition ends).
    /// </summary>
    /// <returns>False when the check is put off: the game's method must not run now.</returns>
    internal static bool BeforeFootingCheck(GameLocationCharacter character, bool forceFeatherFall)
    {
        if (Holds.Count == 0 || !Holds.TryGetValue(character, out var hold))
        {
            return true;
        }

        if (!MoreToCome(hold, hold.Running))
        {
            // The check at the end of the cast's last push, or one made when nothing of the cast is left: the
            // game's own, at the game's own moment. Nothing is owed any more.
            hold.Deferred = false;

            return true;
        }

        // Kept for the check made later in its place (see Release). "Pushed" matters to the game's check: a pushed
        // creature needs ground under it even if it could otherwise hold on to a wall.
        if (!hold.Deferred && IsLogOn)
        {
            Log($"{Name(character)} at {Cell(character)}: footing check put off, more moves of the cast to come");
        }

        hold.Deferred = true;
        hold.DeferredWhilePushed |= character.Pushed;
        hold.DeferredFeatherFall |= forceFeatherFall;

        return false;
    }

    /// <summary>
    ///     Stands in for GameLocationCharacter.Falling in the game's push action, at the point where a push that
    ///     has just landed decides whether to wait for the creature's "back on its feet" animation event.
    ///     <para>
    ///         The game only sends that event when the push lands on a cell the creature can stay on. For a
    ///         landing over a drop the creature is left in its falling pose and the game relies on the fall having
    ///         started by then (Falling is set), which skips the wait. With the fall put off, the push would wait
    ///         for an event that never comes, until the game gives up after 10 seconds of real time. So a held
    ///         creature whose check was put off and that can't stay where it is counts as falling here, and here
    ///         only.
    ///     </para>
    /// </summary>
    internal static bool IsFallingOrLeftInAir(GameLocationCharacter character)
    {
        if (character.Falling)
        {
            return true;
        }

        if (Holds.Count == 0 || !Holds.TryGetValue(character, out var hold))
        {
            return false;
        }

        // A push of the cast is running for this creature: it never entered the falling pose (SetFallingPose), so
        // there is no getting up, and no event for it, on any ground.
        if (hold.Running != null)
        {
            return true;
        }

        // Any other push of a held creature (not one of the cast's) played the game's pose. Only a landing over a
        // drop, with the fall put off, needs the wait skipped.
        if (!hold.Deferred)
        {
            return false;
        }

        // the same test, with the same values, as the push made before it started (its "last move" flag)
        return ServiceRepository.GetService<IGameLocationPositioningService>() is { } positioningService &&
               !positioningService.CanCharacterStayAtPosition(character, character.LocationPosition, true, false, false);
    }

    /// <summary>
    ///     Postfix of GameLocationActionManager.UpdateImpl, where the game advances every character's actions. A
    ///     held creature is let go once it is at rest and nothing more of the cast is to come for it, and the check
    ///     that was put off, if one still is, is made then.
    /// </summary>
    internal static void AfterActionUpdate(GameLocationActionManager manager)
    {
        // The game's update does nothing on a frame that fails this test (in multiplayer it only works on the
        // shared fixed steps), and a postfix runs all the same. Acting only on the frames the game acted on keeps
        // the moment a fall starts the same on every computer. The test only reads state.
        if (Holds.Count == 0 || !NetworkingDefinitions.CanUpdate())
        {
            return;
        }

        if (ServiceRepository.GetService<IGameLocationService>() is not { LocationShuttingDown: false })
        {
            // the location is going: nothing is owed to creatures that are about to be destroyed
            Holds.Clear();

            return;
        }

        foreach (var hold in Holds.Values.ToArray())
        {
            var target = hold.Target;

            // From a location that has been left (its action manager is gone), or the body was destroyed: there
            // is nothing to check. Not a fall.
            if (hold.Manager != manager)
            {
                Holds.Remove(target);

                continue;
            }

            if (!target.Valid || target.RulesetCharacter == null)
            {
                Holds.Remove(target);

                continue;
            }

            // still moving, or doing anything at all (a push, a death, a reaction): wait for it
            if (target.MoveInProgress || target.Pushed || IsActing(manager, target))
            {
                continue;
            }

            // At rest with no action running or queued: no push of the cast is left, whatever the list says (a
            // push that was dropped before it ran never reports its end).
            hold.Pushes.Clear();
            hold.Running = null;

            if (MoreToCome(hold, null))
            {
                continue;
            }

            Holds.Remove(target);
            Release(hold);
        }
    }

    // Is another move of the cast still to come? "running" is this creature's push action that is running now, if
    // the question is asked from inside it: it doesn't count as "to come".
    //
    // The whole cast counts, not only the moves aimed at this creature (user, 2026-11-11: a creature's fall waits
    // "until Eldritch Blast has finished", including the moves the remaining beams make on other creatures). So a
    // creature left over a drop by the first beam stays there while a later beam pushes someone else, and its
    // footing is judged after that. This holds whatever took its footing away meanwhile (the user's example: damage
    // from an area breaks its concentration on Fly): the damage and the lost flight happen at once, the fall waits.
    private static bool MoreToCome(Hold hold, CharacterActionParams running)
    {
        if (hold.Pushes.Any(push => push != running))
        {
            return true;
        }

        foreach (var cast in hold.Casts)
        {
            // A cast whose caster has stopped acting is over. Moves of it that never ran (the cast was cut short)
            // no longer count, or the creature would hang in the air for good.
            if (cast.Caster == null || !IsActing(hold.Manager, cast.Caster))
            {
                continue;
            }

            // a queued move of the cast, for any creature, that hasn't applied its effect yet
            if (cast.Legs.Any(leg => !cast.DoneLegs.Contains(leg)))
            {
                return true;
            }
        }

        // A push of the same cast still queued or on its way for another creature. That creature is held too (a hold
        // is made when a move is applied), and its hold shares the cast. Its own last push doesn't wait for this
        // creature in turn: this one has no push left by then and stands still, so the two can't wait on each other.
        foreach (var other in Holds.Values)
        {
            if (other == hold || !other.Casts.Any(hold.Casts.Contains))
            {
                continue;
            }

            if (other.Pushes.Count > 0 || other.Target.MoveInProgress || other.Target.Pushed)
            {
                return true;
            }
        }

        return false;
    }

    // The creature is at rest and the cast has nothing more for it. If a footing check was put off and none has
    // been made since, it is made now: the game's own check on the cell the creature is really on.
    private static void Release(Hold hold)
    {
        if (!hold.Deferred)
        {
            return;
        }

        var target = hold.Target;
        var pushed = target.Pushed;

        if (IsLogOn)
        {
            Log($"{Name(target)} at {Cell(target)}: the cast's moves are over, footing checked now");
        }

        // The check that was put off was made for a creature being pushed, and the game judges that differently
        // (see BeforeFootingCheck). The creature is where a push left it, so it is judged the same way now. The
        // flag is only raised for the length of the check.
        target.Pushed = pushed || hold.DeferredWhilePushed;

        try
        {
            target.CheckCharacterFooting(hold.DeferredFeatherFall);
        }
        finally
        {
            target.Pushed = pushed;
        }

        if (IsLogOn)
        {
            Log($"{Name(target)}: {(target.Falling ? "falls" : "stays where it is")}");
        }
    }

    // Is the character doing anything, or about to? The game's own "is acting" reads the first table only and
    // assumes the character is in it. Actions waiting their turn are counted as well, so a creature between two
    // queued pushes is never taken to be at rest, whether or not the game starts the next one in the same step.
    internal static bool IsActing(GameLocationActionManager manager, GameLocationCharacter character)
    {
        if (manager == null || character == null)
        {
            return false;
        }

        if (manager.actionChainByCharacter != null &&
            manager.actionChainByCharacter.TryGetValue(character, out var slot) &&
            slot != null)
        {
            return true;
        }

        return manager.actionChainQueueByCharacter != null &&
               manager.actionChainQueueByCharacter.TryGetValue(character, out var queue) &&
               queue is { Count: > 0 };
    }

    private static string Name(GameLocationCharacter character)
    {
        return $"{character.Name} ({character.Guid})";
    }

    private static string Cell(GameLocationCharacter character)
    {
        var cell = character.LocationPosition;

        return $"({cell.x},{cell.y},{cell.z})";
    }

    // logging only: never changes what happens in the game
    private static void Log(string message)
    {
        _lines++;

        Main.Info(_lines > MaxLines
            ? $"[ForcedMove] {MaxLines} fall lines this launch; no more"
            : $"[ForcedMove] {message}");
    }

    private sealed class Hold(GameLocationCharacter target, GameLocationActionManager manager)
    {
        internal GameLocationCharacter Target { get; } = target;

        // the action manager of the location the creature is in; a hold never outlives it
        internal GameLocationActionManager Manager { get; } = manager;

        // the casts with queued moves for this creature (one, unless a second cast starts before the first is over)
        internal List<EldritchBlastMoveContext.CastState> Casts { get; } = [];

        // push actions of those casts queued for the creature that haven't ended, in the order they were queued
        internal List<CharacterActionParams> Pushes { get; } = [];

        // the one of them that is running
        internal CharacterActionParams Running { get; set; }

        // what the game draws for the creature (its animator is where the falling pose is switched on)
        internal GraphicsCharacter Graphics { get; set; }

        // a footing check was put off and none has been made since
        internal bool Deferred { get; set; }

        // what the checks that were put off were asked with
        internal bool DeferredWhilePushed { get; set; }
        internal bool DeferredFeatherFall { get; set; }
    }
}
