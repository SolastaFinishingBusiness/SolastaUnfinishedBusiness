using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

/// <summary>
///     The hooks of ConcentrationDamageContext: a frame around each RulesetCharacter.SustainDamage call with the damage
///     it counts, and damage instance scopes around the game's applications that have no scope of their own.
/// </summary>
[UsedImplicitly]
public static class ConcentrationDamagePatcher
{
    //PATCH: concentration checks of damage (ConcentrationDamageContext): the damage a creature takes, after difficulty
    //scaling and before temporary hit points take their share (RulesetCharacter.SustainDamage is the only caller)
    [HarmonyPatch(typeof(RulesetActor), nameof(RulesetActor.AccountReceivedDamage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AccountReceivedDamage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetActor __instance, int amount)
        {
            ConcentrationDamageContext.AddReceivedDamage(__instance, amount);
        }
    }

    //PATCH: the same for a hero: RulesetCharacterHero overrides AccountReceivedDamage without calling the base method
    //(it only counts sustainedInjuries), and SustainDamage calls it virtually, so each call reaches one of the two
    [HarmonyPatch(typeof(RulesetCharacterHero), nameof(RulesetCharacterHero.AccountReceivedDamage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetCharacterHero_AccountReceivedDamage_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacterHero __instance, int amount)
        {
            ConcentrationDamageContext.AddReceivedDamage(__instance, amount);
        }
    }

    //PATCH: concentration checks of damage (ConcentrationDamageContext): each SustainDamage call is a frame, and the
    //outermost one works out the checks when it ends
    [HarmonyPatch(typeof(RulesetCharacter), nameof(RulesetCharacter.SustainDamage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SustainDamage_Patch
    {
        [UsedImplicitly]
        [HarmonyPriority(Priority.First)]
        public static void Prefix(RulesetCharacter __instance, string damageType, out object __state)
        {
            __state = ConcentrationDamageContext.BeginSustainDamage(__instance, damageType);
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, object __state)
        {
            ConcentrationDamageContext.EndSustainDamage(__state, __exception);

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

    //PATCH: supports `CombineConcentrationChecks`: each target of an environment effect the game triggers directly
    //(traps, gadgets) is one damage instance, closed before the next target saves. Of TriggerEnvironmentEffect's three
    //overloads, the one taking the active effect applies the forms, once per target.
    [HarmonyPatch(typeof(GameLocationEnvironmentManager),
        nameof(GameLocationEnvironmentManager.TriggerEnvironmentEffect), typeof(RulesetEffectEnvironment))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TriggerEnvironmentEffect_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var applyEffectFormsMethod = typeof(IRulesetImplementationService).GetMethod(
                nameof(IRulesetImplementationService.ApplyEffectForms));
            var inScopeMethod = typeof(ConcentrationDamageContext).GetMethod(
                nameof(ConcentrationDamageContext.ApplyEffectFormsInScope),
                BindingFlags.Static | BindingFlags.NonPublic);

            return instructions.ReplaceCalls(applyEffectFormsMethod,
                "GameLocationEnvironmentManager.TriggerEnvironmentEffect",
                new CodeInstruction(OpCodes.Call, inScopeMethod));
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
