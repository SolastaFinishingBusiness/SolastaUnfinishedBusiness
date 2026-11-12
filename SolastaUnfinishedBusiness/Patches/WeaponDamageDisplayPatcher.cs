using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

// ============================================================================================================
// Weapon damage display fixes for Dungeon Maker custom items (combat is never changed, display only):
//   PART 1: the weapon tooltip's "DAMAGE BONUS" row (inventory tooltip)
//   PART 2: the damage breakdown tooltip on the Main Attack panel ("-1: Base" / "+1" cancelling pair)
// ============================================================================================================

// ---------------------------------------- PART 1: tooltip damage row -----------------------------------------
// Fixes the weapon tooltip's "DAMAGE BONUS" row for Dungeon Maker custom items.
//
// The problem: a custom item made from a "+1 Mace" keeps the base item's built-in enhancement (+1 attack, +1 damage).
// The author cancels the damage part with a -1 in the Weapon Effects damage box, which is stored in the weapon's
// damage form (BonusDamage). Combat adds both, so the net bonus is 0. The tooltip's "EFFECTS ON HIT" dice line is
// already right (the game adds the enhancement to the form's bonus there: +1 - 1 -> "1d6"), but the separate
// "DAMAGE BONUS" row shows only the built-in enhancement, so it still said +1.
//
// IMPORTANT: the dice line reads the same IWeaponParametersProvider.DamageRollModifier value, so that value must NOT
// be changed (an earlier attempt that did turned the dice line into "1d6-1"). This patch only touches the row,
// after the game has bound the tooltip:
//   row value = built-in enhancement + the weapon effect's own flat damage bonus (first damage form)
// which is the item's total flat bonus, the same total the dice line shows. If it is 0 the row is hidden and the
// tooltip is resized; otherwise the row shows the net value. Only custom (user content) weapons get the adjustment.
//
// Also fixed: TooltipFeatureWeaponParameters.Bind formats the text of the damage row from the ATTACK bonus instead of
// the damage bonus (the two only coincide on ordinary +N weapons), so the text is rewritten from the real value.
// Combat is untouched: nothing here changes how damage is rolled.
internal static class WeaponTooltipDamage
{
    // flat damage the item's own weapon effect adds (what the Dungeon Maker "Weapon Effects" damage box writes)
    internal static int CustomFlatDamage(ItemDefinition definition)
    {
        if (definition == null || !definition.IsUserContent || !definition.IsWeapon)
        {
            return 0;
        }

        var forms = definition.WeaponDescription?.EffectDescription?.EffectForms;

        var damageForm = forms?
            .FirstOrDefault(x => x != null && x.FormType == EffectForm.EffectFormType.Damage && x.DamageForm != null)
            ?.DamageForm;

        return damageForm?.BonusDamage ?? 0;
    }
}

[UsedImplicitly]
[HarmonyPatch(typeof(TooltipFeatureWeaponParameters), nameof(TooltipFeatureWeaponParameters.Bind))]
public static class WeaponTooltipDamageRowPatcher
{
    [UsedImplicitly]
    public static void Postfix(TooltipFeatureWeaponParameters __instance, ITooltip tooltip)
    {
        if (tooltip?.DataProvider is not IWeaponParametersProvider provider)
        {
            return;
        }

        var traverse = Traverse.Create(__instance);
        var damageGroup = traverse.Field("damageModifierGroup").GetValue<UnityEngine.RectTransform>();
        var damageLabel = traverse.Field("damageModifierValue").GetValue<GuiLabel>();

        // nothing to do unless the game decided to show the damage row
        if (damageGroup == null || damageLabel == null || !damageGroup.gameObject.activeSelf)
        {
            return;
        }

        var definition = (provider as GuiEquipmentItem)?.EquipementItem?.ItemDefinition ??
                         (provider as GuiItemDefinition)?.ItemDefinition;

        var net = provider.DamageRollModifier + WeaponTooltipDamage.CustomFlatDamage(definition);

        if (net != 0)
        {
            damageLabel.Text = net.ToString("+0;-#");

            return;
        }

        // net bonus is 0: hide the row (and the whole modifiers table if the hit row is hidden too), then redo
        // the same layout steps the game performs at the end of Bind
        damageGroup.gameObject.SetActive(false);

        var hitGroup = traverse.Field("hitModifierGroup").GetValue<UnityEngine.RectTransform>();
        var table = traverse.Field("attackModifiersTable").GetValue<UnityEngine.RectTransform>();

        if (table != null && (hitGroup == null || !hitGroup.gameObject.activeSelf))
        {
            table.gameObject.SetActive(false);
        }

        var master = traverse.Field("masterTable").GetValue<UnityEngine.RectTransform>();

        if (master == null)
        {
            return;
        }

        UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(master);

        var rectTransform = __instance.RectTransform;

        rectTransform.sizeDelta = new UnityEngine.Vector2(rectTransform.sizeDelta.x, master.rect.height + 10f);
    }
}

// ------------------------------------ PART 2: damage breakdown cleanup ---------------------------------------
// Cleans up the "Damage Bonus" breakdown tooltip (e.g. hovering the damage on the Main Attack panel).
//
// The problem: for a custom item made from a "+1 Mace" whose Weapon Effects damage box was set to -1, the game builds
// two list entries in RulesetCharacterHero.RefreshAttackMode that cancel each other out:
//   "-1: Base"       the damage form's own flat bonus (the -1 typed in the Dungeon Maker)
//   "+1: <item>"     the item's built-in damage enhancement (ComputeAttackDamageEnhancement, source type Equipment)
// The total in the header (e.g. +2 from Strength) is right, but listing "+1" and "-1" side by side looks strange.
//
// The fix (display only): while the damage breakdown is being formatted, drop a pair of entries made of one Base entry
// and one Equipment entry whose values are exact opposites. The list the game keeps is NOT modified (we hand the
// formatter a filtered copy), and nothing here is used when rolling damage, so combat is unchanged. The tooltip then
// just shows what is left, e.g. "+2: STR".
//
// Scope: only active inside GuiEffectsFormater.Bind (the weapon damage line / Main Attack panel); Gui.FormatTrendsList is
// also used for attack rolls, saving throws, ability checks, etc., which are left alone.
internal static class DamageTrendDisplay
{
    internal static bool InDamageFormatter;

    // returns the same list if there is nothing to remove, otherwise a filtered copy
    internal static List<RuleDefinitions.TrendInfo> WithoutCancellingPairs(List<RuleDefinitions.TrendInfo> trends)
    {
        if (trends == null || trends.Count < 2)
        {
            return trends;
        }

        var result = new List<RuleDefinitions.TrendInfo>(trends);
        var removed = false;

        for (var i = 0; i < result.Count; i++)
        {
            var value = result[i].value;

            if (value == 0 || result[i].sourceType != RuleDefinitions.FeatureSourceType.Base)
            {
                continue;
            }

            var j = result.FindIndex(x =>
                x.sourceType == RuleDefinitions.FeatureSourceType.Equipment && x.value == -value);

            if (j < 0)
            {
                continue;
            }

            result.RemoveAt(Math.Max(i, j));
            result.RemoveAt(Math.Min(i, j));
            removed = true;
            i = Math.Min(i, j) - 1; // rescan from the first removed position
        }

        return removed ? result : trends;
    }
}

// marks the span in which the weapon damage breakdown is formatted
[UsedImplicitly]
[HarmonyPatch(typeof(GuiEffectsFormater), nameof(GuiEffectsFormater.Bind))]
public static class DamageTrendScopePatcher
{
    [UsedImplicitly]
    public static void Prefix()
    {
        DamageTrendDisplay.InDamageFormatter = true;
    }

    // a finalizer always runs, even if Bind throws, so the flag can never stay set
    [UsedImplicitly]
    public static Exception Finalizer(Exception __exception)
    {
        DamageTrendDisplay.InDamageFormatter = false;

        return __exception;
    }
}

[UsedImplicitly]
[HarmonyPatch(typeof(Gui), nameof(Gui.FormatTrendsList))]
public static class DamageTrendCancelPatcher
{
    [UsedImplicitly]
    public static void Prefix(ref List<RuleDefinitions.TrendInfo> trends)
    {
        if (DamageTrendDisplay.InDamageFormatter)
        {
            trends = DamageTrendDisplay.WithoutCancellingPairs(trends);
        }
    }
}
