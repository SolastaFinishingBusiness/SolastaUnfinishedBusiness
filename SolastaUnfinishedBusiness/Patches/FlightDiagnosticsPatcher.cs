using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

// TEMPORARY DIAGNOSTICS, not for release: why Angelic Flight (Malakh) ends when Fighter's Second Wind is used
// (seen in game on 2026-11-15, user: "Investigate the cause."). Every removal of a flying condition and every
// termination of a flying power's effect is written to the mod log with its call stack, as "[FlightDiag]" lines.
// Remove this file once the cause is known.
[UsedImplicitly]
public static class FlightDiagnosticsPatcher
{
    private static bool IsFlight(string name)
    {
        return name != null && (name.Contains("Flying") || name.Contains("Flight") || name.Contains("Wings"));
    }

    [HarmonyPatch(typeof(RulesetActor), nameof(RulesetActor.RemoveConditionOfCategory))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RemoveConditionOfCategory_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetActor __instance, string category, RulesetCondition rulesetCondition)
        {
            var name = rulesetCondition?.ConditionDefinition?.Name;

            if (!IsFlight(name))
            {
                return;
            }

            Main.Info($"[FlightDiag] remove condition {name} category {category} from {__instance?.Name} " +
                      $"remaining {rulesetCondition.RemainingRounds} effect {rulesetCondition.EffectDefinitionName}" +
                      $"\n{Environment.StackTrace}");
        }
    }

    [HarmonyPatch(typeof(RulesetEffect), nameof(RulesetEffect.Terminate))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Terminate_Patch
    {
        [UsedImplicitly]
        public static void Prefix(RulesetEffect __instance, bool self)
        {
            var name = __instance?.SourceDefinition?.Name;

            if (!IsFlight(name))
            {
                return;
            }

            Main.Info($"[FlightDiag] terminate effect {name} self {self} remaining {__instance.RemainingRounds}" +
                      $"\n{Environment.StackTrace}");
        }
    }
}
