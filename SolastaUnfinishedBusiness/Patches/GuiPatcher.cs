using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using static RuleDefinitions;
using static FeatureDefinitionAttributeModifier;
using static MotionForm;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiPatcher
{
    //PATCH: avoid too much missing translation messages during mod boot
    [HarmonyPatch(typeof(Gui), nameof(Gui.LocalizeImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LocalizeImpl_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref bool silent)
        {
            if (!silent)
            {
                silent = !Main.Enabled;
            }
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatEffectRange))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatEffectRange_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, RangeType rangeType, int rangeValue)
        {
            if (rangeValue > 1 && rangeType is RangeType.Touch or RangeType.MeleeHit)
            {
                __result += " " + Gui.FormatDistance(rangeValue);
            }
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatMotionForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatMotionForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, MotionForm motionForm)
        {
            //PATCH: format extra motion types
            __result = (ExtraMotionType)motionForm.Type switch
            {
                ExtraMotionType.CustomSwap => Gui.Format("Rules/&MotionFormSwitchFormat",
                    Gui.FormatDistance(motionForm.Distance)),
                ExtraMotionType.PushDown => Gui.Format("Rules/&MotionFormPushDownFormat",
                    Gui.FormatDistance(motionForm.Distance)),
                (ExtraMotionType)MotionType.DragToOrigin
                    when motionForm.Distance == MotionContext.PullOntoCaster
                    => Gui.Localize("Rules/&MotionFormPullOnTop"),
                _ => __result
            };
        }
    }

    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatSpellSlotsForm))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatSpellSlotsForm_Patch
    {
        [UsedImplicitly]
        public static void Postfix(ref string __result, SpellSlotsForm spellSlotsForm)
        {
            //PATCH: format extra motion types
            __result = (ExtraEffectType)spellSlotsForm.Type switch
            {
                ExtraEffectType.RecoverSorceryHalfLevelDown => Gui.Localize(
                    "Rules/&SpellSlotFormRecoverSorceryHalfLevelDownFormat"),
                _ => __result
            };
        }
    }

    //PATCH: always displays a sign on attribute modifiers
    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatTrendsList))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FormatTrendsList_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            out string __result,
            string output,
            List<TrendInfo> trends,
            bool ignoreZero)
        {
            //PATCH: a Dungeon Maker custom weapon's typed-in damage bonus and the enhancement of the item it was
            //made from are listed as one line under the weapon's name, or not at all when they add up to 0.
            //It is done here because this prefix writes the list instead of the game's formatter, so a second
            //prefix on the same method would only run by the luck of patch order. When they are combined, the full
            //list is formatted too and handed to WeaponDamageDisplay, which shows it in the tooltip while Alt is
            //held, as the game's detailed tooltips are.
            if (WeaponDamageDisplay.InDamageFormatter)
            {
                var filtered = WeaponDamageDisplay.WithWeaponBonusCombined(trends);

                if (!ReferenceEquals(filtered, trends))
                {
                    __result = Format(output, filtered, ignoreZero);
                    WeaponDamageDisplay.NoteCombinedBreakdown(__result, Format(output, trends, ignoreZero));

                    return false;
                }
            }

            __result = Format(output, trends, ignoreZero);

            return false;
        }

        private static string Format(string output, List<TrendInfo> trends, bool ignoreZero)
        {
            foreach (var trend in trends
                         .Where(trend => trend.value != 0 || !ignoreZero))
            {
                if (!string.IsNullOrEmpty(output))
                {
                    output += "\n";
                }

                var fixAdditive =
                    trend.attributeModifier?.operation is AttributeModifierOperation.Additive or
                        AttributeModifierOperation.AddConditionAmount or
                        AttributeModifierOperation.AddProficiencyBonus or
                        AttributeModifierOperation.AddSurroundingEnemies or
                        AttributeModifierOperation.AddAbilityScoreBonus or
                        AttributeModifierOperation.AddHalfProficiencyBonus;

                var value = fixAdditive || trend.additive ? trend.value.ToString("+0;-#") : trend.value.ToString();

                output += Gui.Format("{0}: {1}", value, Gui.FormatTrendInfo(trend));
            }

            return output;
        }
    }
}
