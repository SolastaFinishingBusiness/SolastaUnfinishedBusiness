using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Interfaces;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     The mod's rules for the concentration saving throws damage calls for.
///     <para>
///         Combined checks (CombineConcentrationChecks): the game rolls a concentration save for each damage form a
///         creature takes (GameLocationCharacter.CheckConcentration, from DamageSustained and
///         DamageAbsorbedByTemporaryHitPoints), so one hit with two damage types gives two saves. With the setting,
///         each damage instance is a scope:
///     </para>
///     <list type="bullet">
///         <item>one weapon hit with the damage riders added to its forms (a power action a hit starts afterwards is
///         an instance of its own);</item>
///         <item>one target entry of a spell or power (a Magic Missile dart, a Radiant Motes mote and an Eldritch Blast
///         beam are each their own entry);</item>
///         <item>one application of a spell, power or environment effect on a creature (entering an area or a cell of
///         it, a turn's recurring effect, a trap's activation), and one recurring tick of a condition.</item>
///     </list>
///     <para>
///         While a scope is the innermost open one, the damage each creature takes is added up and the game's own checks
///         only mark the creature for a check; when the scope closes, each marked creature makes one check against its
///         total, without a damage type. Every scope opens and closes within one synchronous call, so its checks roll at
///         a fixed point of the action that applied it, the same on every computer. Losing consciousness still ends
///         concentration at once, through the game's own path.
///     </para>
///     <para>
///         Temporary hit points: by default the whole damage counts, before temporary hit points take their share
///         (RulesetActor.AccountReceivedDamage, which RulesetCharacter.SustainDamage calls after difficulty scaling).
///         The game itself only counts what reaches hit points when temporary hit points absorb part of a hit, and
///         nothing when they absorb it exactly. With NoConcentrationChecksFromTemporaryHitPoints, only the damage that
///         reaches hit points counts.
///     </para>
///     <para>
///         Wild Shape: a shape (RulesetCharacterMonster with an original form) holds no concentrated spell; the
///         character in it does, and the game checks only the creature hit, so damage to a shape never calls for a
///         save. By default it does here: the shape saves (its own Constitution) for the spell of the character in it,
///         against the whole hit, and when the shape drops the character in it saves instead. The overflow
///         SustainDamage then passes on to that character is part of the same hit and calls for no second save. With
///         NoConcentrationChecksFromWildShapeDamage, damage to a shape calls for none, and only the overflow does, as
///         in the game. A shape that holds its own concentrated spell (the Shapechange spell) keeps the game's check.
///     </para>
/// </summary>
internal static class ConcentrationDamageContext
{
    private static readonly Stack<DamageInstance> Scopes = new();

    // the damage each creature was last dealt by SustainDamage, before temporary hit points; the check SustainDamage's
    // callbacks then ask for reads it (outside a scope)
    private static readonly Dictionary<RulesetActor, int> LastDamageReceived = new();

    // the characters in the shapes whose SustainDamage call is in progress (innermost last)
    private static readonly List<RulesetCharacter> ShapeOwnersTakingDamage = [];

    // set while the combined checks of an instance are rolled, so those checks are not gathered again
    private static bool _rollingCombined;

    private static bool IsActive => Main.Settings.CombineConcentrationChecks && Scopes.Count > 0 && !_rollingCombined;

    private static bool ShapesCheck => !Main.Settings.NoConcentrationChecksFromWildShapeDamage;

    private static bool TemporaryHitPointsCount => !Main.Settings.NoConcentrationChecksFromTemporaryHitPoints;

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

        RollCombined(instance);
    }

    /// <summary>Called from the AccountReceivedDamage prefix with the damage a creature takes.</summary>
    internal static void AddReceivedDamage(RulesetActor actor, int damage)
    {
        LastDamageReceived[actor] = damage;

        if (!IsActive || damage <= 0 || !TemporaryHitPointsCount)
        {
            return;
        }

        var isShape = actor is RulesetCharacter { OriginalFormCharacter: not null };

        // a shape's damage counts for the character in it, and the overflow its SustainDamage passes on to that
        // character is already in the shape's count; with shapes calling for no checks, only that overflow counts
        if (isShape ? !ShapesCheck : ShapesCheck && IsOverflowFromShape(actor))
        {
            return;
        }

        Scopes.Peek().GetEntry(isShape ? GetOwner(actor) : actor).Damage += damage;
    }

    /// <summary>Called before RulesetCharacter.SustainDamage: true for a shape, whose call must then be ended.</summary>
    internal static bool BeginSustainDamage(RulesetCharacter character)
    {
        if (character.OriginalFormCharacter is not { } owner)
        {
            return false;
        }

        ShapeOwnersTakingDamage.Add(owner);

        return true;
    }

    internal static void EndSustainDamage(bool begun)
    {
        if (begun && ShapeOwnersTakingDamage.Count > 0)
        {
            ShapeOwnersTakingDamage.RemoveAt(ShapeOwnersTakingDamage.Count - 1);
        }
    }

    // whether this damage to a character is the overflow of a hit on the shape it is in
    private static bool IsOverflowFromShape(RulesetActor actor)
    {
        return ShapeOwnersTakingDamage.Count > 0 && ShapeOwnersTakingDamage[ShapeOwnersTakingDamage.Count - 1] == actor;
    }

    // a shape's damage and checks belong to the character in the shape, whose form can change within one instance
    private static RulesetActor GetOwner(RulesetActor actor)
    {
        return actor is RulesetCharacter { OriginalFormCharacter: { } original } ? original : actor;
    }

    /// <summary>
    ///     The character in a shape whose concentration damage to the shape should check, or null: the creature hit
    ///     isn't a shape, the shape holds its own concentrated spell (the game checks that), the character in it
    ///     concentrates on nothing, or shapes call for no checks.
    /// </summary>
    internal static RulesetCharacter GetShapeOwnerToCheck(RulesetCharacter character)
    {
        return ShapesCheck &&
               character is { OriginalFormCharacter: { ConcentratedSpell: not null } owner, ConcentratedSpell: null }
            ? owner
            : null;
    }

    /// <summary>
    ///     For CheckConcentration outside a combined roll: whether to skip this check because it is a shape's overflow
    ///     the shape's own check already covered.
    /// </summary>
    internal static bool IsCoveredOverflow(RulesetCharacter character)
    {
        return !_rollingCombined && ShapesCheck && IsOverflowFromShape(character);
    }

    /// <summary>
    ///     For CheckConcentration outside a combined roll: the damage to check, the whole damage before temporary hit
    ///     points by default (the game passes what reached hit points), else the game's own amount.
    /// </summary>
    internal static int GetDamageToCheck(RulesetCharacter character, int damage)
    {
        if (_rollingCombined || !TemporaryHitPointsCount ||
            !LastDamageReceived.TryGetValue(character, out var received))
        {
            return damage;
        }

        LastDamageReceived.Remove(character);

        return Math.Max(damage, received);
    }

    /// <summary>
    ///     Called from the CheckConcentration prefix: marks the creature for the open scope's combined check and returns
    ///     true (the game's own check is then skipped), or returns false outside a scope. With temporary hit points not
    ///     counting, the amount the game passes (what reached hit points) is the creature's damage.
    /// </summary>
    internal static bool TryGather(GameLocationCharacter locationCharacter, RulesetCharacter character, int damage)
    {
        if (!IsActive || character == null)
        {
            return false;
        }

        var entry = Scopes.Peek().GetEntry(character.IsSubstitute && ShapesCheck ? GetOwner(character) : character);

        entry.Character = locationCharacter;

        if (!TemporaryHitPointsCount)
        {
            entry.Damage += damage;
        }

        return true;
    }

    /// <summary>
    ///     Whether a feature keeps the concentration of this character's hero from damage
    ///     (IPreventRemoveConcentrationOnDamage).
    /// </summary>
    internal static bool KeepsConcentrationFromDamage(RulesetCharacter character)
    {
        var hero = character?.GetOriginalHero();
        var concentratedSpell = hero?.ConcentratedSpell;

        return concentratedSpell != null &&
               hero.GetSubFeaturesByType<IPreventRemoveConcentrationOnDamage>()
                   .Any(x => x.SpellsThatShouldNotRollConcentrationCheckFromDamage(hero)
                       .Contains(concentratedSpell.SpellDefinition));
    }

    /// <summary>
    ///     The concentration check of a character in a shape, as GameLocationCharacter.CheckConcentration does it for a
    ///     creature's own spell: the roller saves (the shape, or the character itself once the shape dropped), and a
    ///     failure ends the character's concentration.
    /// </summary>
    internal static void RollShapeOwnerCheck(
        RulesetCharacter roller, RulesetCharacter owner, int damage, string damageType)
    {
        if (damage <= 0 || owner.ConcentratedSpell == null || KeepsConcentrationFromDamage(owner))
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
            owner.BreakConcentration();
        }
    }

    private static void RollCombined(DamageInstance instance)
    {
        _rollingCombined = true;

        try
        {
            foreach (var entry in instance.Entries)
            {
                // only creatures the game asked to check
                if (entry.Character is not { } character || entry.Damage <= 0)
                {
                    continue;
                }

                var current = character.RulesetCharacter;

                if (current is { IsDeadOrDyingOrUnconscious: false })
                {
                    // the creature, or the shape still standing (CheckConcentration then rolls for the character in it)
                    character.CheckConcentration(current, entry.Damage, string.Empty, true);
                }
                else if (entry.Actor is RulesetCharacter { IsDeadOrDyingOrUnconscious: false } owner &&
                         owner != current && GetShapeOwnerToCheck(current) == owner)
                {
                    // the shape dropped: the character in it, who keeps concentrating, saves
                    RollShapeOwnerCheck(owner, owner, entry.Damage, string.Empty);
                }
            }
        }
        finally
        {
            _rollingCombined = false;
        }
    }

    internal sealed class DamageInstance
    {
        internal readonly List<Entry> Entries = [];

        internal Entry GetEntry(RulesetActor actor)
        {
            var entry = Entries.Find(x => x.Actor == actor);

            if (entry != null)
            {
                return entry;
            }

            entry = new Entry { Actor = actor };
            Entries.Add(entry);

            return entry;
        }
    }

    internal sealed class Entry
    {
        internal RulesetActor Actor;

        // set once the game asks this creature for a concentration check inside the instance
        internal GameLocationCharacter Character;
        internal int Damage;
    }
}
