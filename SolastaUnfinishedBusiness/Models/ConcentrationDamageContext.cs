using System;
using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     Combines the concentration checks of one damage instance. The game rolls a concentration save for each damage
///     form a creature takes (GameLocationCharacter.CheckConcentration, from DamageSustained and
///     DamageAbsorbedByTemporaryHitPoints), so one hit with two damage types gives two saves. With the
///     CombineConcentrationChecks setting, each damage instance is a scope:
///     <list type="bullet">
///         <item>one attack's hit with the damage riders added to its forms (a power action a hit starts afterwards
///         is an instance of its own);</item>
///         <item>one target entry of a spell or power (a Magic Missile dart, a Radiant Motes mote and an Eldritch Blast
///         beam are each their own entry);</item>
///         <item>one application of a spell, power or environment effect on a creature (entering an area or a cell of
///         it, a turn's recurring effect), and one recurring tick of a condition.</item>
///     </list>
///     While a scope is the innermost open one, the damage each creature takes is added up from
///     RulesetActor.AccountReceivedDamage, which RulesetCharacter.SustainDamage calls with the damage after difficulty
///     scaling and before temporary hit points take their share; the game's own checks for a still-conscious creature
///     only mark that creature for a check. Damage to a shape (Wild Shape) counts for the character in that shape: the
///     full hit the shape takes counts, and the overflow SustainDamage then passes on to the character does not count
///     again. When the scope closes, each marked creature makes one check against its total, without a damage type.
///     Every scope opens and closes within one synchronous call, so its checks roll at a fixed point of the action that
///     applied it, the same on every computer. Losing consciousness still ends concentration at once, through the
///     game's own path.
/// </summary>
internal static class ConcentrationDamageContext
{
    private static readonly Stack<DamageInstance> Scopes = new();

    // set while the combined checks of an instance are rolled, so those checks are not gathered again
    private static bool _rollingCombined;

    // depth of SustainDamage calls on shapes (Wild Shape forms) in progress; the overflow those calls pass on to the
    // character in the shape is not counted again
    private static int _shapeDamageDepth;

    private static bool IsActive => Main.Settings.CombineConcentrationChecks && Scopes.Count > 0 && !_rollingCombined;

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

    /// <summary>
    ///     Called from the AccountReceivedDamage prefix: adds the damage a creature takes to the innermost open scope.
    /// </summary>
    internal static void AddReceivedDamage(RulesetActor actor, int damage)
    {
        var isShape = actor is RulesetCharacter { OriginalFormCharacter: not null };

        // the overflow a shape's SustainDamage passes on to the character in the shape is already in the shape's count
        if (!IsActive || damage <= 0 || (!isShape && _shapeDamageDepth > 0))
        {
            return;
        }

        Scopes.Peek().GetEntry(GetOwner(actor)).Damage += damage;
    }

    /// <summary>Called before RulesetCharacter.SustainDamage: true for a shape, whose call must then be ended.</summary>
    internal static bool BeginSustainDamage(RulesetCharacter character)
    {
        if (character.OriginalFormCharacter == null)
        {
            return false;
        }

        _shapeDamageDepth++;

        return true;
    }

    internal static void EndSustainDamage(bool begun)
    {
        if (begun && _shapeDamageDepth > 0)
        {
            _shapeDamageDepth--;
        }
    }

    // a shape's damage and checks belong to the character in the shape, whose form can change within one instance
    private static RulesetActor GetOwner(RulesetActor actor)
    {
        return actor is RulesetCharacter { OriginalFormCharacter: { } original } ? original : actor;
    }

    /// <summary>
    ///     Called from the CheckConcentration prefix for a creature still conscious: marks it for the open scope's
    ///     combined check and returns true (the game's own check is then skipped), or returns false outside a scope.
    /// </summary>
    internal static bool TryGather(GameLocationCharacter character)
    {
        if (!IsActive || character.RulesetCharacter == null)
        {
            return false;
        }

        var entry = Scopes.Peek().GetEntry(GetOwner(character.RulesetCharacter));

        entry.Character = character;

        return true;
    }

    private static void RollCombined(DamageInstance instance)
    {
        _rollingCombined = true;

        try
        {
            foreach (var entry in instance.Entries)
            {
                // only creatures the game asked to check; one that lost concentration or consciousness later in the
                // same instance is skipped, or the game's check returns at once without a concentrated spell
                if (entry.Character is not { } character ||
                    entry.Damage <= 0 ||
                    character.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } rulesetCharacter)
                {
                    continue;
                }

                character.CheckConcentration(rulesetCharacter, entry.Damage, string.Empty, true);
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
