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
///         that beam hasn't been decided yet, and rewrites that action's own copy of the move.
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

    // A wait that never ends would hang the game, so it gives up after this many frames. Normal moves take far fewer.
    private const int MaxQuietFrames = 1800;

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

    private sealed class CastState
    {
        // set once the caster picked "for All": every later beam of the cast uses it without asking
        internal Decision? ForAll;

        // creatures a beam of this cast has already been decided for (used when only the first move counts)
        internal readonly HashSet<ulong> DecidedTargets = [];
    }

    private sealed class Beam
    {
        internal CastState Cast;
        internal bool HasPull;
        internal bool HasPush;
        internal Decision? Decision;
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

            var beam = new Beam
            {
                Cast = Casts.GetOrCreateValue(castAction), HasPush = pushIndex >= 0, HasPull = pullIndex >= 0
            };

            if (pushIndex >= 0)
            {
                SetLeg(queued[pushIndex].ActionParams, new Leg { Beam = beam, IsFirst = true });
            }

            if (pullIndex >= 0)
            {
                SetLeg(queued[pullIndex].ActionParams, new Leg { Beam = beam, IsFirst = pushIndex < 0 });
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

    #endregion

    #region Asking, and applying the answer

    /// <summary>
    ///     Called from the spend-power action just before it applies its effect forms to a target.
    ///     <paramref name="effectForms" /> is that action's own copy, so changing it changes only this one move.
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

        // A caster no player controls, or one with neither invocation on Ask, keeps today's behaviour untouched:
        // full distance, push then pull, no waiting.
        if (!IsManaged(caster, hero, beam))
        {
            yield break;
        }

        // The move is worked out from where the creature stands, so the one before it has to have finished.
        yield return WaitUntilQuiet(target);

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

    private static IEnumerator WaitUntilQuiet(GameLocationCharacter target)
    {
        var actions = ServiceRepository.GetService<IGameLocationActionService>();

        for (var frame = 0; frame < MaxQuietFrames; frame++)
        {
            if (!target.MoveInProgress && !target.Pushed && actions?.IsCharacterActing(target) != true)
            {
                yield break;
            }

            yield return null;
        }

        Main.Error($"EldritchBlastMove: {target.Name} was still moving after {MaxQuietFrames} frames; going on.");
    }

    private static IEnumerator Decide(
        GameLocationBattleManager battleManager,
        GameLocationCharacter caster,
        GameLocationCharacter target,
        Beam beam)
    {
        var cast = beam.Cast;

        // With "once for each hit" off, only the first move of a cast moves a creature: later beams on it do nothing
        // and aren't asked about.
        if (!Main.Settings.PushOrPullOncePerHit && !cast.DecidedTargets.Add(target.Guid))
        {
            beam.Decision = Decision.None;

            yield break;
        }

        if (cast.ForAll is { } forAll)
        {
            beam.Decision = forAll;

            yield break;
        }

        // nothing to move: no prompt, and nothing happens for this beam
        if (target.RulesetActor == null)
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

    // Makes this action's copied forms carry exactly one move: a push or a pull of the given distance, or none.
    private static void ApplyMove(List<EffectForm> effectForms, bool push, int cells)
    {
        var motion = effectForms.FirstOrDefault(x => x.FormType == EffectForm.EffectFormType.Motion);

        if (motion?.MotionForm == null)
        {
            return;
        }

        if (cells <= 0)
        {
            effectForms.Remove(motion);

            return;
        }

        // the kind of move is taken from the invocation's own power, so this never invents a new kind of motion
        motion.MotionForm.type = MotionTypeOf(push ? RepellingPower : GraspPower, motion.MotionForm.Type);
        motion.MotionForm.distance = Math.Min(cells, MaxCells);
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
