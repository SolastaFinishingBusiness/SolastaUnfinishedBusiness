using System.Collections.Generic;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Interfaces;

/// <summary>
///     A hook of the creature that DEALS the damage: it changes how the damaged creature's damage affinities
///     (resistances, immunities, vulnerabilities) apply to that damage, for example "your spells ignore resistance".
///     <c>RulesetActorPatcher.ModulateSustainedDamage_Patch</c> calls it only for the damage's source (an effect
///     proxy counts as its controller), never for the damaged creature's own hooks (#44): until then it ran on both
///     sides, so a hook meant for its owner's damage also stripped its owner's own resistances whenever they were hit.
///     <see cref="ModifyDamageAffinityContext" /> says what the damage comes from while it runs.
/// </summary>
public interface IModifyDamageAffinity
{
    public void ModifyDamageAffinity(
        [UsedImplicitly] RulesetActor defender,
        RulesetActor attacker,
        List<FeatureDefinition> features);
}

/// <summary>
///     A hook of the creature that TAKES the damage: it changes its own damage affinities against any damage, for
///     example a mark put on a target that makes it lose a resistance. Called with the same arguments as
///     <see cref="IModifyDamageAffinity" /> (attacker is null when the damage has no source creature), but only for
///     the damaged creature's own definitions and conditions (#44).
/// </summary>
public interface IModifyOwnDamageAffinity
{
    public void ModifyDamageAffinity(
        RulesetActor defender,
        [CanBeNull] RulesetActor attacker,
        List<FeatureDefinition> features);
}

/// <summary>
///     What the damage being worked out comes from, set by <c>RulesetActorPatcher.ModulateSustainedDamage_Patch</c>
///     around its calls to the affinity hooks above and cleared after them. The hooks' signature carries only the two
///     creatures, so hooks that care whether the damage is a spell's or a weapon attack's read it here (#44).
/// </summary>
public static class ModifyDamageAffinityContext
{
    /// <summary>The effect dealing the damage (a spell, a power), or null, for example for a weapon attack.</summary>
    [CanBeNull]
    public static RulesetEffect ActiveEffect { get; internal set; }

    /// <summary>The attack mode dealing the damage (a weapon or unarmed attack), or null.</summary>
    [CanBeNull]
    public static RulesetAttackMode AttackMode { get; internal set; }

    /// <summary>True while the damage comes from a spell.</summary>
    public static bool IsSpellDamage => ActiveEffect is RulesetEffectSpell;
}
