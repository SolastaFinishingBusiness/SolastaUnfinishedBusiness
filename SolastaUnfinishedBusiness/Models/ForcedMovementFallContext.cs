using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     A creature moved over a drop by the pushes and pulls of one action falls only once that action has no more of
///     them to come, from the cell it is then on. Gameplay &gt; Rules,
///     <see cref="Settings.DeferFallDuringEldritchBlastMoves" /> (the name is kept from when the setting only covered
///     Eldritch Blast, so players' saved choice carries over).
///     <para>
///         History. Issue #37 built this for Eldritch Blast with Repelling Blast and Grasp of Hadar (user,
///         2026-11-11: "make this fall handling a toggleable option as well (that is set to on). If it is off, the
///         default behaviour should occur"). Issue #39 widens it to every action (user, 2026-11-14: "Start the build
///         now", after the recommendations in the issue: one cast, power use or attack with its riders counts as
///         one action; pushes and pulls only, not teleports, swaps or grapple drags; a reaction is its own action).
///     </para>
///     <para>
///         What the game does. Every move that ends (GameLocationPositioningManager.FinalizeNewPosition) calls
///         GameLocationCharacter.CheckCharacterFooting, which starts a fall at once if the creature can't stay on
///         its cell. Starting a fall also stops the creature's actions and drops the ones queued for it. So a
///         creature pushed over a ledge by a first push fell there and then: a second push of the same action never
///         ran, or ran from the bottom of the drop, and a pull that would have brought it back came too late.
///     </para>
///     <para>
///         What this changes, and nothing else: while more of an action's moves can still come, for that creature or
///         for another one the same action moves, its footing check is put off. The last move's own check runs as the
///         game makes it. If the last move doesn't happen (no distance chosen, the way was blocked, the action was
///         cut short), the check that was put off is made as soon as the creature is at rest. Whether it then falls,
///         how far, the damage and any reaction to the fall are the game's, from where the creature really is. Areas
///         crossed, prone, death and everything else keep their usual timing.
///     </para>
///     <para>
///         Which moves belong to one action (issue #39). The game doesn't record it: a push action only names the
///         moved creature and where it goes. So the mod ties them together itself, from the game's own events:
///         <list type="bullet">
///             <item>
///                 Each action that applies effects (a spell, a power, an attack, a shove) can head a
///                 <see cref="Group" />.
///             </item>
///             <item>
///                 A power queued or started while one of these is running for the same character joins its group:
///                 the game's direct on-hit powers when they are queued behind the action that hit
///                 (<see cref="PowerQueued" />), the mod's own on-hit powers such as Weapon Mastery Push when they
///                 start (<see cref="ActionStarting" />). A reaction never does: it is its own action.
///             </item>
///             <item>
///                 An action ends when its Execute ends, or when the game stops it (<see cref="ActionsStopped" />)
///                 or aborts it, since a stopped action's coroutine is reset and never reports its end. One not
///                 started yet counts only while it still waits in its character's chains.
///             </item>
///             <item>
///                 The powers the game queues to run right after an action (its ResultingActions, e.g. Repelling Blast
///                 for each Eldritch Blast beam, Gravity Well on each hit) join that action's group when the game
///                 queues them (<see cref="ResultingActionsQueued" />).
///             </item>
///             <item>
///                 A push or pull queued while an action applies a motion effect (<see cref="EnterMotionForm" />), or
///                 one whose pusher is running an action (Shove), belongs to that action's group
///                 (<see cref="PushQueued" />), and the moved creature is held from then on.
///             </item>
///         </list>
///         A creature is held while any action of its group is running or queued, or a push of the group is queued
///         or running for any creature. The game's own fall checks are only put off, never replaced, and a hold
///         always ends once the creature is at rest and its group has nothing left, so a creature is never left in
///         the air because a move or an action was dropped.
///     </para>
///     <para>
///         The pose (user, 2026-11-11: "the creature should visibly move through each push and pull without
///         entering the falling pose or waiting for it to end. Only play the fall at the end of the Eldritch
///         Blast"). The game puts a pushed creature in its falling pose for the move and, on ground it can
///         stand on, has it get up again after every single push or pull, waiting for that to finish. With the
///         setting on, a push of the action holding the creature doesn't enter that pose
///         (<see cref="SetFallingPose" />) and so has no getting up to wait for (<see cref="IsFallingOrLeftInAir" />):
///         the creature travels as it stands. A push from anything else plays the game's pose as before. A real fall
///         at the end starts its own falling pose, as every fall does. With the setting off no creature is held, so
///         each push and pull plays the game's pose and recovery as before.
///     </para>
///     <para>
///         Multiplayer. Everything is decided from the game's own state and events (actions starting, ending and
///         being queued; which push actions are queued or running; whether a creature is acting), and a creature is
///         let go inside the game's action update, which in multiplayer only runs on the shared fixed steps. The
///         setting is the host's, like every setting. Not checked on two computers.
///     </para>
/// </summary>
internal static class ForcedMovementFallContext
{
    // "[ForcedMove]" lines from here per launch (Logging > "Forced movement through areas")
    private const int MaxLines = 100;

    // The creatures whose footing check is being put off. A creature is added when one of its action's moves is
    // about to be applied to it or is queued for it, and taken out by AfterActionUpdate once nothing more is to come
    // and it is at rest.
    private static readonly Dictionary<GameLocationCharacter, Hold> Holds = [];

    // the hold of the creature a tagged spend-power action is applying its effect to right now (EnterLeg to ExitLeg)
    private static Hold _applying;

    // Issue #39. The group of every action that is in one, by its parameters (an action's parameters are made once
    // and kept by it from being queued to its end, so they name it before the action object exists). Entries of
    // actions that have ended are kept until the end of the game's action update they ended in (see
    // AfterActionUpdate), because the game queues an action's resulting actions right after it ends.
    private static readonly Dictionary<CharacterActionParams, Group> GroupByParams = [];

    // Issue #39. Per character, the effect actions it has started that haven't ended, oldest first. The last one not
    // aborted is the one the character is carrying out right now: reactions and powers run in the middle of another
    // action start after it, so they come later in the list.
    private static readonly Dictionary<GameLocationCharacter, List<CharacterAction>> Running = [];

    // Issue #39. The group of the action applying a motion effect right now (EnterMotionForm to ExitMotionForm).
    private static Group _motionGroup;
    private static int _motionDepth;

    private static int _lines;

    private static bool IsOn => Main.Settings.DeferFallDuringEldritchBlastMoves;

    private static bool IsLogOn =>
        Main.Settings is { EnableAdditionalLogging: true, LogForcedMovement: true } && _lines <= MaxLines;

    #region Eldritch Blast casts (issue #37)

    /// <summary>
    ///     Called from the spend-power action right before it applies its effect forms to a target (after
    ///     <see cref="EldritchBlastMoveContext.BeforeMove" />). If the action is one of an Eldritch Blast cast's
    ///     queued moves, the target is held from here on, this move no longer counts as "to come", and a push the
    ///     effect queues before <see cref="ExitLeg" /> is noted as this cast's. The cast knows its moves in advance
    ///     (one per beam and invocation), which the general bookkeeping of issue #39 can't, so both are kept.
    /// </summary>
    internal static void EnterLeg(CharacterActionSpendPower action, GameLocationCharacter target)
    {
        _applying = null;

        // Off: no creature is ever held, so every check below finds nothing and the game runs as it always did.
        if (!IsOn ||
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

        var hold = GetHold(target, manager);

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

    #endregion

    #region Which action a move belongs to (issue #39)

    /// <summary>
    ///     Called when an action starts (CharacterAction.Execute). Joins a power started in the middle of another
    ///     action of the same character to that action's group, and notes the action as running.
    /// </summary>
    internal static void ActionStarting(CharacterAction action)
    {
        if (!IsOn ||
            !IsEffectAction(action) ||
            action.ActingCharacter is not { } actor ||
            action.ActionParams is not { } actionParams)
        {
            return;
        }

        if (GroupByParams.TryGetValue(actionParams, out var group))
        {
            // queued by the game as one of an action's resulting actions: it is that action's, and has now started
            group.Started(actionParams, action);
        }
        else if (!IsReaction(action) && CurrentEffectAction(actor) is { } parent)
        {
            // Started by its own character while another of its actions is being carried out, and not as a
            // reaction: an on-hit power run at once (the game's direct path, or the mod's ExecuteInstantSingleAction
            // ones). It is part of that action.
            group = GroupOf(parent);
            group.Add(actionParams, action);
            GroupByParams[actionParams] = group;
        }

        if (!Running.TryGetValue(actor, out var running))
        {
            running = [];
            Running[actor] = running;
        }

        running.Add(action);
    }

    /// <summary>
    ///     Called when an action has run to its end (the end of CharacterAction.Execute), and for an action the game
    ///     stopped (<see cref="ActionsStopped" />), whose end is never reported otherwise.
    /// </summary>
    internal static void ActionEnded(CharacterAction action)
    {
        if (action?.ActingCharacter is not { } actor)
        {
            return;
        }

        if (Running.TryGetValue(actor, out var running))
        {
            running.Remove(action);

            if (running.Count == 0)
            {
                Running.Remove(actor);
            }
        }

        if (action.ActionParams != null && GroupByParams.TryGetValue(action.ActionParams, out var group))
        {
            group.Ended(action.ActionParams);
        }
    }

    /// <summary>
    ///     Called right before the game queues an action's resulting actions on its character, once the action has
    ///     ended (GameLocationActionManager.FinishActionExecution). These are part of the action.
    /// </summary>
    internal static void ResultingActionsQueued(GameLocationActionManager manager, CharacterAction action)
    {
        if (!IsOn ||
            action?.ActionParams == null ||
            action.ResultingActions is not { Count: > 0 } resultingActions ||
            action.Aborted)
        {
            return;
        }

        // An aborted chain drops them instead (the game terminates it without queueing them).
        if (manager.actionChainByCharacter != null &&
            action.ActingCharacter != null &&
            manager.actionChainByCharacter.TryGetValue(action.ActingCharacter, out var slot) &&
            slot is { aborted: true })
        {
            return;
        }

        Group group = null;

        foreach (var resultingAction in resultingActions)
        {
            // a reaction is its own action, however it was queued (Codex's review of c98ac8bd)
            if (!IsEffectAction(resultingAction) ||
                IsReaction(resultingAction) ||
                resultingAction.ActionParams is not { } childParams ||
                GroupByParams.ContainsKey(childParams))
            {
                continue;
            }

            group ??= GroupOf(action);
            group.Add(childParams, null);
            GroupByParams[childParams] = group;
        }
    }

    /// <summary>
    ///     The actions in a character's running chain right before the game stops its actions
    ///     (GameLocationActionManager.StopCharacterActionsImpl), for <see cref="ActionsStopped" />.
    /// </summary>
    internal static List<CharacterAction> ActionsBeforeStop(GameLocationActionManager.ActionChainSlot slot)
    {
        if (Running.Count == 0 || slot?.actionQueue is not { Count: > 0 } queue)
        {
            return null;
        }

        var actions = new List<CharacterAction>(queue.Count);

        foreach (var queueSlot in queue)
        {
            if (queueSlot?.action is { } action)
            {
                actions.Add(action);
            }
        }

        return actions;
    }

    /// <summary>
    ///     Right after the game stopped a character's actions. The game takes the stopped actions out of the chain
    ///     and resets their coroutine (TA.Coroutine.Reset) after aborting them, or kills one that refused to be
    ///     aborted. A reset coroutine is never finished or disposed, so the end of Execute, where
    ///     <see cref="ActionEnded" /> is called, never comes for them (Codex's review of c98ac8bd). Every action that
    ///     was in the chain before and isn't any more has ended here.
    /// </summary>
    internal static void ActionsStopped(GameLocationActionManager.ActionChainSlot slot, List<CharacterAction> before)
    {
        if (before == null)
        {
            return;
        }

        foreach (var action in before)
        {
            if (slot?.actionQueue == null || !slot.actionQueue.Any(queueSlot => queueSlot?.action == action))
            {
                ActionEnded(action);
            }
        }
    }

    /// <summary>
    ///     Called when an action is handed to the action service (GameLocationActionManager.ExecuteAction), before
    ///     anything else of it. The game's direct path for an on-hit power queues it behind the action that hit
    ///     (GameLocationBattleManager.PrepareAndExecuteSpendPowerAction calls ExecuteAction with enqueue on), so it
    ///     often starts only once that action has ended. It is tied to that action here, when it is queued (Codex's
    ///     review of c98ac8bd). The mod's own on-hit powers start at once and are tied when they start
    ///     (<see cref="ActionStarting" />).
    /// </summary>
    private static void PowerQueued(CharacterActionParams actionParams)
    {
        if (actionParams.ActionDefinition?.Id != ActionDefinitions.Id.SpendPower ||
            actionParams.IsReactionEffect ||
            actionParams.ActionDefinition.ActionType == ActionDefinitions.ActionType.Reaction ||
            GroupByParams.ContainsKey(actionParams) ||
            CurrentEffectAction(actionParams.ActingCharacter) is not { } parent)
        {
            return;
        }

        var group = GroupOf(parent);

        group.Add(actionParams, null);
        GroupByParams[actionParams] = group;
    }

    /// <summary>
    ///     Called when the game starts applying a motion effect (RulesetImplementationManagerLocation.ApplyMotionForm),
    ///     before the mod's own handling of it. The pushes it queues belong to the action its source is carrying out.
    /// </summary>
    internal static void EnterMotionForm(RulesetCharacter source)
    {
        _motionDepth++;

        if (_motionDepth > 1)
        {
            return;
        }

        _motionGroup = null;

        if (!IsOn ||
            GameLocationCharacter.GetFromActor(source) is not { } character ||
            CurrentEffectAction(character) is not { } action)
        {
            // no action of its source is running (an area applying its effect at a turn's start, say): the game's
            // own timing
            return;
        }

        _motionGroup = GroupOf(action);
    }

    /// <summary>The end of what <see cref="EnterMotionForm" /> started, however the method ended.</summary>
    internal static void ExitMotionForm()
    {
        if (_motionDepth > 0)
        {
            _motionDepth--;
        }

        if (_motionDepth == 0)
        {
            _motionGroup = null;
        }
    }

    // The action the character is carrying out right now, if it is one that applies effects: the latest it started
    // that hasn't ended and wasn't aborted.
    private static CharacterAction CurrentEffectAction(GameLocationCharacter character)
    {
        if (character == null || !Running.TryGetValue(character, out var running))
        {
            return null;
        }

        for (var i = running.Count - 1; i >= 0; i--)
        {
            if (!running[i].Aborted)
            {
                return running[i];
            }
        }

        return null;
    }

    // The group a running or just-ended action heads or belongs to, made if it has none yet.
    private static Group GroupOf(CharacterAction action)
    {
        if (GroupByParams.TryGetValue(action.ActionParams, out var group))
        {
            return group;
        }

        group = new Group();
        group.Add(action.ActionParams, action);

        // An action that has ended can still head a group: its resulting actions come after it. Ended means no
        // longer in Running (ActionEnded takes it out at the end of its Execute). The game's own Executing flag is
        // not used: it is only cleared after the action's end handler, which is where the resulting actions are
        // queued, so it is still set then.
        if (!(action.ActingCharacter != null &&
              Running.TryGetValue(action.ActingCharacter, out var running) &&
              running.Contains(action)))
        {
            group.Ended(action.ActionParams);
        }

        GroupByParams[action.ActionParams] = group;

        return group;
    }

    // Actions that can apply a motion effect, and so can move a creature: spells, powers (CharacterActionMagicEffect
    // covers casting, using and spending them), attacks and shoves. The pushes themselves are the moves, not
    // actions of a group.
    private static bool IsEffectAction(CharacterAction action)
    {
        return action is CharacterActionMagicEffect or CharacterActionAttack or CharacterActionShove;
    }

    private static bool IsReaction(CharacterAction action)
    {
        return action.ActionType == ActionDefinitions.ActionType.Reaction ||
               action.ActionParams is { IsReactionEffect: true };
    }

    #endregion

    #region Pushes

    /// <summary>
    ///     Called when anything is handed to the action service. A push action queued while one of an Eldritch Blast
    ///     cast's moves applies its effect is that move's push; a push queued while an action applies a motion effect,
    ///     or for a pusher that is carrying out an action (Shove), is that action's.
    /// </summary>
    internal static void PushQueued(CharacterActionParams actionParams)
    {
        if (actionParams?.ActingCharacter is not { } target)
        {
            return;
        }

        if (actionParams.ActionDefinition?.Id is not
            (ActionDefinitions.Id.Pushed or (ActionDefinitions.Id)ExtraActionId.PushedCustom))
        {
            // not a push: an on-hit power queued behind the action that hit is part of it
            if (IsOn)
            {
                PowerQueued(actionParams);
            }

            return;
        }

        if (_applying != null && target == _applying.Target && !_applying.Pushes.Contains(actionParams))
        {
            _applying.Pushes.Add(actionParams);
        }

        if (!IsOn || ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager manager)
        {
            return;
        }

        var group = _motionGroup;

        // Shove queues its push itself, from the shove action, with the shover as the push's first target (the
        // game's motion effects name their source there too).
        if (group == null &&
            actionParams.TargetCharacters is { Count: > 0 } targets &&
            CurrentEffectAction(targets[0]) is { } pusherAction)
        {
            group = GroupOf(pusherAction);
        }

        if (group == null)
        {
            return;
        }

        var hold = GetHold(target, manager);

        if (!hold.Groups.Contains(group))
        {
            hold.Groups.Add(group);
        }

        if (!hold.Pushes.Contains(actionParams))
        {
            hold.Pushes.Add(actionParams);
        }
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
            // A push from something else. A push of the action that was running for this creature is over by now
            // (the game stops a creature's actions before it queues a push), even if its end was never reported, so
            // this push must not be taken for it: it plays the game's pose and waits as usual (Codex's review of
            // c98ac8bd).
            hold.Running = null;

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
    ///     (WorldLocationCharacter.AnimateMove). For a push of the action holding the creature the pose is not
    ///     entered; anything else is passed on to the game unchanged. A creature that is really falling is never
    ///     touched here: its fall sets the pose itself, elsewhere (AnimateFall).
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

    #endregion

    #region Falls

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
            // The check at the end of the action's last push, or one made when nothing of the action is left: the
            // game's own, at the game's own moment. Nothing is owed any more.
            hold.Deferred = false;

            return true;
        }

        // Kept for the check made later in its place (see Release). "Pushed" matters to the game's check: a pushed
        // creature needs ground under it even if it could otherwise hold on to a wall.
        if (!hold.Deferred && IsLogOn)
        {
            Log($"{Name(character)} at {Cell(character)}: footing check put off, more moves of the action to come");
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

        // A push of the holding action is running for this creature: it never entered the falling pose
        // (SetFallingPose), so there is no getting up, and no event for it, on any ground.
        if (hold.Running != null)
        {
            return true;
        }

        // Any other push of a held creature (not one of the action's) played the game's pose. Only a landing over a
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
    ///     held creature is let go once it is at rest and nothing more of its action is to come, and the check that
    ///     was put off, if one still is, is made then.
    /// </summary>
    internal static void AfterActionUpdate(GameLocationActionManager manager)
    {
        // The game's update does nothing on a frame that fails this test (in multiplayer it only works on the
        // shared fixed steps), and a postfix runs all the same. Acting only on the frames the game acted on keeps
        // the moment a fall starts the same on every computer. The test only reads state.
        if (!NetworkingDefinitions.CanUpdate())
        {
            return;
        }

        if (ServiceRepository.GetService<IGameLocationService>() is not { LocationShuttingDown: false })
        {
            // the location is going: nothing is owed to creatures that are about to be destroyed, and its actions
            // won't end normally
            Holds.Clear();
            GroupByParams.Clear();
            Running.Clear();

            return;
        }

        ForgetEndedActions(manager);

        if (Holds.Count == 0)
        {
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

            // At rest with no action running or queued: no push of the action is left, whatever the list says (a
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

    // Issue #39. Entries of actions that ended are dropped once the update they ended in is over (by then the game
    // has queued their resulting actions, which keep the group). Entries of queued actions that will never run are
    // dropped when their character has nothing queued or running any more. Characters destroyed in between are
    // dropped too. A hold keeps its groups itself, so dropping an entry never ends a hold.
    private static void ForgetEndedActions(GameLocationActionManager manager)
    {
        if (GroupByParams.Count > 0)
        {
            foreach (var entry in GroupByParams.ToArray())
            {
                var member = entry.Value.Find(entry.Key);

                if (member == null || member.HasEnded || !member.IsLive(manager))
                {
                    GroupByParams.Remove(entry.Key);
                }
            }
        }

        if (Running.Count > 0)
        {
            foreach (var entry in Running.ToArray())
            {
                if (!entry.Key.Valid)
                {
                    Running.Remove(entry.Key);

                    continue;
                }

                // An aborted action applies no more effects, and one the game stopped by resetting its coroutine
                // never reports its end, even for a character that stays (Codex's review of c98ac8bd). Its end is
                // noted here, which also ends it in its group.
                foreach (var action in entry.Value.Where(action => action.Aborted).ToArray())
                {
                    ActionEnded(action);
                }
            }
        }
    }

    // Is another move of the action still to come? "running" is this creature's push action that is running now, if
    // the question is asked from inside it: it doesn't count as "to come".
    //
    // The whole action counts, not only the moves aimed at this creature (user, 2026-11-11: a creature's fall waits
    // "until Eldritch Blast has finished", including the moves the remaining beams make on other creatures). So a
    // creature left over a drop by a first push stays there while a later one moves someone else, and its footing
    // is judged after that. This holds whatever took its footing away meanwhile (the user's example: damage from an
    // area breaks its concentration on Fly): the damage and the lost flight happen at once, the fall waits.
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

        // Issue #39: an action of the group that is running, or queued and not started yet. Its effects can still
        // move this creature or another one.
        if (hold.Groups.Any(group => group.AnyLive(hold.Manager)))
        {
            return true;
        }

        // A push of the same cast or action still queued or on its way for another creature. That creature is held
        // too, and its hold shares the cast or group. Its own last push doesn't wait for this creature in turn: this
        // one has no push left by then and stands still, so the two can't wait on each other.
        foreach (var other in Holds.Values)
        {
            if (other == hold ||
                (!other.Casts.Any(hold.Casts.Contains) && !other.Groups.Any(hold.Groups.Contains)))
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

    // The creature is at rest and the action has nothing more for it. If a footing check was put off and none has
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
            Log($"{Name(target)} at {Cell(target)}: the action's moves are over, footing checked now");
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

    #endregion

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

    private static Hold GetHold(GameLocationCharacter target, GameLocationActionManager manager)
    {
        if (Holds.TryGetValue(target, out var hold) && hold.Manager == manager)
        {
            return hold;
        }

        hold = new Hold(target, manager);
        Holds[target] = hold;

        return hold;
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

    /// <summary>
    ///     Issue #39: one action that applies effects and the actions that are part of it (powers it sets off, run at
    ///     once or queued after it). Pushes are not members: a hold keeps those.
    /// </summary>
    private sealed class Group
    {
        private readonly List<Member> _members = [];

        internal void Add(CharacterActionParams actionParams, CharacterAction action)
        {
            if (Find(actionParams) != null)
            {
                return;
            }

            _members.Add(new Member(actionParams) { Action = action });
        }

        internal void Started(CharacterActionParams actionParams, CharacterAction action)
        {
            if (Find(actionParams) is { } member)
            {
                member.Action = action;
            }
        }

        internal void Ended(CharacterActionParams actionParams)
        {
            if (Find(actionParams) is { } member)
            {
                member.HasEnded = true;
            }
        }

        internal Member Find(CharacterActionParams actionParams)
        {
            foreach (var member in _members)
            {
                if (member.Params == actionParams)
                {
                    return member;
                }
            }

            return null;
        }

        internal bool AnyLive(GameLocationActionManager manager)
        {
            foreach (var member in _members)
            {
                if (member.IsLive(manager))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class Member(CharacterActionParams actionParams)
    {
        internal CharacterActionParams Params { get; } = actionParams;

        // the action object, once it has started (a queued resulting action has none before)
        internal CharacterAction Action { get; set; }

        internal bool HasEnded { get; set; }

        // Can this action still do anything? A started one until it ends, unless it was aborted (an aborted action
        // applies no more effects, whether or not its end is ever reported). One that hasn't started yet while it is
        // still waiting in its character's chains: once it isn't, it was dropped or refused and will never run.
        // (Its character merely doing something else doesn't keep it alive: Codex's review of c98ac8bd.)
        internal bool IsLive(GameLocationActionManager manager)
        {
            if (HasEnded)
            {
                return false;
            }

            if (Action != null)
            {
                return !Action.Aborted;
            }

            return IsWaiting(manager, Params);
        }
    }

    // Is this action, not started yet, still waiting to run? It is either in its character's running chain already
    // (a resulting action, or the chain's next action) or in one of its chains queued after that one.
    private static bool IsWaiting(GameLocationActionManager manager, CharacterActionParams actionParams)
    {
        var character = actionParams.ActingCharacter;

        if (manager == null || character == null)
        {
            return false;
        }

        if (manager.actionChainByCharacter != null &&
            manager.actionChainByCharacter.TryGetValue(character, out var slot) &&
            slot != null)
        {
            if (slot.actionQueue != null &&
                slot.actionQueue.Any(queueSlot => queueSlot?.action?.ActionParams == actionParams))
            {
                return true;
            }

            // The running chain's list keeps every entry it started with. The game moves the chain's index on and
            // then starts the entry at the new index (StartNextActionFromCurrentChain), so only the entries after the
            // index are still to come; the one at it has been started, and is in actionQueue above if it still runs
            // (Codex's re-review of 888a9a08).
            if (slot.actionChainParams?.actionsParams != null &&
                slot.actionChainParams.actionsParams.IndexOf(actionParams) > slot.indexActionChainParams)
            {
                return true;
            }
        }

        return manager.actionChainQueueByCharacter != null &&
               manager.actionChainQueueByCharacter.TryGetValue(character, out var queue) &&
               queue != null &&
               queue.Any(chain => chain?.actionChainParams?.actionsParams != null &&
                                  chain.actionChainParams.actionsParams.Contains(actionParams));
    }

    private sealed class Hold(GameLocationCharacter target, GameLocationActionManager manager)
    {
        internal GameLocationCharacter Target { get; } = target;

        // the action manager of the location the creature is in; a hold never outlives it
        internal GameLocationActionManager Manager { get; } = manager;

        // the Eldritch Blast casts with queued moves for this creature (one, unless a second cast starts before the
        // first is over)
        internal List<EldritchBlastMoveContext.CastState> Casts { get; } = [];

        // issue #39: the actions whose pushes or pulls were queued for this creature
        internal List<Group> Groups { get; } = [];

        // push actions of those casts and actions queued for the creature that haven't ended, in the order they were
        // queued
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
