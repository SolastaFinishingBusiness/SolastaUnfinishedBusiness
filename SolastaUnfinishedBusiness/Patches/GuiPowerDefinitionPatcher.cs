using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GuiPowerDefinitionPatcher
{
    [HarmonyPatch(typeof(GuiPowerDefinition), nameof(GuiPowerDefinition.EnumerateTags))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnumerateTags_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiPowerDefinition __instance)
        {
            //PATCH: adds `Unfinished Business` tag to all CE powers
            CeContentPackContext.AddCeTag(__instance.BaseDefinition, __instance.TagsMap);
        }
    }

    [HarmonyPatch(typeof(GuiPowerDefinition), nameof(GuiPowerDefinition.EffectDescription), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EffectDescription_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiPowerDefinition __instance, ref EffectDescription __result)
        {
            //PATCH: support for `ICustomMagicEffectBasedOnCaster` and `IModifySpellEffect` 
            // makes tooltips show modified effects
            __result = PowerBundle.ModifyMagicEffectGui(__result, __instance.PowerDefinition);
        }
    }

    [HarmonyPatch(typeof(GuiPowerDefinition), nameof(GuiPowerDefinition.Description), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Description_Getter_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GuiPowerDefinition __instance, ref string __result)
        {
            //PATCH: support for `IModifyPowerDescription`
            // lets a power's tooltip text match the modified effect the tooltip shows (EffectDescription above)
            var modifier = __instance.PowerDefinition.GetFirstSubFeatureOfType<IModifyPowerDescription>();

            if (modifier != null)
            {
                __result = modifier.GetDescription(
                    __instance.PowerDefinition, __instance.EffectDescription, __result);
            }
        }
    }
}
