using System.Runtime.CompilerServices;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

/// <summary>
///     Attack outcome rules shared by the attack roll (RulesetCharacterPatcher.RollAttack_Patch) and the reaction
///     handlers that reroll an attack's d20 and set the outcome themselves.
/// </summary>
internal static class AttackOutcomeRules
{
    // attacks whose critical hit a reaction turned into a normal hit (e.g. Blessing of the Wilderness); the attack's
    // final outcome is recomputed from its d20 after the reactions, which would otherwise make it critical again
    private static readonly ConditionalWeakTable<CharacterAction, object> CriticalCancelled = new();

    /// <summary>
    ///     The outcome of a rerolled d20: a natural 20 hits critically and a natural 1 misses critically; with
    ///     "critical hits and misses at a difference of 10" a total 10 or more above the AC is a critical hit and one
    ///     10 or more below it a critical miss.
    /// </summary>
    internal static RollOutcome FromRoll(int dieRoll, int successDelta)
    {
        if (successDelta >= 0)
        {
            return dieRoll == DiceMaxValue[(int)DieType.D20] ||
                   (Main.Settings.EnableCriticalHitsMissesAt10 && successDelta >= 10)
                ? RollOutcome.CriticalSuccess
                : RollOutcome.Success;
        }

        return dieRoll == DiceMinValue[(int)DieType.D20] ||
               (Main.Settings.EnableCriticalHitsMissesAt10 && successDelta <= -10)
            ? RollOutcome.CriticalFailure
            : RollOutcome.Failure;
    }

    internal static void CancelCritical(CharacterAction action)
    {
        CriticalCancelled.Remove(action);
        CriticalCancelled.Add(action, null);
    }

    // applied after the final recompute of an attack's outcome
    internal static RollOutcome KeepCancelledCritical(CharacterAction action, RollOutcome outcome)
    {
        return outcome == RollOutcome.CriticalSuccess && CriticalCancelled.TryGetValue(action, out _)
            ? RollOutcome.Success
            : outcome;
    }
}
