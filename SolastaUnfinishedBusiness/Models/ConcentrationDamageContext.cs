using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     Combines the concentration checks of one damage instance. The game rolls a concentration save for each damage
///     form a creature takes (GameLocationCharacter.CheckConcentration, from DamageSustained and
///     DamageAbsorbedByTemporaryHitPoints), so one hit with two damage types gives two saves. With the
///     CombineConcentrationChecks setting, each damage instance gets a <see cref="DamageInstance" />:
///     <list type="bullet">
///         <item>one attack's hit with its damage riders, including rider powers the attacker starts from the
///         attack's finished handlers (<see cref="OpenRiderWindow" />); the attack's coroutine waits for those powers
///         to end and then rolls the checks itself (<see cref="FinishHit" />), so the roll always comes at the same
///         point of the attack whatever the frame timing of the parallel rider coroutines;</item>
///         <item>one target entry of a spell or power (a Magic Missile dart, a Radiant Motes mote and an Eldritch Blast
///         beam are each their own entry);</item>
///         <item>one application of a spell, power or environment effect on a creature (entering an area, a turn's
///         recurring effect), and one recurring tick of a condition.</item>
///     </list>
///     While an instance is the innermost open scope, the damage each creature takes is added up from
///     RulesetActor.AccountReceivedDamage, which RulesetCharacter.SustainDamage calls with the damage after difficulty
///     scaling and before temporary hit points take their share; the game's own checks for a still-conscious creature
///     only mark that creature for a check. Damage to a shape (Wild Shape) counts for the character in that shape: the
///     full hit the shape takes counts, and the overflow SustainDamage then passes on to the character does not count
///     again. Once the instance has no holds left, each marked creature makes one check
///     against its total, without a damage type. Losing consciousness still ends concentration at once, through the
///     game's own path. Every computer runs the same instances in the same order, so the rolls happen at the same point
///     of the shared dice sequence.
/// </summary>
internal static class ConcentrationDamageContext
{
    private static readonly Stack<DamageInstance> Scopes = new();

    // each attack's hit instance, from its damage until FinishHit (or GuardHit) releases it
    private static readonly ConditionalWeakTable<CharacterAction, DamageInstance> Hits = new();

    // the open rider windows of each attacker, innermost last (an attack can start in another one's handlers)
    private static readonly Dictionary<GameLocationCharacter, List<DamageInstance>> RiderWindows = new();

    // rider power actions and the hit instance they belong to
    private static readonly ConditionalWeakTable<CharacterActionParams, DamageInstance> Riders = new();

    // set while the combined checks of an instance are rolled, so those checks are not gathered again
    private static bool _rollingCombined;

    // depth of SustainDamage calls on shapes (Wild Shape forms) in progress; the overflow those calls pass on to the
    // character in the shape is not counted again
    private static int _shapeDamageDepth;

    private static bool IsActive => Main.Settings.CombineConcentrationChecks && Scopes.Count > 0 && !_rollingCombined;

    /// <summary>
    ///     The instance of an attack's hit, with one hold the attack keeps until <see cref="FinishHit" />; null with
    ///     the setting off.
    /// </summary>
    internal static DamageInstance NewHit(CharacterAction action)
    {
        if (!Main.Settings.CombineConcentrationChecks)
        {
            return null;
        }

        var instance = new DamageInstance { Holds = 1 };

        Hits.Remove(action);
        Hits.Add(action, instance);

        return instance;
    }

    /// <summary>
    ///     Closes the hit's rider window, waits for the rider powers started in it to end (each holds the instance until
    ///     then), and releases the attack's hold, which rolls the combined checks in the attack's coroutine. A rider
    ///     action always runs (GameLocationActionManager.ExecuteInstantSingleAction adds it to the unstoppable
    ///     coroutines) and releases its hold in a finally block.
    /// </summary>
    internal static IEnumerator FinishHit(CharacterAction action)
    {
        if (!Hits.TryGetValue(action, out var instance))
        {
            yield break;
        }

        CloseRiderWindow(action.ActingCharacter, instance);

        while (instance.Holds > 1)
        {
            yield return null;
        }

        Hits.Remove(action);
        Release(instance);
    }

    /// <summary>
    ///     Runs an attack's coroutine; if it ends without reaching <see cref="FinishHit" /> (an exception), the hit's
    ///     window is closed and its hold released there.
    /// </summary>
    internal static IEnumerator GuardHit(IEnumerator execute, CharacterAction action)
    {
        try
        {
            while (execute.MoveNext())
            {
                yield return execute.Current;
            }
        }
        finally
        {
            if (Hits.TryGetValue(action, out var instance))
            {
                Hits.Remove(action);
                CloseRiderWindow(action.ActingCharacter, instance);
                Release(instance);
            }
        }
    }

    /// <summary>
    ///     Runs one application inside a scope of the given instance (or of a new one), then releases the scope's hold:
    ///     a new instance rolls its checks at once, a shared one when its last hold is released.
    /// </summary>
    internal static T Apply<T>(Func<T> apply, DamageInstance instance = null)
    {
        var scope = Begin(instance);

        try
        {
            return apply();
        }
        finally
        {
            End(scope);
        }
    }

    /// <summary>Opens a scope (for Harmony prefixes); pass its result to <see cref="End" />.</summary>
    internal static DamageInstance Begin(DamageInstance instance = null)
    {
        if (!Main.Settings.CombineConcentrationChecks)
        {
            return null;
        }

        instance ??= new DamageInstance();
        instance.Holds++;
        Scopes.Push(instance);

        return instance;
    }

    /// <summary>Closes a scope opened by <see cref="Begin" /> and releases its hold.</summary>
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

        Release(instance);
    }

    /// <summary>Releases one hold; the last one rolls the instance's checks.</summary>
    internal static void Release(DamageInstance instance)
    {
        if (instance == null || instance.Holds <= 0 || --instance.Holds > 0)
        {
            return;
        }

        RollCombined(instance);
    }

    /// <summary>
    ///     While an attacker's physical attack runs its finished handlers, rider powers the attacker starts
    ///     (<see cref="TagRider" />) join the hit's instance.
    /// </summary>
    internal static void OpenRiderWindow(CharacterAction action)
    {
        if (!Hits.TryGetValue(action, out var instance))
        {
            return;
        }

        if (!RiderWindows.TryGetValue(action.ActingCharacter, out var windows))
        {
            windows = [];
            RiderWindows[action.ActingCharacter] = windows;
        }

        windows.Add(instance);
    }

    private static void CloseRiderWindow(GameLocationCharacter attacker, DamageInstance instance)
    {
        if (!RiderWindows.TryGetValue(attacker, out var windows))
        {
            return;
        }

        windows.Remove(instance);

        if (windows.Count == 0)
        {
            RiderWindows.Remove(attacker);
        }
    }

    /// <summary>
    ///     Called when a character starts a power action: inside that character's rider window the action joins the
    ///     hit's instance and holds it until the action ends (<see cref="ReleaseRiderWhenDone" />).
    /// </summary>
    internal static void TagRider(GameLocationCharacter user, CharacterActionParams actionParams)
    {
        if (!Main.Settings.CombineConcentrationChecks ||
            !RiderWindows.TryGetValue(user, out var windows) ||
            windows.Count == 0)
        {
            return;
        }

        var instance = windows[windows.Count - 1];

        instance.Holds++;
        Riders.Remove(actionParams);
        Riders.Add(actionParams, instance);
    }

    /// <summary>The hit instance a rider power action belongs to, or null.</summary>
    internal static DamageInstance GetRiderInstance(CharacterActionParams actionParams)
    {
        return actionParams != null && Riders.TryGetValue(actionParams, out var instance) ? instance : null;
    }

    /// <summary>Runs a rider action's coroutine and releases its hold on the hit's instance when it ends.</summary>
    internal static IEnumerator ReleaseRiderWhenDone(IEnumerator execute, CharacterActionParams actionParams)
    {
        var instance = GetRiderInstance(actionParams);

        if (instance == null)
        {
            return execute;
        }

        return ReleaseWhenDone(execute, actionParams, instance);
    }

    private static IEnumerator ReleaseWhenDone(
        IEnumerator execute, CharacterActionParams actionParams, DamageInstance instance)
    {
        try
        {
            while (execute.MoveNext())
            {
                yield return execute.Current;
            }
        }
        finally
        {
            Riders.Remove(actionParams);
            Release(instance);
        }
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
        internal int Holds;

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
