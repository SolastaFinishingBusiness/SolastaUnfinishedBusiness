using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

/// <summary>
///     Damage instance scopes for ConcentrationDamageContext around the game's applications that have no scope of
///     their own, and the damage count each scope adds up.
/// </summary>
[UsedImplicitly]
public static class ConcentrationDamagePatcher
{
    //PATCH: supports `CombineConcentrationChecks`: counts the damage a creature takes, after difficulty scaling and
    //before temporary hit points take their share (RulesetCharacter.SustainDamage is the only caller)
    [HarmonyPatch(typeof(RulesetActor), nameof(RulesetActor.AccountReceivedDamage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AccountReceivedDamage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetActor __instance, int damage)
        {
            ConcentrationDamageContext.AddReceivedDamage(__instance, damage);
        }
    }

    //PATCH: supports `NoConcentrationChecksFromTemporaryHitPoints`: damage temporary hit points absorb in full calls
    //for no concentration check (the method does nothing else)
    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.DamageAbsorbedByTemporaryHitPoints))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DamageAbsorbedByTemporaryHitPoints_Patch
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return !Main.Settings.NoConcentrationChecksFromTemporaryHitPoints;
        }
    }

    //PATCH: supports `CombineConcentrationChecks`: marks SustainDamage calls on a shape (Wild Shape form), so the
    //overflow they pass on to the character in the shape is not counted twice
    [HarmonyPatch(typeof(RulesetCharacter), nameof(RulesetCharacter.SustainDamage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SustainDamage_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(RulesetCharacter __instance, out bool __state)
        {
            __state = ConcentrationDamageContext.BeginSustainDamage(__instance);
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, bool __state)
        {
            ConcentrationDamageContext.EndSustainDamage(__state);

            return __exception;
        }
    }

    //PATCH: supports `CombineConcentrationChecks`: one recurring tick of a condition is one damage instance, on both
    //the mod's replacement path and the game's own
    [HarmonyPatch(typeof(RulesetActor), nameof(RulesetActor.ExecuteRecurrentForms))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteRecurrentForms_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(out object __state)
        {
            __state = ConcentrationDamageContext.Begin();
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, object __state)
        {
            ConcentrationDamageContext.End(__state as ConcentrationDamageContext.DamageInstance);

            return __exception;
        }
    }

    //PATCH: supports `CombineConcentrationChecks`: one application of a spell's effect on a creature (entering its
    //area, its recurring turn effect) is one damage instance
    [HarmonyPatch(typeof(RulesetEffectSpell), nameof(RulesetEffectSpell.ApplyEffectOnCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEffectSpell_ApplyEffectOnCharacter_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(out object __state)
        {
            __state = ConcentrationDamageContext.Begin();
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, object __state)
        {
            ConcentrationDamageContext.End(__state as ConcentrationDamageContext.DamageInstance);

            return __exception;
        }
    }

    //PATCH: supports `CombineConcentrationChecks`: the same for a power's effect
    [HarmonyPatch(typeof(RulesetEffectPower), nameof(RulesetEffectPower.ApplyEffectOnCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEffectPower_ApplyEffectOnCharacter_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(out object __state)
        {
            __state = ConcentrationDamageContext.Begin();
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, object __state)
        {
            ConcentrationDamageContext.End(__state as ConcentrationDamageContext.DamageInstance);

            return __exception;
        }
    }

    //PATCH: supports `CombineConcentrationChecks`: one activation of an environment effect the game triggers directly
    //(traps, gadgets) is one damage instance; of its three overloads, the one taking the active effect applies the forms
    [HarmonyPatch(typeof(GameLocationEnvironmentManager),
        nameof(GameLocationEnvironmentManager.TriggerEnvironmentEffect), typeof(RulesetEffectEnvironment))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TriggerEnvironmentEffect_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(out object __state)
        {
            __state = ConcentrationDamageContext.Begin();
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, object __state)
        {
            ConcentrationDamageContext.End(__state as ConcentrationDamageContext.DamageInstance);

            return __exception;
        }
    }

    //PATCH: supports `CombineConcentrationChecks`: the same for an environment effect
    [HarmonyPatch(typeof(RulesetEffectEnvironment), nameof(RulesetEffectEnvironment.ApplyEffectOnCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetEffectEnvironment_ApplyEffectOnCharacter_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(out object __state)
        {
            __state = ConcentrationDamageContext.Begin();
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, object __state)
        {
            ConcentrationDamageContext.End(__state as ConcentrationDamageContext.DamageInstance);

            return __exception;
        }
    }
}
