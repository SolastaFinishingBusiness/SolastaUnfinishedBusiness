using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Subclasses.Builders;
using static ActionDefinitions;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     Eldritch Blast's two movement invocations, Repelling Blast (a push) and Grasp of Hadar (a pull): the third
///     toggle state "Ask", and the prompt that lets the caster choose, for each beam that hits, how far each one moves
///     the creature and which acts first (issue #37).
///     <para>
///         How a cast runs. Each beam that hits makes the game queue one "spend power" action per active invocation on
///         the casting action (<see cref="TagHit" /> notes which queued actions belong to which beam, and puts the
///         push before the pull). After every beam is rolled the queued actions run one by one. Just before one
///         applies its move, <see cref="BeforeMove" /> waits until the creature has stopped moving, asks the caster if
///         that beam hasn't been decided yet, and gives that action its own, changed copy of the move.
///     </para>
///     <para>
///         Multiplayer. Everything here runs on every computer and must do the same thing on each: whether to ask
///         depends only on shared game state, one request is raised at a time, and the answer is read from the
///         request after the game has delivered it to every computer (<see cref="ReactionRequestEldritchBlastMove" />).
///     </para>
/// </summary>
internal static class EldritchBlastMoveContext
{
    /// <summary>The push or pull distance of both invocations, in cells (10 feet).</summary>
    internal const int MaxCells = 2;

    // Kept in the character's saved list of toggled names (RulesetCharacter.ToggledPowersOn), where the mod already
    // keeps its own toggles. An invocation is "Ask" when it is active and its marker is in that list. A save made
    // with a marker loads as plain "On" without the mod, and an old save has no marker.
    private const string AskMarkerPrefix = "FbAskInvocation:";

    // After this many frames of waiting for a creature to stop moving, one line is logged. The wait itself goes on:
    // going ahead on a frame count would let computers with different frame rates act at different moments.
    private const int SlowQuietFrames = 1800;

    private static readonly ConditionalWeakTable<CharacterAction, CastState> Casts = new();
    private static readonly ConditionalWeakTable<CharacterActionParams, Leg> Legs = new();

    private static string RepellingPowerName => DatabaseHelper.FeatureDefinitionPowers.PowerInvocationRepellingBlast.Name;

    private static string GraspPowerName => InvocationsBuilders.GraspingBlast.GrantedFeature.Name;

    #region The Ask state

    /// <summary>True for the two invocations that have the third state.</summary>
    internal static bool Supports(InvocationDefinition invocation)
    {
        return invocation &&
               (invocation == DatabaseHelper.InvocationDefinitions.RepellingBlast ||
                invocation == InvocationsBuilders.GraspingBlast);
    }

    private static string MarkerOf(InvocationDefinition invocation)
    {
        return AskMarkerPrefix + invocation.Name;
    }

    /// <summary>True when the character has this invocation switched to Ask (which also means it is active).</summary>
    internal static bool IsAsk(RulesetCharacter character, InvocationDefinition invocation)
    {
        return character != null &&
               Supports(invocation) &&
               character.ToggledPowersOn.Contains(MarkerOf(invocation)) &&
               character.HasActiveInvocation(invocation);
    }

    /// <summary>
    ///     One click on the invocation in the Invocations popup: Off, then On, then Ask, then Off again. Called from the
    ///     game's toggle command, which runs on every computer on the same tick, so each computer works the next state
    ///     out from the same state.
    /// </summary>
    /// <returns>
    ///     True when the click was fully handled here (On to Ask, which leaves the game's own on/off value alone);
    ///     false when the caller should flip the game's on/off value as usual.
    /// </returns>
    internal static bool CycleState(RulesetCharacter character, RulesetInvocation invocation)
    {
        var definition = invocation.InvocationDefinition;

        if (!Supports(definition))
        {
            return false;
        }

        var marker = MarkerOf(definition);
        var toggles = character.ToggledPowersOn;

        if (!invocation.Active)
        {
            // Off to On. A marker left behind by something else that switched the invocation off is dropped.
            toggles.Remove(marker);

            return false;
        }

        if (toggles.Contains(marker))
        {
            // Ask to Off.
            toggles.Remove(marker);

            return false;
        }

        // On to Ask. The game's value doesn't change, so its "state changed" event is raised here to redraw the popup.
        toggles.Add(marker);
        invocation.InvocationActivationStateChanged?.Invoke(invocation, true);

        return true;
    }

    #endregion

    #region Which queued action belongs to which beam

    /// <summary>What one beam's moves are to be, as chosen in the prompt or by default.</summary>
    internal readonly struct Decision(int pushCells, int pullCells, bool pushFirst)
    {
        internal int PushCells { get; } = pushCells;
        internal int PullCells { get; } = pullCells;
        internal bool PushFirst { get; } = pushFirst;

        /// <summary>Full distance, push then pull: what On does, and what a prompt nobody answers falls back to.</summary>
        internal static Decision Default => new(MaxCells, MaxCells, true);

        internal static Decision None => new(0, 0, true);
    }

    /// <summary>What is kept for one cast of Eldritch Blast while its queued moves run.</summary>
    internal sealed class CastState
    {
        // set once the caster picked "for All": every later beam of the cast uses it without asking
        internal Decision? ForAll;

        // creatures a beam of this cast has already hit (used when only the first beam on a creature moves it)
        internal readonly HashSet<ulong> DecidedTargets = [];

        // who cast it: while this character is still acting, the cast's queued moves can still run
        internal GameLocationCharacter Caster;

        // Every queued move of the cast (the parameters of its spend-power action), and those that have applied
        // their effect. The difference is what is still to come, which EldritchBlastFallContext needs to know
        // whether a creature will be moved again before it may fall.
        internal readonly List<CharacterActionParams> Legs = [];
        internal readonly HashSet<CharacterActionParams> DoneLegs = [];
    }

    private sealed class Beam
    {
        internal CastState Cast;
        internal bool HasPull;
        internal bool HasPush;
        internal Decision? Decision;

        // set when this beam may not move the creature at all (see BeforeMove)
        internal bool Suppressed;
    }

    // one queued action of a beam: the first or the second of its (at most two) moves
    private sealed class Leg
    {
        internal Beam Beam;
        internal bool IsFirst;
    }

    /// <summary>
    ///     Called after the game handled one confirmed hit. The spend-power actions it queued on the casting action
    ///     since <paramref name="countBefore" /> are that beam's: they are noted as one beam, and the push is put before
    ///     the pull, so the default order never depends on the order of the character's powers.
    /// </summary>
    internal static void TagHit(CharacterAction castAction, int countBefore)
    {
        try
        {
            var queued = castAction?.ResultingActions;

            if (queued == null || queued.Count <= countBefore)
            {
                return;
            }

            var pushIndex = -1;
            var pullIndex = -1;

            for (var i = countBefore; i < queued.Count; i++)
            {
                var name = queued[i]?.ActionParams?.StringParameter;

                if (queued[i] is not CharacterActionSpendPower)
                {
                    continue;
                }

                if (name == RepellingPowerName)
                {
                    pushIndex = i;
                }
                else if (name == GraspPowerName)
                {
                    pullIndex = i;
                }
            }

            if (pushIndex < 0 && pullIndex < 0)
            {
                return;
            }

            if (pushIndex >= 0 && pullIndex >= 0 && pullIndex < pushIndex)
            {
                (queued[pushIndex], queued[pullIndex]) = (queued[pullIndex], queued[pushIndex]);
                (pushIndex, pullIndex) = (pullIndex, pushIndex);
            }

            var cast = Casts.GetOrCreateValue(castAction);

            cast.Caster ??= castAction.ActingCharacter;

            var beam = new Beam { Cast = cast, HasPush = pushIndex >= 0, HasPull = pullIndex >= 0 };

            if (pushIndex >= 0)
            {
                SetLeg(queued[pushIndex].ActionParams, new Leg { Beam = beam, IsFirst = true });
                cast.Legs.Add(queued[pushIndex].ActionParams);
            }

            if (pullIndex >= 0)
            {
                SetLeg(queued[pullIndex].ActionParams, new Leg { Beam = beam, IsFirst = pushIndex < 0 });
                cast.Legs.Add(queued[pullIndex].ActionParams);
            }
        }
        catch (Exception e)
        {
            // never break an attack over this: the beam then simply moves as if both invocations were On
            Main.Error($"EldritchBlastMove.TagHit: {e}");
        }
    }

    private static void SetLeg(CharacterActionParams actionParams, Leg leg)
    {
        Legs.Remove(actionParams);
        Legs.Add(actionParams, leg);
    }

    /// <summary>
    ///     The cast a spend-power action belongs to, if that action is one of the moves <see cref="TagHit" /> noted
    ///     (a Repelling Blast push or a Grasp of Hadar pull queued by a beam of Eldritch Blast that hit).
    /// </summary>
    internal static bool TryGetCast(CharacterActionParams actionParams, out CastState cast)
    {
        cast = actionParams != null && Legs.TryGetValue(actionParams, out var leg) ? leg.Beam.Cast : null;

        return cast != null;
    }

    #endregion

    #region Asking, and applying the answer

    /// <summary>
    ///     Called from the spend-power action just before it applies its effect forms to a target.
    ///     <paramref name="effectForms" /> is that action's own list, so replacing or removing a form in it changes
    ///     only this one move. The forms in it are shared with the power's definition and must not be changed in
    ///     place (see <see cref="ApplyMove" />).
    /// </summary>
    internal static IEnumerator BeforeMove(
        GameLocationBattleManager battleManager,
        CharacterActionSpendPower action,
        GameLocationCharacter caster,
        GameLocationCharacter target,
        List<EffectForm> effectForms)
    {
        if (action?.ActionParams == null ||
            !Legs.TryGetValue(action.ActionParams, out var leg) ||
            caster?.RulesetCharacter == null ||
            target == null)
        {
            yield break;
        }

        var beam = leg.Beam;
        var hero = caster.RulesetCharacter;

        // With "Push or pull a creature once for each hit" off, only the first beam of a cast that hits a creature
        // moves it. That goes for every caster, asked or not: before, a caster with both invocations simply On could
        // still move the creature again with a later beam, because the game replays each queued move's stored
        // destination. (In multiplayer the setting is the host's: clients play with the host's settings.)
        if (!Main.Settings.PushOrPullOncePerHit)
        {
            if (leg.IsFirst && !beam.Cast.DecidedTargets.Add(target.Guid))
            {
                beam.Suppressed = true;
            }

            if (beam.Suppressed)
            {
                effectForms.RemoveAll(x => x.FormType == EffectForm.EffectFormType.Motion);

                yield break;
            }
        }

        // A caster no player controls, or one with neither invocation on Ask, is otherwise left as it was:
        // full distance, push then pull, no waiting.
        if (!IsManaged(caster, hero, beam))
        {
            yield break;
        }

        // The move is worked out from where the creature stands, so the one before it has to have finished.
        yield return WaitUntilQuiet(target);

        // The creature is gone (an area it was moved through killed it and its body was removed, say): there is
        // nothing left to ask about or to move, so this action applies no move and raises no prompt.
        if (IsGone(target))
        {
            effectForms.RemoveAll(x => x.FormType == EffectForm.EffectFormType.Motion);

            yield break;
        }

        if (leg.IsFirst)
        {
            yield return Decide(battleManager, caster, target, beam);
        }

        // the first action of the beam was cut short before it could decide: leave this one as it is
        if (beam.Decision is not { } decision)
        {
            yield break;
        }

        // The first action makes whichever move was chosen to go first, the second makes the other, whatever power
        // each action belongs to.
        var both = beam.HasPush && beam.HasPull;
        var makesPush = both ? leg.IsFirst == decision.PushFirst : beam.HasPush;

        ApplyMove(effectForms, makesPush, makesPush ? decision.PushCells : decision.PullCells);
    }

    // Shared state only: this is evaluated on every computer and all must agree.
    private static bool IsManaged(GameLocationCharacter caster, RulesetCharacter hero, Beam beam)
    {
        var controllers = ServiceRepository.GetService<IPlayerControllerService>();

        if (controllers == null || !controllers.IsHumanControlled(caster))
        {
            return false;
        }

        return (beam.HasPush && IsAsk(hero, DatabaseHelper.InvocationDefinitions.RepellingBlast)) ||
               (beam.HasPull && IsAsk(hero, InvocationsBuilders.GraspingBlast));
    }

    // true for a creature the game has taken out of play: its body was destroyed or it has no rules side any more
    private static bool IsGone(GameLocationCharacter target)
    {
        return !target.Valid || target.RulesetActor == null;
    }

    /// <summary>
    ///     Called from the spend-power action for each of its targets, before it first touches the target and again
    ///     after <see cref="BeforeMove" /> (which can wait a long time: for the creature's previous move, and for the
    ///     caster's answer). True when the action is one of an Eldritch Blast cast's queued moves and the creature
    ///     has been taken out of play meanwhile, for instance killed by an area an earlier move took it through
    ///     and removed. The action must then leave that target alone: the rest of it reads the creature's rules
    ///     side (hit points, conditions, where to show the impact), which no longer exists and would throw, cutting
    ///     the whole action short (Codex's review of a8c25e62).
    ///     <para>
    ///         The move is also marked as no longer to come, so no other creature the cast has moved goes on
    ///         waiting for it before it may fall (<see cref="EldritchBlastFallContext" />).
    ///     </para>
    ///     <para>
    ///         Only the cast's own queued moves are covered. Any other power is left exactly as it was.
    ///     </para>
    /// </summary>
    internal static bool SkipGoneTarget(CharacterActionSpendPower action, GameLocationCharacter target)
    {
        var actionParams = action?.ActionParams;

        if (actionParams == null || !Legs.TryGetValue(actionParams, out var leg))
        {
            return false;
        }

        if (target != null && !IsGone(target))
        {
            return false;
        }

        leg.Beam.Cast.DoneLegs.Add(actionParams);

        return true;
    }

    /// <summary>
    ///     The targets the spend-power action reports as finished, once it has gone through all of them. For one of
    ///     an Eldritch Blast cast's queued moves, entries that are missing or have been taken out of play (the ones
    ///     <see cref="SkipGoneTarget" /> made the action skip, or removed since) are left out, so the mod's "effect
    ///     finished" handlers, which read each target's rules side, are never handed one that has none (Codex's
    ///     re-review of 3ad388c7). For any other power, and when nothing is missing, the list is returned as it is.
    /// </summary>
    internal static List<GameLocationCharacter> FinishedTargets(
        CharacterActionSpendPower action, List<GameLocationCharacter> targets)
    {
        var actionParams = action?.ActionParams;

        if (targets == null ||
            actionParams == null ||
            !Legs.TryGetValue(actionParams, out _) ||
            !targets.Any(x => x == null || IsGone(x)))
        {
            return targets;
        }

        return targets.Where(x => x != null && !IsGone(x)).ToList();
    }

    private static IEnumerator WaitUntilQuiet(GameLocationCharacter target)
    {
        var actions = ServiceRepository.GetService<IGameLocationActionService>() as GameLocationActionManager;

        // It ends on game state alone (the creature's own actions and moves have finished), never on elapsed time,
        // so every computer goes on from the same state. A creature that died or fell is quiet once that is over.
        //
        // The game's own "is acting" isn't used: it looks the creature up in a table and throws when the creature
        // is no longer in it, and the game takes a creature out of that table once it is removed from play, which
        // can happen during this wait (Codex's review of 93555539). An error here would cut the move's action
        // short. The mod's own test reads the same table safely, and a creature that is gone ends the wait.
        for (var frame = 0;; frame++)
        {
            if (IsGone(target) ||
                (!target.MoveInProgress && !target.Pushed &&
                 !EldritchBlastFallContext.IsActing(actions, target)))
            {
                yield break;
            }

            if (frame == SlowQuietFrames)
            {
                Main.Error($"EldritchBlastMove: still waiting for {target.Name} to stop moving.");
            }

            yield return null;
        }
    }

    private static IEnumerator Decide(
        GameLocationBattleManager battleManager,
        GameLocationCharacter caster,
        GameLocationCharacter target,
        Beam beam)
    {
        var cast = beam.Cast;

        if (cast.ForAll is { } forAll)
        {
            beam.Decision = forAll;

            yield break;
        }

        // nothing to move: no prompt, and nothing happens for this beam
        if (IsGone(target))
        {
            beam.Decision = Decision.None;

            yield break;
        }

        if (ServiceRepository.GetService<IGameLocationActionService>() is not GameLocationActionManager actionManager ||
            !battleManager)
        {
            beam.Decision = Decision.Default;

            yield break;
        }

        var pending = actionManager.PendingReactionRequestGroups.Count;
        var reactionParams = new CharacterActionParams(caster, (Id)ExtraActionId.DoNothingFree)
        {
            IsReactionEffect = true
        };
        var request = new ReactionRequestEldritchBlastMove(reactionParams, caster, target, beam.HasPush, beam.HasPull);

        actionManager.AddInterruptRequest(request);

        yield return battleManager.WaitForReactions(caster, actionManager, pending);

        // Not validated means nobody pressed one of the four buttons (the reaction timer ran out, or the game closed
        // the prompt): the beam moves as if the invocations were On, and the next beam asks again.
        if (!reactionParams.ReactionValidated)
        {
            beam.Decision = Decision.Default;

            yield break;
        }

        beam.Decision = request.Chosen;

        if (request.ForAll)
        {
            cast.ForAll = request.Chosen;
        }
    }

    // Makes this action's list of forms carry exactly one move: a push or a pull of the given distance, or none.
    //
    // The list is the action's own, but the forms in it are still the power definition's: the copy the spend-power
    // action makes is a new list holding the same form objects. Changing a form in place therefore changed the
    // invocation itself for the rest of the game session (seen in game: after one answer that swapped the order,
    // Grasp of Hadar pushed). So the motion form is replaced in the list by a real copy, made with the game's own
    // EffectForm.Copy, and only that copy is changed.
    private static void ApplyMove(List<EffectForm> effectForms, bool push, int cells)
    {
        var index = effectForms.FindIndex(x => x.FormType == EffectForm.EffectFormType.Motion);

        if (index < 0 || effectForms[index].MotionForm == null)
        {
            return;
        }

        if (cells <= 0)
        {
            effectForms.RemoveAt(index);

            return;
        }

        var original = effectForms[index];
        var copy = new EffectForm();

        copy.Copy(original);

        // never the definition's own motion form, whatever Copy did with it
        if (copy.motionForm == null || ReferenceEquals(copy.motionForm, original.MotionForm))
        {
            copy.motionForm = new MotionForm();
            copy.motionForm.Copy(original.MotionForm);
        }

        // the kind of move is taken from the invocation's own power, so this never invents a new kind of motion
        copy.motionForm.type = MotionTypeOf(push ? RepellingPower : GraspPower, original.MotionForm.Type);
        copy.motionForm.distance = Math.Min(cells, MaxCells);
        effectForms[index] = copy;
    }

    private static FeatureDefinitionPower RepellingPower =>
        DatabaseHelper.FeatureDefinitionPowers.PowerInvocationRepellingBlast;

    private static FeatureDefinitionPower GraspPower =>
        InvocationsBuilders.GraspingBlast.GrantedFeature as FeatureDefinitionPower;

    private static MotionForm.MotionType MotionTypeOf(FeatureDefinitionPower power, MotionForm.MotionType fallback)
    {
        var form = power?.EffectDescription.EffectForms
            .FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Motion);

        return form?.MotionForm?.Type ?? fallback;
    }

    #endregion
}
