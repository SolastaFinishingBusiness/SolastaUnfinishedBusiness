using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Spells;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     The concentration saving throws damage calls for.
///     <para>
///         The game checks concentration from two callbacks RulesetCharacter.SustainDamage raises
///         (GameLocationCharacter.DamageSustained and DamageAbsorbedByTemporaryHitPoints), with only what each tells
///         it: the damage that reached hit points, or the damage temporary hit points took when they were higher than
///         it. A hit they absorb exactly raises neither (SustainDamage IL_00d9 leaves with no damage left). A shape
///         (Wild Shape, Shapechange) holds the concentrated spell of the character in it
///         (RulesetCharacter.SetupFromShapeChangedCharacter moves it there and SetupFromSubstituteCharacter back), so
///         it saves while it stands; when it drops the game gives it no save (CheckConcentration: no break for a
///         substitute), and the overflow SustainDamage then deals to the character in it (IL_0556) finds no spell.
///     </para>
///     <para>
///         Here each SustainDamage call is a frame: its target, the damage it received (RulesetActor.AccountReceivedDamage,
///         after difficulty scaling, before temporary hit points), its hit points before and after, and the calls made
///         inside it (such as the overflow). The game's own checks are skipped while a frame
///         is open; when the outermost frame ends, the checks are worked out from the frames:
///     </para>
///     <list type="bullet">
///         <item>a creature concentrating that stays conscious saves against the damage it took: all of it, or with
///         NoConcentrationChecksFromTemporaryHitPoints only the hit points it lost (none when temporary hit points
///         absorb the hit exactly); one reduced to 0 hit points loses concentration at once, as in the game;</item>
///         <item>a shape reduced to 0 hit points makes the character in it save for the spell against the whole hit (the
///         overflow included);</item>
///         <item>with NoConcentrationChecksFromWildShapeDamage, a Wild Shape form (any shape not holding the Shapechange
///         spell) left with hit points calls for no save, and one reduced to 0 makes the character in it save against
///         the overflow only (none when there is no overflow).</item>
///     </list>
///     <para>
///         With CombineConcentrationChecks, each damage instance is a scope (one weapon hit with the damage added to
///         its forms; one target entry of a spell or power, so each Magic Missile dart, Radiant Motes mote and Eldritch
///         Blast beam on its own; one application of a spell, power or environment effect on a creature, and one
///         target of a trap; one recurring tick). The damage a spell's holder takes inside it adds up, and it saves once,
///         without a damage type, when the scope closes. Frames and scopes open and close within one synchronous call,
///         so every save rolls at a fixed point of the action that dealt the damage, the same on every computer.
///     </para>
/// </summary>
internal static class ConcentrationDamageContext
{
    private static readonly Stack<DamageInstance> Scopes = new();
    private static readonly Stack<DamageFrame> Frames = new();

    private static bool IsActive => Main.Settings.CombineConcentrationChecks && Scopes.Count > 0;

    private static bool WildShapeChecks => !Main.Settings.NoConcentrationChecksFromWildShapeDamage;

    private static bool TemporaryHitPointsCount => !Main.Settings.NoConcentrationChecksFromTemporaryHitPoints;

    /// <summary>Whether a SustainDamage frame is open, so the game's own concentration checks are left out.</summary>
    internal static bool IsTrackingDamage => Frames.Count > 0;

    /// <summary>Runs one damage instance's application inside a scope and then rolls its combined checks.</summary>
    internal static T Apply<T>(Func<T> apply)
    {
        var scope = Begin();

        try
        {
            return apply();
        }
        finally
        {
            End(scope);
        }
    }

    /// <summary>Opens a scope (for Harmony prefixes); pass its result to <see cref="End" />. Null with the setting off.</summary>
    internal static DamageInstance Begin()
    {
        if (!Main.Settings.CombineConcentrationChecks)
        {
            return null;
        }

        var instance = new DamageInstance();

        Scopes.Push(instance);

        return instance;
    }

    /// <summary>Closes a scope opened by <see cref="Begin" /> and rolls its combined checks.</summary>
    internal static void End(DamageInstance instance)
    {
        if (instance == null)
        {
            return;
        }

        if (Scopes.Count > 0 && Scopes.Peek() == instance)
        {
            Scopes.Pop();
        }

        foreach (var entry in instance.Entries.Where(x => x.Roller is { IsDeadOrDyingOrUnconscious: false }))
        {
            RollCheck(entry.Roller, entry.Holder, entry.Damage, string.Empty);
        }
    }

    /// <summary>Called before RulesetCharacter.SustainDamage: opens its frame (pass the result to EndSustainDamage).</summary>
    internal static object BeginSustainDamage(RulesetCharacter character, string damageType)
    {
        var frame = new DamageFrame
        {
            Actor = character,
            DamageType = damageType,
            HitPointsBefore = character.CurrentHitPoints
        };

        if (Frames.Count > 0)
        {
            frame.Parent = Frames.Peek();
            frame.Parent.Children.Add(frame);
        }

        Frames.Push(frame);

        return frame;
    }

    /// <summary>Called from the AccountReceivedDamage prefix (SustainDamage's only call) with the damage taken.</summary>
    internal static void AddReceivedDamage(RulesetActor actor, int damage)
    {
        if (Frames.Count > 0 && Frames.Peek().Actor == actor)
        {
            Frames.Peek().Received += damage;
        }
    }

    /// <summary>
    ///     Called after RulesetCharacter.SustainDamage: closes its frame, and works out the checks at the outermost. A
    ///     call that threw is left out, as its damage may not have been applied.
    /// </summary>
    internal static void EndSustainDamage(object state, Exception exception)
    {
        if (state is not DamageFrame frame || Frames.Count == 0 || Frames.Peek() != frame)
        {
            return;
        }

        Frames.Pop();

        if (exception != null)
        {
            frame.Parent?.Children.Remove(frame);

            return;
        }

        frame.HitPointsAfter = frame.Actor.CurrentHitPoints;

        if (frame.Parent == null)
        {
            Resolve(frame);
        }
    }

    // the hit points this call took from its target, without what calls made inside it took from the same target
    private static int OwnHitPointsLost(DamageFrame frame)
    {
        return Math.Max(0, frame.HitPointsBefore - frame.HitPointsAfter -
                           frame.Children.Where(x => x.Actor == frame.Actor)
                               .Sum(x => x.HitPointsBefore - x.HitPointsAfter));
    }

    // the damage of a call that counts: all it received, or only the hit points it took
    private static int Counted(DamageFrame frame)
    {
        return TemporaryHitPointsCount ? frame.Received : OwnHitPointsLost(frame);
    }

    // a shape other than one holding the Shapechange spell
    private static bool IsWildShapeForm(RulesetCharacter character)
    {
        return character.OriginalFormCharacter != null &&
               character.ConcentratedSpell?.SpellDefinition.Name != SpellBuilders.ShapechangeName;
    }

    private static void Resolve(DamageFrame frame)
    {
        var actor = frame.Actor;

        if (actor.ConcentratedSpell != null && !WildShapeChecks && IsWildShapeForm(actor))
        {
            // a Wild Shape form calls for no save itself; once reduced to 0, the character in it saves against the
            // overflow it took
            if (actor.IsDeadOrDyingOrUnconscious &&
                actor.OriginalFormCharacter is { } owner &&
                frame.Children.Where(x => x.Actor == owner).Sum(Counted) is var overflow and > 0)
            {
                Report(actor, owner, overflow, frame.DamageType);
            }
        }
        else if (actor.ConcentratedSpell != null)
        {
            if (!actor.IsDeadOrDyingOrUnconscious)
            {
                if (Counted(frame) is var damage and > 0)
                {
                    Report(actor, actor, damage, frame.DamageType);
                }
            }
            else if (actor.OriginalFormCharacter is { } owner)
            {
                // a dropped shape: the character in it saves for the spell, against the whole hit
                var damage = TemporaryHitPointsCount
                    ? frame.Received
                    : OwnHitPointsLost(frame) + frame.Children.Where(x => x.Actor == owner).Sum(OwnHitPointsLost);

                if (damage > 0)
                {
                    Report(actor, owner, damage, frame.DamageType);
                }
            }
            else
            {
                // the game's immediate break for a creature that dropped (GameLocationCharacter.CheckConcentration)
                actor.BreakConcentration();
            }
        }

        foreach (var child in frame.Children)
        {
            Resolve(child);
        }
    }

    // a check: added to the open scope's combined check, or rolled now
    private static void Report(RulesetCharacter holder, RulesetCharacter roller, int damage, string damageType)
    {
        // only creatures on the map are checked, as the game's callbacks only exist for them
        if (GameLocationCharacter.GetFromActor(holder) == null && GameLocationCharacter.GetFromActor(roller) == null)
        {
            return;
        }

        if (!IsActive)
        {
            RollCheck(roller, holder, damage, damageType);

            return;
        }

        var scope = Scopes.Peek();
        var entry = scope.Entries.Find(x => x.Holder == holder);

        if (entry == null)
        {
            entry = new Entry { Holder = holder };
            scope.Entries.Add(entry);
        }

        entry.Damage += damage;
        entry.Roller = roller;
    }

    /// <summary>
    ///     Whether a feature keeps the concentration of this character's hero from damage
    ///     (IPreventRemoveConcentrationOnDamage).
    /// </summary>
    private static bool KeepsConcentrationFromDamage(RulesetCharacter character)
    {
        var hero = character?.GetOriginalHero();
        var concentratedSpell = hero?.ConcentratedSpell ?? character?.ConcentratedSpell;

        return hero != null &&
               concentratedSpell != null &&
               hero.GetSubFeaturesByType<IPreventRemoveConcentrationOnDamage>()
                   .Any(x => x.SpellsThatShouldNotRollConcentrationCheckFromDamage(hero)
                       .Contains(concentratedSpell.SpellDefinition));
    }

    /// <summary>
    ///     A concentration check as GameLocationCharacter.CheckConcentration makes it for a conscious creature: the
    ///     roller saves (the holder, or the character in a dropped shape), and a failure ends the holder's
    ///     concentration.
    /// </summary>
    private static void RollCheck(RulesetCharacter roller, RulesetCharacter holder, int damage, string damageType)
    {
        if (damage <= 0 || holder.ConcentratedSpell == null || KeepsConcentrationFromDamage(holder))
        {
            return;
        }

        var outcome = RuleDefinitions.RollOutcome.Failure;

        if (ServiceRepository.GetService<IGameSettingsService>() is { NeverLoseConcentrationOnSpells: true } &&
            roller.Side == RuleDefinitions.Side.Ally)
        {
            outcome = RuleDefinitions.RollOutcome.Success;
        }
        else if (!roller.IsAutomaticallyFailingSavingThrow(AttributeDefinitions.Constitution))
        {
            roller.RollConcentrationCheckFromDamage(damage, damageType, out outcome);
        }

        if (outcome is RuleDefinitions.RollOutcome.Failure or RuleDefinitions.RollOutcome.CriticalFailure)
        {
            holder.BreakConcentration();
        }
    }

    /// <summary>
    ///     IRulesetImplementationService.ApplyEffectForms inside its own scope: put in place of the call
    ///     GameLocationEnvironmentManager.TriggerEnvironmentEffect makes for each target of a trap.
    /// </summary>
    internal static int ApplyEffectFormsInScope(
        IRulesetImplementationService service,
        List<EffectForm> effectForms,
        RulesetImplementationDefinitions.ApplyFormsParams formsParams,
        List<string> effectiveDamageTypes,
        out bool damageAbsorbedByTemporaryHitPoints,
        out bool terminateEffectOnTarget,
        bool retargeting,
        bool proxyOnly,
        bool forceSelfConditionOrLightOnly,
        RuleDefinitions.EffectApplication effectApplication,
        List<EffectFormFilter> filters)
    {
        var scope = Begin();

        try
        {
            return service.ApplyEffectForms(effectForms, formsParams, effectiveDamageTypes,
                out damageAbsorbedByTemporaryHitPoints, out terminateEffectOnTarget, retargeting, proxyOnly,
                forceSelfConditionOrLightOnly, effectApplication, filters);
        }
        finally
        {
            End(scope);
        }
    }

    internal sealed class DamageInstance
    {
        internal readonly List<Entry> Entries = [];
    }

    internal sealed class Entry
    {
        internal int Damage;
        internal RulesetCharacter Holder;

        // who saves: the holder, or the character in it once the shape holding the spell dropped
        internal RulesetCharacter Roller;
    }

    private sealed class DamageFrame
    {
        internal readonly List<DamageFrame> Children = [];
        internal RulesetCharacter Actor;
        internal string DamageType;
        internal int HitPointsAfter;
        internal int HitPointsBefore;
        internal DamageFrame Parent;
        internal int Received;
    }
}
