using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
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
//   2. The damage breakdown (hovering the damage of an attack) lists both as separate entries, the typed number as
//      "Base" and the enhancement under the weapon's name: "+3: Base" and "+1: Excalibur" for a +1 sword with +3
//      typed in, or "-1: Base" and "+1: <weapon>" for a +1 mace with -1 typed in.
//
// The dice line of the tooltip is right already (the game adds the enhancement to the form's bonus there), and it
// reads the same DamageRollModifier value, so that value is not touched.
//
// The breakdown (2) is changed only while "Show a weapon's own damage bonus and its enhancement as one line" is on
// (Gameplay > Campaigns, Combat section, on by default): the two entries become one, under the weapon's name, and
// none when they add up to 0 ("+4: Excalibur"; nothing for the mace). It applies to any weapon with a damage bonus of
// its own, although in practice only Dungeon Maker weapons have one. The full list is then still there while Alt is
// held, the way the game shows a detailed tooltip (see ShowTooltip below).
//
// Entries with no name: Gui.FormatTrendInfo prints an entry's source name, or the title it finds for it, and some
// entries carry an empty name, which shows as "+1:" with nothing after it (such as the Main Attack tooltip of a
// custom weapon). Wherever a breakdown entry would print nothing, its source is traced
// (NameOfUnnamed): the object the entry was made from, the weapon of the attack being shown, or else the kind of
// source ("Item", "Spell"...).
internal static class WeaponDamageDisplay
{
    // true while GuiEffectsFormater.Bind runs: the one place a damage form's bonus breakdown is formatted.
    // Gui.FormatTrendsList is also used for attack rolls, saving throws and checks, which are left alone.
    internal static bool InDamageFormatter;

    // The weapon of the attack whose box is being filled (AttackModeBox.Bind), while it is: the breakdown's
    // enhancement entry doesn't always name its weapon, so this is how the entry is tied to it. Null elsewhere.
    internal static ItemDefinition AttackWeapon;

    // A breakdown just changed by WithWeaponBonusCombined, and the full one, until the effect line that shows it
    // takes them (FeatureElementEffectLine.Bind, right after the formatter returned).
    private static string _pendingShort;
    private static string _pendingFull;

    // the effect lines' tooltips showing a combined breakdown, with both texts
    private static readonly ConditionalWeakTable<GuiTooltip, Breakdown> Breakdowns = new();

    // the flat damage a custom weapon's own effect adds (what the Dungeon Maker's Weapon Effects damage box writes);
    // the weapon tooltip's row (part 1) is only corrected for those
    internal static int CustomFlatDamage(ItemDefinition definition)
    {
        return definition != null && definition.IsUserContent ? OwnFlatDamage(definition) : 0;
    }

    // the flat damage any weapon's own damage form adds, which the breakdown lists as its Base entry
    internal static int OwnFlatDamage(ItemDefinition definition)
    {
        if (definition == null || !definition.IsWeapon)
        {
            return 0;
        }

        return definition.WeaponDescription?.EffectDescription?.EffectForms?
            .FirstOrDefault(x => x is { FormType: EffectForm.EffectFormType.Damage, DamageForm: not null })?
            .DamageForm.BonusDamage ?? 0;
    }

    // The breakdown with a weapon's own damage bonus folded into the weapon's line. RefreshAttackMode adds the
    // form's own bonus (the number typed into a custom weapon) as (value, Base, "", null), and the enhancement of
    // the weapon's item as (value, Equipment, item name, null) after the ability score entry (IL_0973 to IL_09d9).
    //
    // The Base entry belongs to a weapon whose own damage bonus is exactly its value. The enhancement entry of that
    // weapon is found by its name or, when it has none, as the weapon of the attack being shown (AttackWeapon); an
    // Equipment entry of anything else (ammunition, another item) never matches, as its item's own bonus differs or
    // it is named for another item.
    //   - Both there: one entry where the enhancement was, worth the sum, under the weapon's name ("+3: Base" and
    //     "+1: Excalibur" become "+4: Excalibur"); none when the sum is 0 (+1 with -1 typed in).
    //   - No enhancement entry (a weapon made from an item without one), but the attack's weapon is known: the Base
    //     entry is shown under the weapon's name instead ("+2: Base" becomes "+2: <weapon>"). Its sourceName is
    //     the weapon's definition name, which Gui.FormatTrendInfo looks up to print the weapon's title, as it does
    //     for the enhancement entry.
    //   - Otherwise the Base entry can't be tied to a weapon and stays.
    // The game's own list is not changed (the attack mode keeps it); a changed copy is returned, or the same list
    // when nothing changes, which is how the caller knows whether to offer the full one on Alt.
    internal static List<RuleDefinitions.TrendInfo> WithWeaponBonusCombined(List<RuleDefinitions.TrendInfo> trends)
    {
        if (!Main.Settings.CombineWeaponDamageBonuses || trends == null || trends.Count == 0)
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
                x.sourceType == RuleDefinitions.FeatureSourceType.Equipment &&
                (string.IsNullOrEmpty(x.sourceName)
                    ? OwnFlatDamage(AttackWeapon) == own.value
                    : items.TryGetElement(x.sourceName, out var weapon) && OwnFlatDamage(weapon) == own.value));

            if (j >= 0)
            {
                var result = new List<RuleDefinitions.TrendInfo>(trends);
                // TrendInfo is a struct: this is a copy, and the game's entry keeps its value
                var combined = trends[j];

                combined.value += own.value;

                if (combined.value == 0)
                {
                    result.RemoveAt(Math.Max(i, j));
                    result.RemoveAt(Math.Min(i, j));
                }
                else
                {
                    result[j] = combined;
                    result.RemoveAt(i);
                }

                // one weapon, one own bonus: nothing else to look for
                return result;
            }

            if (AttackWeapon && OwnFlatDamage(AttackWeapon) == own.value)
            {
                var result = new List<RuleDefinitions.TrendInfo>(trends);
                var renamed = own;

                renamed.sourceType = RuleDefinitions.FeatureSourceType.Equipment;
                renamed.sourceName = AttackWeapon.Name;
                result[i] = renamed;

                return result;
            }
        }

        return trends;
    }

    // the formatter changed a breakdown: keep both texts for the effect line that is about to show it
    internal static void NoteCombinedBreakdown(string shortText, string fullText)
    {
        _pendingShort = shortText;
        _pendingFull = fullText;
    }

    // FeatureElementEffectLine.Bind has put a breakdown in its tooltip. If it is the one just combined, the
    // tooltip keeps both texts; a tooltip reused for another breakdown forgets what it had.
    internal static void NoteEffectLineBound(GuiTooltip tooltip, string damageBreakdown)
    {
        if (!tooltip)
        {
            return;
        }

        Breakdowns.Remove(tooltip);

        if (_pendingShort != null && damageBreakdown == _pendingShort)
        {
            Breakdowns.Add(tooltip, new Breakdown { Short = _pendingShort, Full = _pendingFull });
            tooltip.Content = WithHint(_pendingShort);
        }

        _pendingShort = null;
        _pendingFull = null;
    }

    // TooltipScreen.ShowTooltip is about to show a tooltip. For a combined breakdown, the full one is shown when
    // the game would show a detailed tooltip: with Alt held (or the controller's hold button), turned round by the
    // mod's "invert tooltip behaviour" setting, the same rule TooltipScreen and TooltipPanelPatcher apply.
    internal static void BeforeShowTooltip(ITooltip tooltip)
    {
        if (tooltip is not GuiTooltip guiTooltip || !Breakdowns.TryGetValue(guiTooltip, out var breakdown))
        {
            return;
        }

        var detailed = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) ||
                       TooltipFeatureHoldInfo.ConsoleHeld;

        if (SettingsContext.GuiModManagerInstance.InvertTooltipBehavior)
        {
            detailed = !detailed;
        }

        guiTooltip.Content = detailed ? breakdown.Full : WithHint(breakdown.Short);
    }

    // the short breakdown, with a line saying how to see the rest
    private static string WithHint(string shortText)
    {
        var hint = Gui.Localize(SettingsContext.GuiModManagerInstance.InvertTooltipBehavior
            ? "UI/&BreakdownFullHintInverted"
            : "UI/&BreakdownFullHint");

        return $"{shortText}\n{hint}";
    }

    // Gui.FormatTrendInfo printed nothing for an entry: what to print instead. In order: the object the entry was
    // made from (an item, a definition, a character); for an item entry, the weapon of the attack being shown;
    // otherwise the kind of source in words ("+1: Item").
    internal static string NameOfUnnamed(RuleDefinitions.TrendInfo trend)
    {
        var fromSource = trend.source switch
        {
            RulesetItem item => TitleOf(item.ItemDefinition),
            BaseDefinition definition => TitleOf(definition),
            RulesetEntity entity => entity.Name,
            _ => null
        };

        if (!string.IsNullOrEmpty(fromSource))
        {
            return fromSource;
        }

        if (trend.sourceType == RuleDefinitions.FeatureSourceType.Equipment && AttackWeapon)
        {
            var weapon = TitleOf(AttackWeapon);

            if (!string.IsNullOrEmpty(weapon))
            {
                return weapon;
            }
        }

        return Gui.Localize(trend.sourceType switch
        {
            RuleDefinitions.FeatureSourceType.Equipment => "UI/&TrendSourceItem",
            RuleDefinitions.FeatureSourceType.Condition => "UI/&TrendSourceCondition",
            RuleDefinitions.FeatureSourceType.Spell or RuleDefinitions.FeatureSourceType.KnownSpell =>
                "UI/&TrendSourceSpell",
            RuleDefinitions.FeatureSourceType.Power => "UI/&TrendSourcePower",
            RuleDefinitions.FeatureSourceType.Feat => "UI/&TrendSourceFeat",
            RuleDefinitions.FeatureSourceType.FightingStyle => "UI/&TrendSourceFightingStyle",
            RuleDefinitions.FeatureSourceType.Invocation => "UI/&TrendSourceInvocation",
            RuleDefinitions.FeatureSourceType.Metamagic => "UI/&TrendSourceMetamagic",
            RuleDefinitions.FeatureSourceType.Proficiency or RuleDefinitions.FeatureSourceType.MissingProficiency =>
                "UI/&TrendSourceProficiency",
            RuleDefinitions.FeatureSourceType.AbilityScore => "UI/&TrendSourceAbilityScore",
            RuleDefinitions.FeatureSourceType.Lighting => "UI/&TrendSourceLighting",
            RuleDefinitions.FeatureSourceType.Cover => "UI/&TrendSourceCover",
            RuleDefinitions.FeatureSourceType.CharacterFeature or RuleDefinitions.FeatureSourceType.MonsterFeature
                or RuleDefinitions.FeatureSourceType.ExplicitFeature => "UI/&TrendSourceFeature",
            _ => "UI/&TrendSourceOther"
        });
    }

    private static string TitleOf(BaseDefinition definition)
    {
        var title = definition?.GuiPresentation?.Title;

        return string.IsNullOrEmpty(title) ? null : Gui.Localize(title);
    }

    private sealed class Breakdown
    {
        internal string Full;
        internal string Short;
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

            // The game ends Bind by switching off this whole section when it shows nothing: no bonus row and no
            // weapon header (Reach or Range), which is every melee weapon without the reach or thrown tag. A
            // custom weapon with "+2 typed" and no enhancement then had its row switched on inside a section
            // that stayed off, so nothing showed. The section is shown when a row is, and hidden the way the
            // game hides it when the custom bonus leaves no row and there is no header (+1 enhancement, -1 typed).
            var statsShown = __instance.statsGroup && __instance.statsGroup.gameObject.activeSelf;
            var anyShown = wanted || hitWanted || statsShown;

            if (__instance.gameObject.activeSelf != anyShown)
            {
                __instance.gameObject.SetActive(anyShown);
            }

            if (!anyShown)
            {
                return;
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

    //PATCH: the weapon of the attack whose box is filled, for the breakdown's entries that don't name it (the
    //inventory's Main Attack box and the attack boxes of the character screens)
    [HarmonyPatch(typeof(AttackModeBox), nameof(AttackModeBox.Bind), typeof(RulesetAttackMode))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AttackModeBox_Bind
    {
        [UsedImplicitly]
        public static void Prefix(RulesetAttackMode attackMode)
        {
            WeaponDamageDisplay.AttackWeapon = attackMode?.SourceDefinition as ItemDefinition ??
                                               (attackMode?.SourceObject as RulesetItem)?.ItemDefinition;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            WeaponDamageDisplay.AttackWeapon = null;

            return __exception;
        }
    }

    //PATCH: the effect line has put a damage breakdown in its tooltip: a combined one keeps the full text too.
    //Bind has two overloads (this one, and Bind(string specialFormsDescription)), so the arguments are named:
    //without them Harmony can't tell which is meant, and the mod's loader skips a patch it can't resolve, which
    //would leave the full breakdown with no way to see it.
    [HarmonyPatch(typeof(FeatureElementEffectLine), nameof(FeatureElementEffectLine.Bind),
        [
            typeof(EffectForm), typeof(bool), typeof(int), typeof(bool), typeof(bool), typeof(Gui.VersatilityDisplay),
            typeof(int), typeof(string), typeof(RuleDefinitions.EffectApplication)
        ],
        [
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal,
            ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal
        ])]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FeatureElementEffectLine_Bind
    {
        [UsedImplicitly]
        public static void Postfix(FeatureElementEffectLine __instance, string damageBreakdown)
        {
            WeaponDamageDisplay.NoteEffectLineBound(__instance.tooltip, damageBreakdown);
        }
    }

    //PATCH: a combined damage breakdown shows in full when the tooltip opens with Alt held (the game's detailed
    //tooltip rule)
    [HarmonyPatch(typeof(TooltipScreen), nameof(TooltipScreen.ShowTooltip))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class TooltipScreen_ShowTooltip
    {
        [UsedImplicitly]
        public static void Prefix(ITooltip tooltip)
        {
            WeaponDamageDisplay.BeforeShowTooltip(tooltip);
        }
    }

    //PATCH: a breakdown entry that would print no source (as "+1:") names it: the object it came from, the weapon of
    //the attack shown, or the kind of source. Every breakdown goes through Gui.FormatTrendInfo, so this covers all
    //of them, not only damage.
    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatTrendInfo))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Gui_FormatTrendInfo
    {
        [UsedImplicitly]
        public static void Postfix(RuleDefinitions.TrendInfo trend, ref string __result)
        {
            if (string.IsNullOrEmpty(__result))
            {
                __result = WeaponDamageDisplay.NameOfUnnamed(trend);
            }
        }
    }
}
