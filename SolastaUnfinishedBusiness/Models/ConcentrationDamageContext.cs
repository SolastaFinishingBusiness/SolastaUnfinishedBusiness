using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     The concentration saving throws damage calls for. The game checks concentration from two callbacks
///     RulesetCharacter.SustainDamage raises (GameLocationCharacter.DamageSustained and
///     DamageAbsorbedByTemporaryHitPoints), with only what each tells it: the damage that reached hit points, or the
///     damage temporary hit points took in full when they were higher than it. A hit they absorb exactly raises
///     neither, and a Wild Shape form holds no concentrated spell, so damage to it never checks the druid's. Here each
///     SustainDamage call is a frame: its target, the damage it received (RulesetActor.AccountReceivedDamage, after
///     difficulty scaling, before temporary hit points), the temporary hit points it used and whether its target is
///     still conscious; the call SustainDamage makes on the character in a dropped shape for the overflow
///     (IL_0556) is a frame inside the shape's. The game's own checks are skipped while a frame is open; when the
///     outermost frame closes the checks are worked out from the frames.
///     <list type="bullet">
///         <item>A creature concentrating on a spell that stays conscious saves against the damage it took: all of it,
///         or with NoConcentrationChecksFromTemporaryHitPoints only what reached hit points. One that drops loses
///         concentration at once, as in the game.</item>
///         <item>A shape (with no spell of its own) saves for the spell of the character in it, against the whole hit
///         including the overflow; the shape rolls while it stands, the character once the shape dropped. With
///         NoConcentrationChecksFromWildShapeDamage a shape calls for no save and only the overflow counts, as in the
///         game. A shape holding its own spell (the Shapechange spell) is checked like any creature.</item>
///     </list>
///     With CombineConcentrationChecks, each damage instance is a scope (one weapon hit with the damage added to its
///     forms; one target entry of a spell or power, so each Magic Missile dart, Radiant Motes mote and Eldritch Blast
///     beam on its own; one application of a spell, power or environment effect on a creature; one recurring tick).
///     The damage a spell's holder takes inside it adds up, and it saves once, without a damage type, when the scope
///     closes. Frames and scopes open and close within one synchronous call, so every save rolls at a fixed point of
///     the action that dealt the damage, the same on every computer.
/// </summary>
internal static class ConcentrationDamageContext
{
    // the condition RulesetCharacter.SustainDamage looks for before using temporary hit points
    private const string TemporaryHitPointsCondition = "ConditionTemporaryHitPoints";

    private static readonly Stack<DamageInstance> Scopes = new();
    private static readonly Stack<DamageFrame> Frames = new();

    private static bool IsActive => Main.Settings.CombineConcentrationChecks && Scopes.Count > 0;

    private static bool ShapesCheck => !Main.Settings.NoConcentrationChecksFromWildShapeDamage;

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

        foreach (var entry in instance.Entries)
        {
            var roller = entry.Roller;

            // the roller still stands; or the shape that took the damage dropped and the character in it saves
            if (roller is { IsDeadOrDyingOrUnconscious: false })
            {
                RollCheck(roller, entry.Holder, entry.Damage, string.Empty);
            }
            else if (roller?.OriginalFormCharacter == entry.Holder &&
                     entry.Holder is { IsDeadOrDyingOrUnconscious: false })
            {
                RollCheck(entry.Holder, entry.Holder, entry.Damage, string.Empty);
            }
        }
    }

    /// <summary>Called before RulesetCharacter.SustainDamage: opens its frame (pass the result to EndSustainDamage).</summary>
    internal static object BeginSustainDamage(RulesetCharacter character, string damageType)
    {
        var frame = new DamageFrame
        {
            Actor = character,
            DamageType = damageType,
            TemporaryHitPointsBefore = character.HasConditionOfType(TemporaryHitPointsCondition)
                ? character.temporaryHitPoints
                : 0
        };

        if (Frames.Count > 0)
        {
            Frames.Peek().Children.Add(frame);
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

    /// <summary>Called after RulesetCharacter.SustainDamage: closes its frame, and works out the checks at the outermost.</summary>
    internal static void EndSustainDamage(object state)
    {
        if (state is not DamageFrame frame || Frames.Count == 0 || Frames.Peek() != frame)
        {
            return;
        }

        Frames.Pop();

        var actor = frame.Actor;

        frame.TemporaryHitPointsUsed = Math.Max(0,
            frame.TemporaryHitPointsBefore -
            (actor.HasConditionOfType(TemporaryHitPointsCondition) ? actor.temporaryHitPoints : 0));

        if (Frames.Count == 0)
        {
            Resolve(frame);
        }
    }

    // the damage of a frame that counts: all of it, or only what reached hit points
    private static int Counted(DamageFrame frame)
    {
        return TemporaryHitPointsCount
            ? frame.Received
            : Math.Max(0, frame.Received - frame.TemporaryHitPointsUsed);
    }

    private static void Resolve(DamageFrame frame)
    {
        var actor = frame.Actor;
        var children = frame.Children;

        // a shape checks for the spell of the character in it; the overflow call on that character is part of the hit
        if (ShapesCheck && actor is { OriginalFormCharacter: { } owner, ConcentratedSpell: null })
        {
            var overflow = children.Where(x => x.Actor == owner).ToList();
            var overflowReceived = overflow.Sum(x => x.Received);
            var counted = TemporaryHitPointsCount
                ? frame.Received
                : Math.Max(0, frame.Received - overflowReceived - frame.TemporaryHitPointsUsed) +
                  overflow.Sum(Counted);

            if (owner.ConcentratedSpell != null && counted > 0)
            {
                Report(owner, actor.IsDeadOrDyingOrUnconscious ? owner : actor, counted, frame.DamageType);
            }

            foreach (var child in children.Where(x => x.Actor != owner))
            {
                Resolve(child);
            }

            return;
        }

        if (actor.ConcentratedSpell != null)
        {
            if (actor.IsDeadOrDyingOrUnconscious)
            {
                // the game breaks concentration at once, except for a shape (GameLocationCharacter.CheckConcentration)
                if (!actor.IsSubstitute)
                {
                    actor.BreakConcentration();
                }
            }
            else if (Counted(frame) is var damage and > 0)
            {
                Report(actor, actor, damage, frame.DamageType);
            }
        }

        foreach (var child in children)
        {
            Resolve(child);
        }
    }

    // a check: added to the open scope's combined check, or rolled now
    private static void Report(RulesetCharacter holder, RulesetCharacter roller, int damage, string damageType)
    {
        // only creatures on the map are checked, as the game's callbacks only exist for them
        if (GameLocationCharacter.GetFromActor(roller) == null && GameLocationCharacter.GetFromActor(holder) == null)
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
        var concentratedSpell = hero?.ConcentratedSpell;

        return concentratedSpell != null &&
               hero.GetSubFeaturesByType<IPreventRemoveConcentrationOnDamage>()
                   .Any(x => x.SpellsThatShouldNotRollConcentrationCheckFromDamage(hero)
                       .Contains(concentratedSpell.SpellDefinition));
    }

    /// <summary>
    ///     A concentration check as GameLocationCharacter.CheckConcentration makes it for a conscious creature: the
    ///     roller saves (a shape for the character in it, or the holder itself), and a failure ends the holder's
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

    internal sealed class DamageInstance
    {
        internal readonly List<Entry> Entries = [];
    }

    internal sealed class Entry
    {
        internal int Damage;
        internal RulesetCharacter Holder;

        // the creature that took the damage last (the holder, or a shape it is in)
        internal RulesetCharacter Roller;
    }

    private sealed class DamageFrame
    {
        internal readonly List<DamageFrame> Children = [];
        internal RulesetCharacter Actor;
        internal string DamageType;
        internal int Received;
        internal int TemporaryHitPointsBefore;
        internal int TemporaryHitPointsUsed;
    }
}
