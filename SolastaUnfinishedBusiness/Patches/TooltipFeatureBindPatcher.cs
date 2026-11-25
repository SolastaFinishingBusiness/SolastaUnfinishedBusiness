using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class TooltipFeatureBindPatcher
{
    /// <summary>
    ///     Makes the tooltip's own character available while each part of a tooltip is filled in.
    ///     TooltipPanel.DelayedShow calls Bind(ITooltip) on every active TooltipFeature with the tooltip that asked for
    ///     it, and the features read the effect and text from the tooltip's data provider (for example
    ///     GuiPowerDefinition.EffectDescription and Description). That wrapper is shared by every tooltip of the
    ///     definition, so the modified effect it returns (PowerBundle.ModifyMagicEffectGui) would otherwise be worked
    ///     out for the selected or inspected character, not for the character the tooltip belongs to.
    /// </summary>
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        // every Bind(ITooltip) a game tooltip feature declares; each feature overrides the base method, so patching
        // TooltipFeature.Bind alone would never run
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return typeof(TooltipFeature).Assembly.GetTypes()
                .Where(type => typeof(TooltipFeature).IsAssignableFrom(type))
                .Select(type => type.GetMethod(
                    nameof(TooltipFeature.Bind),
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                    null,
                    [typeof(ITooltip)],
                    null))
                .Where(method => method is { IsAbstract: false });
        }

        // __state keeps the previous value, so a Bind that runs inside another restores the outer one
        [UsedImplicitly]
        public static void Prefix(ITooltip tooltip, out RulesetCharacter __state)
        {
            __state = Tooltips.BindingCharacter;
            Tooltips.BindingCharacter = Tooltips.GetTooltipCharacter(tooltip);
        }

        // a finalizer rather than a postfix, so a Bind that throws still clears the character
        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception, RulesetCharacter __state)
        {
            Tooltips.BindingCharacter = __state;

            return __exception;
        }
    }
}
