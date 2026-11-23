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

// Weapon damage display for Dungeon Maker custom weapons. Display only: nothing here is used when damage is rolled.
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
// The breakdown (2) is changed so that the two entries become one, under the weapon's name, and
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

    // The item instance of that attack, while its box is filled: whether an item is identified belongs to the
    // instance (RulesetItem.Identified), not to its definition. Null elsewhere.
    internal static RulesetItem AttackItem;

    // The item instance of the attack being rolled (RulesetCharacter.RollAttackMode), while it is. The combat log
    // writes the roll from inside it (RollAttack raises AttackRolled, which GameConsole.AttackRolled handles), and
    // the log's entry names only the weapon's definition, so this is how the entry is tied to the copy that
    // attacked. Null elsewhere.
    internal static RulesetItem RollItem;

    // the weapon of the attack being shown or rolled, for an entry that doesn't name its item
    private static ItemDefinition WeaponOfAttack => AttackWeapon ? AttackWeapon : RollItem?.ItemDefinition;

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

    // The breakdown with a weapon's own damage bonus folded into the weapon's line. RulesetCharacterHero's
    // RefreshAttackMode adds the form's own bonus (the number typed into a custom weapon) as (value, Base, "", null)
    // (IL_0535 to IL_0550), then the enhancement of the weapon's item as (value, Equipment, item name, null), also
    // when the enhancement is 0 (IL_0583 to IL_05da). That is the list's first Equipment entry: the entries of
    // features and ammunition come after it.
    //
    // Only that first Equipment entry is taken as the weapon's, and only when it is the weapon's own (WeaponOf): its
    // name finds the weapon of the attack being shown (AttackWeapon) in the ItemDefinition database, or any weapon
    // when that is unknown; with no name, it is AttackWeapon's. The Base entry is folded in only when that weapon's
    // own damage bonus is exactly the Base value.
    //
    // With AttackWeapon known, that ties the Equipment entry to the attack's weapon. With AttackWeapon null (a
    // breakdown shown outside AttackModeBox.Bind, or an attack mode with no item definition), the first Equipment
    // entry counts only when its name resolves to a weapon; an unnamed or unresolved first entry means no fold, and
    // later Equipment entries are not looked at. The Base entry itself is only recognised by its type and value in
    // either case: a Base entry from something else, anywhere in the list, with the same value as the weapon's own
    // bonus would be folded or renamed too. No list the game builds is known to have that shape: RefreshAttackMode
    // writes FeatureSourceType.Base only for the weapon's own bonus (IL_0543); its other entries take the ability
    // score, the item, or each feature's origin as their source type.
    //   - Both there: one entry where the enhancement was, worth the sum, under the weapon's name ("+3: Base" and
    //     "+1: Excalibur" become "+4: Excalibur"); none when the sum is 0 (+1 with -1 typed in).
    //   - No weapon entry, but the attack's weapon is known and its own bonus is the Base value: the Base entry is
    //     shown under the weapon's name instead ("+2: Base" becomes "+2: <weapon>"). Its sourceName is the
    //     weapon's definition name, which Gui.FormatTrendInfo looks up in the ItemDefinition database (where a
    //     campaign's custom items are registered when it loads) to print the weapon's title, as it does for the
    //     enhancement entry.
    //   - Otherwise the Base entry can't be tied to a weapon and stays.
    // The game's own list is not changed (the attack mode keeps it); a changed copy is returned, or the same list
    // when nothing changes, which is how the caller knows whether to offer the full one on Alt.
    internal static List<RuleDefinitions.TrendInfo> WithWeaponBonusCombined(List<RuleDefinitions.TrendInfo> trends)
    {
        if (trends == null || trends.Count == 0)
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

            var j = trends.FindIndex(x => x.sourceType == RuleDefinitions.FeatureSourceType.Equipment);

            if (j >= 0 && OwnFlatDamage(WeaponOf(trends[j], items)) == own.value)
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

    // The weapon an Equipment entry is for: with a name, the item the ItemDefinition database has under it (the same
    // lookup Gui.FormatTrendInfo makes), and only if it is the weapon of the attack being shown when that is known;
    // without a name, the weapon of the attack being shown. Null otherwise.
    private static ItemDefinition WeaponOf(RuleDefinitions.TrendInfo trend, Database<ItemDefinition> items)
    {
        if (string.IsNullOrEmpty(trend.sourceName))
        {
            return AttackWeapon;
        }

        if (!items.TryGetElement(trend.sourceName, out var weapon))
        {
            return null;
        }

        return !AttackWeapon || weapon == AttackWeapon ? weapon : null;
    }

    // the formatter changed a breakdown: keep both texts for the effect line that is about to show it
    internal static void NoteCombinedBreakdown(string shortText, string fullText)
    {
        _pendingShort = shortText;
        _pendingFull = fullText;
    }

    // FeatureElementEffectLine.Bind has put a breakdown in its tooltip. If it is the one just combined, the
    // tooltip keeps both texts (it already shows the combined one); a tooltip reused for another breakdown forgets
    // what it had.
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
        }

        _pendingShort = null;
        _pendingFull = null;
    }

    // TooltipScreen.ShowTooltip is about to show a tooltip. For a combined breakdown, the full one is shown when
    // the game would show a detailed tooltip: with Alt held (or the controller's hold button), turned round by the
    // mod's "invert tooltip behaviour" setting, the same rule TooltipScreen and TooltipPanelPatcher apply. Nothing
    // in the tooltip says so, as the game's own detailed tooltips don't either.
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

        guiTooltip.Content = detailed ? breakdown.Full : breakdown.Short;
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

        if (trend.sourceType == RuleDefinitions.FeatureSourceType.Equipment && WeaponOfAttack)
        {
            var weapon = TitleOf(WeaponOfAttack);

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

    // The name a breakdown entry prints for an item that isn't identified yet: the definition's unidentified title
    // ("Magnificent Longsword"), localized as GuiEquipmentItem.get_Title localizes it (IL_0037 to IL_004f), so the
    // breakdown names the item as the inventory and the attack box do.
    // A definition with no unidentified title is named by its item type instead, "Unidentified <type>"
    // (UI/&UnidentifiedItemFormat; ItemTypeOf).
    internal static string UnidentifiedNameOf(ItemDefinition definition)
    {
        var title = definition?.ItemPresentation?.UnidentifiedTitle;

        if (!string.IsNullOrEmpty(title))
        {
            return Gui.Localize(title);
        }

        var type = ItemTypeOf(definition);

        return string.IsNullOrEmpty(type) ? "???" : Gui.Format("UI/&UnidentifiedItemFormat", type);
    }

    // An item's type, as the player can tell it without identifying the item: the most specific name its definition
    // records, in this order (the first that gives one wins):
    //   1. a weapon: its weapon type ("Longsword"), the WeaponTypeDefinition's title;
    //   2. armour, shields included: its armour type ("Chain Mail", "Shield"), the ArmorTypeDefinition's title;
    //   3. ammunition: its ammunition type ("Arrow"), the AmmunitionTypeDefinition's title;
    //   4. a tool: its tool type ("Thieves' Tools"), the ToolTypeDefinition's title;
    //   5. a musical instrument: "Musical Instrument" (Tooltip/&TagMusicalInstrumentTitle). Not the instrument type:
    //      most of the game's instrument types have no text of their own (only the Lyre's title is translated);
    //   6. a usable device tagged as a scroll, potion or poison: "Scroll", "Potion" (the game's
    //      Equipment/&ItemType...Title) or "Poison" (Tooltip/&TagPoisonTitle);
    //   7. an item worn on a finger: "Ring" (Equipment/&ItemTypeRingTitle), the game's own word, though it records
    //      rings by their slot alone;
    //   8. the type line the game's own tooltip shows under the name of an unidentified item
    //      (GuiEquipmentItem.get_Subtitle, IL_0007 to IL_0034, GuiItemDefinition.SubtitleWithoutRarity, which the
    //      mod writes in GuiItemTweaks.SubTitleWithoutRarity): "Spell Focus (Arcane)", "Wondrous Item" for items the
    //      game marks so, "Container", "Starter Pack", "Light Source", "Spellbook";
    //   9. any other item that can be worn or held (a slot besides the container slot), not a document: "Wondrous
    //      Item" (Equipment/&ItemTypeWondrousTitle), as amulets, cloaks, belts, bracers and the like record no type.
    // Empty otherwise (documents, gems, wealth, food and items of a kind added by other code); the entry then
    // prints "???".
    private static string ItemTypeOf(ItemDefinition definition)
    {
        if (!definition)
        {
            return null;
        }

        var typeTitle = TypeDefinitionTitleOf(definition);

        if (!string.IsNullOrEmpty(typeTitle))
        {
            return Gui.Localize(typeTitle);
        }

        var typeLine = new GuiItemDefinition(definition).SubtitleWithoutRarity;

        if (!string.IsNullOrEmpty(typeLine))
        {
            return typeLine;
        }

        var worn = !definition.IsDocument &&
                   definition.SlotTypes != null &&
                   definition.SlotTypes.Any(x => x != EquipmentDefinitions.SlotTypeContainer);

        return worn ? Gui.Localize("Equipment/&ItemTypeWondrousTitle") : null;
    }

    // the title key of steps 1 to 7 of ItemTypeOf, or null when none applies
    private static string TypeDefinitionTitleOf(ItemDefinition definition)
    {
        if (definition.IsWeapon)
        {
            return definition.WeaponDescription?.WeaponTypeDefinition?.GuiPresentation?.Title;
        }

        if (definition.IsArmor)
        {
            return definition.ArmorDescription?.ArmorTypeDefinition?.GuiPresentation?.Title;
        }

        if (definition.IsAmmunition)
        {
            return TypeTitleOf<AmmunitionTypeDefinition>(definition.AmmunitionDescription?.AmmunitionType);
        }

        if (definition.IsTool)
        {
            return TypeTitleOf<ToolTypeDefinition>(definition.ToolDescription?.ToolType);
        }

        if (definition.IsMusicalInstrument)
        {
            return "Tooltip/&TagMusicalInstrumentTitle";
        }

        var tags = definition.IsUsableDevice ? definition.UsableDeviceDescription?.UsableDeviceTags : null;

        if (tags != null)
        {
            if (tags.Contains(TagsDefinitions.UsableDeviceTagScroll))
            {
                return "Equipment/&ItemTypeScrollTitle";
            }

            if (tags.Contains(GameConstants.TagPotion))
            {
                return "Equipment/&ItemTypePotionTitle";
            }

            if (tags.Contains(GameConstants.TagPoison))
            {
                return "Tooltip/&TagPoisonTitle";
            }
        }

        return definition.SlotTypes != null && definition.SlotTypes.Contains(EquipmentDefinitions.SlotTypeFinger)
            ? "Equipment/&ItemTypeRingTitle"
            : null;
    }

    // the title key of a type definition found by name, or null
    private static string TypeTitleOf<T>(string name) where T : BaseDefinition
    {
        return !string.IsNullOrEmpty(name) && DatabaseRepository.GetDatabase<T>().TryGetElement(name, out var type)
            ? type.GuiPresentation?.Title
            : null;
    }

    // The character whose worn items an entry being formatted comes from, while it is known: the owner of the stat
    // box being refreshed (CharacterStatBox.Refresh), or the attacker of a roll the combat log is writing
    // (GameConsole.AttackRolled). Null elsewhere.
    internal static RulesetCharacter Carrier;

    // which character each attribute shown in a stat panel belongs to: a stat box knows only its attribute, and
    // refreshes itself whenever the attribute changes, not only when its panel binds it
    private static readonly ConditionalWeakTable<RulesetAttribute, RulesetCharacter> AttributeOwners = new();

    // reused for inventory lookups, which only run while a tooltip text is built (main thread)
    private static readonly List<RulesetItem> ItemsScratch = [];

    // a stat panel (CharacterStatsPanel, ActiveCharacterPanel) is about to bind this character's attributes
    internal static void NoteAttributeOwner(RulesetCharacter character)
    {
        if (character?.Attributes == null)
        {
            return;
        }

        foreach (var attribute in character.Attributes.Values.Where(x => x != null))
        {
            AttributeOwners.Remove(attribute);
            AttributeOwners.Add(attribute, character);
        }
    }

    // the character a stat box's attribute belongs to, if a stat panel bound it
    internal static RulesetCharacter OwnerOf(RulesetAttribute attribute)
    {
        return attribute != null && AttributeOwners.TryGetValue(attribute, out var owner) ? owner : null;
    }

    // Whether a breakdown entry names an item the player hasn't identified, and which definition it names. The
    // game's own entries name the item by its definition's real title (Gui.FormatTrendInfo looks the definition up
    // by name), on to-hit, damage and stat breakdowns (armour class) and the combat log's rolls alike, so this is
    // checked for every entry.
    //
    // An item counts as unidentified by the test the game uses to show its unidentified title ("Magnificent
    // Longsword"): the definition is magical, requires identification, and the item instance isn't identified
    // (GuiEquipmentItem.get_Title, IL_0007 to IL_0054; RulesetItemExtensions.NeedsIdentification is the same test).
    // Identification belongs to the instance, and two copies of one definition can differ (a campaign's loot can
    // hand out a copy already identified), so the instance that gave the bonus is looked for:
    //   - the entry's own source, when it is an item;
    //   - the item of the attack whose box is being filled (AttackItem), or of the attack being rolled (RollItem),
    //     when it is of that definition: an attack's entries are about that one copy;
    //   - the copies of that definition the Carrier wears: its inventory slots without containers and without the
    //     stored weapon sets (EnumerateAllItems with ignoreConfigSlots, IL_002a to IL_0053), as RefreshArmorClass
    //     counts only worn items;
    //   - with no character known, the copies the party carries, containers included.
    // From the copies found, the name stays only when every one is identified; with none found, the item counts as
    // unidentified, so nothing reveals an item the player hasn't identified. An armour class entry has no copy of its
    // own: RefreshArmorClass keeps one entry per definition (bonusByItem, IL_041b to IL_0461), by name with no
    // source, so every worn copy of that definition (two rings) is checked, and the real name shows only when all of
    // them are identified.
    internal static bool NamesUnidentifiedItem(RuleDefinitions.TrendInfo trend, out ItemDefinition definition)
    {
        var item = trend.source as RulesetItem;

        definition = item?.ItemDefinition;

        if (!definition && trend.sourceType == RuleDefinitions.FeatureSourceType.Equipment)
        {
            definition = string.IsNullOrEmpty(trend.sourceName)
                ? WeaponOfAttack
                : DatabaseRepository.GetDatabase<ItemDefinition>().TryGetElement(trend.sourceName, out var named)
                    ? named
                    : null;
        }

        if (!NeedsIdentification(definition))
        {
            return false;
        }

        if (item == null && AttackItem != null && AttackItem.ItemDefinition == definition)
        {
            item = AttackItem;
        }

        if (item == null && RollItem != null && RollItem.ItemDefinition == definition)
        {
            item = RollItem;
        }

        if (item != null)
        {
            return !item.Identified;
        }

        var worn = Carrier != null;
        IEnumerable<RulesetCharacter> carriers = worn
            ? [Carrier]
            : Gui.GameCampaign
                ? Gui.GameCampaign.Party?.CharactersList?.Select(x => (RulesetCharacter)x.RulesetCharacter) ?? []
                : [];
        var found = false;
        // an out parameter can't be used inside the lambda below
        var wanted = definition;

        foreach (var carrier in carriers.Where(x => x?.CharacterInventory != null))
        {
            ItemsScratch.Clear();
            carrier.CharacterInventory.EnumerateAllItems(ItemsScratch, !worn, worn);

            foreach (var copy in ItemsScratch.Where(x => x.ItemDefinition == wanted))
            {
                if (!copy.Identified)
                {
                    ItemsScratch.Clear();

                    return true;
                }

                found = true;
            }
        }

        ItemsScratch.Clear();

        return !found;
    }

    // a definition whose items can be unidentified (the first part of the game's test)
    private static bool NeedsIdentification(ItemDefinition definition)
    {
        return definition && definition.Magical && definition.RequiresIdentification;
    }

    // Whether a stat box's list names an item that can be unidentified. Its tooltip is then formatted again on every
    // refresh: CharacterStatBox.Refresh only formats it when the value or the number of entries changed
    // (IL_00e8 to IL_011b), and identifying an item changes neither, so the tooltip would keep the unidentified
    // name. A tooltip already on screen keeps its text until it is shown again: GuiTooltip.Content only stores it,
    // and the tooltip panel copies it when bound, which GuiManager.UpdateTooltip skips while the hover is unchanged.
    internal static bool NamesItemNeedingIdentification(RulesetAttribute attribute)
    {
        var trends = attribute?.ValueTrends;

        if (trends == null)
        {
            return false;
        }

        var items = DatabaseRepository.GetDatabase<ItemDefinition>();

        return trends.Any(x =>
            x.source is RulesetItem item
                ? NeedsIdentification(item.ItemDefinition)
                : x.sourceType == RuleDefinitions.FeatureSourceType.Equipment &&
                  !string.IsNullOrEmpty(x.sourceName) &&
                  items.TryGetElement(x.sourceName, out var definition) && NeedsIdentification(definition));
    }

    // Gui.FormatTrendInfo's text for an entry naming an unidentified item, built as the game builds it with the
    // item's unidentified name in place of its real title (IL_0000 to IL_0083 for the character's name, IL_02bd to
    // IL_02e8 for the item branch, IL_05bc to IL_05e4 for the details): an entry whose source is a character reads
    // "<character> uses <unidentified name>" (Tooltip/&SourceUsesItemFormat), any other the unidentified name alone,
    // and the entry's details follow as they would.
    internal static string UnidentifiedEntryText(RuleDefinitions.TrendInfo trend, ItemDefinition definition)
    {
        var name = UnidentifiedNameOf(definition);
        var text = name;
        var actor = trend.source is RulesetCharacter character ? NameOf(character) : null;

        if (!string.IsNullOrEmpty(actor))
        {
            text = Gui.Format("Tooltip/&SourceUsesItemFormat", actor, name);
        }

        if (!string.IsNullOrEmpty(trend.additionalDetails))
        {
            text += " " + Gui.Localize(trend.additionalDetails);
        }

        return text;
    }

    // a character's name as Gui.FormatTrendInfo prints it (IL_000d to IL_0083)
    private static string NameOf(RulesetCharacter character)
    {
        if (character is not RulesetCharacterMonster monster)
        {
            return character.Name;
        }

        if (monster.OriginalFormCharacter != null)
        {
            return monster.OriginalFormCharacter.Name;
        }

        return ServiceRepository.GetService<IGuiWrapperService>()
            .GetGuiMonsterDefinition(monster.MonsterDefinition.Name)
            .GetDisplayName(monster.HumanoidMonsterPresentationDefinition, false);
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
            WeaponDamageDisplay.AttackItem = attackMode?.SourceObject as RulesetItem;
            WeaponDamageDisplay.AttackWeapon = attackMode?.SourceDefinition as ItemDefinition ??
                                               WeaponDamageDisplay.AttackItem?.ItemDefinition;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            WeaponDamageDisplay.AttackWeapon = null;
            WeaponDamageDisplay.AttackItem = null;

            return __exception;
        }
    }

    //PATCH: the stat panels name the character whose attributes their stat boxes show, so an armour class entry
    //naming an item can be checked against that character's own copies of it
    [HarmonyPatch(typeof(CharacterStatsPanel), nameof(CharacterStatsPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterStatsPanel_Bind
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacterHero hero)
        {
            WeaponDamageDisplay.NoteAttributeOwner(hero);
        }
    }

    [HarmonyPatch(typeof(ActiveCharacterPanel), nameof(ActiveCharacterPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ActiveCharacterPanel_Bind
    {
        [UsedImplicitly]
        public static void Prefix(GuiCharacter guiCharacter)
        {
            WeaponDamageDisplay.NoteAttributeOwner(guiCharacter?.RulesetCharacter);
        }
    }

    //PATCH: the item of the attack being rolled, for the combat log's entry of that roll (written while it runs).
    //A separate patch from RulesetCharacterPatcher.RollAttackMode_Patch, which serves other features.
    [HarmonyPatch(typeof(RulesetCharacter), nameof(RulesetCharacter.RollAttackMode))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RulesetCharacter_RollAttackMode
    {
        [UsedImplicitly]
        public static void Prefix(RulesetAttackMode attackMode)
        {
            WeaponDamageDisplay.RollItem = attackMode?.SourceObject as RulesetItem;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            WeaponDamageDisplay.RollItem = null;

            return __exception;
        }
    }

    //PATCH: while the combat log writes an attack roll (and formats its breakdown), the attacker, whose worn items
    //the roll's item entries come from; the method of an attack made with a weapon is that weapon's definition
    [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.AttackRolled))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameConsole_AttackRolled
    {
        [UsedImplicitly]
        public static void Prefix(RulesetActor attacker, BaseDefinition attackMethodDefinition)
        {
            WeaponDamageDisplay.Carrier = attacker as RulesetCharacter;
            WeaponDamageDisplay.AttackWeapon = attackMethodDefinition as ItemDefinition;
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            WeaponDamageDisplay.Carrier = null;
            WeaponDamageDisplay.AttackWeapon = null;

            return __exception;
        }
    }

    //PATCH: while a stat box refreshes (and formats its breakdown tooltip), the character it belongs to
    [HarmonyPatch(typeof(CharacterStatBox), nameof(CharacterStatBox.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterStatBox_Refresh
    {
        [UsedImplicitly]
        public static void Prefix(CharacterStatBox __instance)
        {
            WeaponDamageDisplay.Carrier = WeaponDamageDisplay.OwnerOf(__instance.smartAttribute);

            if (WeaponDamageDisplay.NamesItemNeedingIdentification(__instance.smartAttribute))
            {
                __instance.previousTrendListCount = -1;
            }
        }

        [UsedImplicitly]
        public static Exception Finalizer(Exception __exception)
        {
            WeaponDamageDisplay.Carrier = null;

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
    //the attack shown, or the kind of source. An entry naming an item that isn't identified yet prints the item's
    //unidentified name (the prefix). Every breakdown goes through Gui.FormatTrendInfo, so this covers all of them,
    //not only damage.
    [HarmonyPatch(typeof(Gui), nameof(Gui.FormatTrendInfo))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Gui_FormatTrendInfo
    {
        // an entry naming an unidentified item gets the game's text with the item's unidentified name, and the
        // game's formatter, which would print the item's real title, doesn't run
        [UsedImplicitly]
        public static bool Prefix(RuleDefinitions.TrendInfo trend, ref string __result)
        {
            if (!WeaponDamageDisplay.NamesUnidentifiedItem(trend, out var definition))
            {
                return true;
            }

            __result = WeaponDamageDisplay.UnidentifiedEntryText(trend, definition);

            return false;
        }

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
