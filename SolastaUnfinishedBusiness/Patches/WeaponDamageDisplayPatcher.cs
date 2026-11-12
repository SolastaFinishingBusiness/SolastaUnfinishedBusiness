using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

// Weapon damage display for Dungeon Maker custom weapons (issue #42, adapted from BlackguardRogue's Dungeon Maker
// tools). Display only: nothing here is used when damage is rolled.
//
// A custom weapon has two flat damage bonuses: the enhancement of the item it was made from (a "+1 Mace" keeps +1
// to hit and +1 damage), and the number its author typed in the Weapon Effects damage box, which is stored in the
// weapon's own damage form (BonusDamage). Combat adds both. The game shows them inconsistently:
//
//   1. The weapon tooltip's "damage bonus" row is shown when the ENHANCEMENT is not 0, and its text is formatted
//      from the ATTACK bonus (TooltipFeatureWeaponParameters.Bind uses the wrong variable; the two only agree on
//      ordinary +N weapons). So a +1 mace with -1 typed in showed +1 instead of nothing, and a plain mace with +2
//      typed in showed no row at all.
//   2. The damage breakdown (hovering the damage of an attack) lists both as "-1: Base" and "+1: <weapon>".
//
// The dice line of the tooltip is right already (the game adds the enhancement to the form's bonus there), and it
// reads the same DamageRollModifier value, so that value is not touched.
internal static class WeaponDamageDisplay
{
    // true while GuiEffectsFormater.Bind runs: the one place a damage form's bonus breakdown is formatted.
    // Gui.FormatTrendsList is also used for attack rolls, saving throws and checks, which are left alone.
    internal static bool InDamageFormatter;

    // the flat damage a custom weapon's own effect adds (what the Dungeon Maker's Weapon Effects damage box writes)
    internal static int CustomFlatDamage(ItemDefinition definition)
    {
        if (definition == null || !definition.IsUserContent || !definition.IsWeapon)
        {
            return 0;
        }

        return definition.WeaponDescription?.EffectDescription?.EffectForms?
            .FirstOrDefault(x => x is { FormType: EffectForm.EffectFormType.Damage, DamageForm: not null })?
            .DamageForm.BonusDamage ?? 0;
    }

    // The breakdown without a custom weapon's two bonuses when they cancel each other. RefreshAttackMode adds the
    // form's own bonus as (value, Base, "", null) and the weapon's enhancement as (value, Equipment, the weapon's
    // name, null). A pair is dropped only when the Equipment entry names a custom weapon whose typed-in bonus is
    // exactly the Base entry and whose enhancement is its opposite: an opposite modifier from the ammunition or
    // from anything else stays. The game's own list is not changed; a filtered copy is returned.
    internal static List<RuleDefinitions.TrendInfo> WithoutCancellingPair(List<RuleDefinitions.TrendInfo> trends)
    {
        if (trends == null || trends.Count < 2)
        {
            return trends;
        }

        var items = DatabaseRepository.GetDatabase<ItemDefinition>();

        for (var i = 0; i < trends.Count; i++)
        {
            var own = trends[i];

            if (own.value == 0 || own.sourceType != RuleDefinitions.FeatureSourceType.Base)
            {
                continue;
            }

            var j = trends.FindIndex(x =>
                x.sourceType == RuleDefinitions.FeatureSourceType.Equipment && x.value == -own.value &&
                !string.IsNullOrEmpty(x.sourceName) && items.TryGetElement(x.sourceName, out var weapon) &&
                CustomFlatDamage(weapon) == own.value);

            if (j < 0)
            {
                continue;
            }

            var result = new List<RuleDefinitions.TrendInfo>(trends);

            result.RemoveAt(Math.Max(i, j));
            result.RemoveAt(Math.Min(i, j));

            // one weapon, one pair: nothing else to look for
            return result;
        }

        return trends;
    }
}

[UsedImplicitly]
public static class WeaponDamageDisplayPatcher
{
    //PATCH: the weapon tooltip's damage bonus row shows a custom weapon's total flat bonus
    [HarmonyPatch(typeof(TooltipFeatureWeaponParameters), nameof(TooltipFeatureWeaponParameters.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipFeatureWeaponParameters_Bind
    {
        [UsedImplicitly]
        public static void Postfix(TooltipFeatureWeaponParameters __instance, ITooltip tooltip)
        {
            if (tooltip?.DataProvider is not IWeaponParametersProvider provider)
            {
                return;
            }

            var damageGroup = __instance.damageModifierGroup;
            var damageLabel = __instance.damageModifierValue;

            if (!damageGroup || !damageLabel)
            {
                return;
            }

            var definition = (provider as GuiEquipmentItem)?.EquipementItem?.ItemDefinition ??
                             (provider as GuiItemDefinition)?.ItemDefinition;
            var custom = WeaponDamageDisplay.CustomFlatDamage(definition);
            var table = __instance.attackModifiersTable;
            var hitGroup = __instance.hitModifierGroup;

            // What is on screen now. The game hides the whole table when a weapon has neither bonus and then leaves
            // the two rows inside it as the previous weapon set them, so a row only counts as shown when its table
            // is shown too.
            var tableShown = !table || table.gameObject.activeSelf;
            var shown = tableShown && damageGroup.gameObject.activeSelf;

            // the total is worked out first, and decides whether the row is there: the game only looks at the
            // enhancement, so it shows a row for +1 -1 and none for +0 +2
            var net = provider.DamageRollModifier + custom;
            var wanted = net != 0;

            if (wanted)
            {
                // also for ordinary weapons whose row the game showed: its text comes from the attack bonus
                damageLabel.Text = net.ToString("+0;-#");
            }

            // only a custom weapon's own bonus can change whether the row is there
            if (wanted == shown || custom == 0)
            {
                return;
            }

            // Both rows are set from this weapon's numbers, not from what they were left as: the "to hit" row is
            // there when the weapon has an attack bonus (the game's own rule), the damage row when the total is
            // not 0, and the table when either is.
            var hitWanted = provider.AttackRollModifier != 0;

            damageGroup.gameObject.SetActive(wanted);

            if (hitGroup)
            {
                hitGroup.gameObject.SetActive(hitWanted);
            }

            if (table)
            {
                table.gameObject.SetActive(wanted || hitWanted);
            }

            // the layout steps the game performs at the end of Bind
            var master = __instance.masterTable;

            if (!master)
            {
                return;
            }

            LayoutRebuilder.ForceRebuildLayoutImmediate(master);

            var rectTransform = __instance.RectTransform;

            rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, master.rect.height + 10f);
        }
    }

    //PATCH: marks the span in which a damage form's bonus breakdown is formatted; the filtering itself is in
    //GuiPatcher.FormatTrendsList_Patch, which writes the list in place of the game's formatter
    [HarmonyPatch(typeof(GuiEffectsFormater), nameof(GuiEffectsFormater.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GuiEffectsFormater_Bind
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            WeaponDamageDisplay.InDamageFormatter = true;
        }

        // a finalizer runs even if Bind throws, so the flag can't stay set
        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            WeaponDamageDisplay.InDamageFormatter = false;

            return __exception;
        }
    }
}
