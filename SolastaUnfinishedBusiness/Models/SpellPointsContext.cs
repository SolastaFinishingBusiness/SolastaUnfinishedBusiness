using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Classes;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Subclasses;
using TMPro;
using UnityEngine;
using static FeatureDefinitionCastSpell;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Builders.Features.FeatureDefinitionCastSpellBuilder;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionCastSpells;

namespace SolastaUnfinishedBusiness.Models;

internal static class SpellPointsContext
{
    private const string ModeElement = "FbUsesSpellPoints";
    private sealed class HeroMode
    {
        internal bool Points;
    }

    // Heroes can be recreated by loading a character or receiving it from another peer.
    // Keep the live choice with its owner without retaining discarded character objects.
    private static readonly ConditionalWeakTable<RulesetCharacterHero, HeroMode> Modes = new();
    private static readonly ConditionalWeakTable<RulesetSpellRepertoire, HeroMode> RepertoireModes = new();

    internal static readonly List<int> SpellCostByLevel = [0, 2, 3, 5, 6, 7, 9, 10, 11, 13];

    internal static readonly List<SlotsByLevelDuplet> SpellPointsFullCastingSlots = [];
    internal static readonly List<SlotsByLevelDuplet> SpellPointsHalfCastingSlots = [];
    private static readonly List<SlotsByLevelDuplet> SpellPointsHalfRoundUpCastingSlots = [];
    private static readonly List<SlotsByLevelDuplet> SpellPointsOneThirdCastingSlots = [];

    private static readonly List<(string, List<SlotsByLevelDuplet>, List<SlotsByLevelDuplet>)>
        FeatureDefinitionCastSpellTab =
        [
            (CastSpellBard.Name, SharedSpellsContext.FullCastingSlots, SpellPointsFullCastingSlots),
            (CastSpellCleric.Name, SharedSpellsContext.FullCastingSlots, SpellPointsFullCastingSlots),
            (CastSpellDruid.Name, SharedSpellsContext.FullCastingSlots, SpellPointsFullCastingSlots),
            (CastSpellPaladin.Name, SharedSpellsContext.HalfCastingSlots, SpellPointsHalfCastingSlots),
            (CastSpellRanger.Name, SharedSpellsContext.HalfCastingSlots, SpellPointsHalfCastingSlots),
            (CastSpellSorcerer.Name, SharedSpellsContext.FullCastingSlots, SpellPointsFullCastingSlots),
            (CastSpellWizard.Name, SharedSpellsContext.FullCastingSlots, SpellPointsFullCastingSlots),
            (CastSpellMartialSpellBlade.Name, SharedSpellsContext.OneThirdCastingSlots,
                SpellPointsOneThirdCastingSlots),
            (CastSpellShadowcaster.Name, SharedSpellsContext.OneThirdCastingSlots,
                SpellPointsOneThirdCastingSlots),
            (InventorClass.SpellCasting.Name, SharedSpellsContext.HalfRoundUpCastingSlots,
                SpellPointsHalfRoundUpCastingSlots),
            (RoguishArcaneScoundrel.CastSpellName, SharedSpellsContext.OneThirdCastingSlots,
                SpellPointsOneThirdCastingSlots),
            (MartialSpellShield.CastSpellName, SharedSpellsContext.OneThirdCastingSlots,
                SpellPointsOneThirdCastingSlots)
        ];

    internal static readonly FeatureDefinitionPower PowerSpellPoints = FeatureDefinitionPowerBuilder
        .Create("PowerSpellPoints")
        .SetGuiPresentationNoContent(true)
        .SetUsesFixed(ActivationTime.NoCost, RechargeRate.LongRest, 1, 0)
        .AddCustomSubFeatures(
            HasModifiedUses.Marker,
            new ModifyPowerPoolAmountPowerSpellPoints())
        .AddToDB();

    internal static void LateLoad()
    {
        EnumerateSlotsPerLevel(CasterProgression.Full, SpellPointsFullCastingSlots, true);
        EnumerateSlotsPerLevel(CasterProgression.Half, SpellPointsHalfCastingSlots, true);
        EnumerateSlotsPerLevel(CasterProgression.HalfRoundUp, SpellPointsHalfRoundUpCastingSlots, true);
        EnumerateSlotsPerLevel(CasterProgression.OneThird, SpellPointsOneThirdCastingSlots, true);
        SwitchFeatureDefinitionCastSpellSlots();
    }

    internal static void SwitchFeatureDefinitionCastSpellSlots()
    {
        var db = DatabaseRepository.GetDatabase<FeatureDefinitionCastSpell>();

        foreach (var (name, slotsVanilla, _) in FeatureDefinitionCastSpellTab)
        {
            var featureCastSpell = db.GetElement(name);
            featureCastSpell.slotsPerLevels = UsesLevelOneProgression(name)
                ? SharedSpellsContext.HalfRoundUpCastingSlots
                : slotsVanilla;
        }
    }

    private static bool UsesLevelOneProgression(string name) =>
        (Main.Settings.EnablePaladinSpellCastingAtLevel1 && name == CastSpellPaladin.Name) ||
        (Main.Settings.EnableRangerSpellCastingAtLevel1 && name == CastSpellRanger.Name);

    // ComputeSpellSlots writes capacity counts and GetMaxSlotsNumberOfAllLevels sums those counts.
    // AutoPrepareSpells also uses the numerical capacities when distributing prepared spells.
    // The spell-learning methods only need the unlocked levels, identical in both tables.
    // Leave definitions shared and select a capacity table only for a repertoire with a known owner.
    internal static List<SlotsByLevelDuplet> GetCapacityTable(FeatureDefinitionCastSpell feature,
        RulesetSpellRepertoire repertoire)
    {
        if (RepertoireModes.TryGetValue(repertoire, out var mode) && mode.Points &&
            !UsesLevelOneProgression(feature.Name))
        {
            foreach (var (name, _, points) in FeatureDefinitionCastSpellTab)
            {
                if (feature.Name == name)
                {
                    return points;
                }
            }
        }

        return feature.SlotsPerLevels;
    }

    internal static void BindRepertoires(RulesetCharacterHero hero)
    {
        if (!Modes.TryGetValue(hero, out var mode))
        {
            return;
        }

        foreach (var repertoire in hero.SpellRepertoires)
        {
            RepertoireModes.Remove(repertoire);
            RepertoireModes.Add(repertoire, mode);
        }
    }

    internal static bool CanCastSpellOfLevel(RulesetCharacter rulesetCharacter, int level)
    {
        var remaining = GetRemainingSpellPoints(rulesetCharacter);

        return remaining >= SpellCostByLevel[level];
    }

    internal static bool IsSpellPointsEnabled(this RulesetCharacter character)
    {
        return character is RulesetCharacterHero hero && Modes.TryGetValue(hero, out var mode) && mode.Points;
    }

    // The stock object reader seeks to the end of the hero after reading its known elements.
    // An absent disk field identifies a legacy hero. In multiplayer the host loads the saved party
    // and resolves that choice before sharing it. Peers read the explicit mode from the network.
    internal static void SerializeMode(RulesetCharacterHero hero, IElementsSerializer serializer)
    {
        if (serializer is not BinarySerializer binary)
        {
            return;
        }

        var mode = Modes.GetOrCreateValue(hero);
        if (serializer.Mode == Serializer.SerializationMode.Read)
        {
            mode.Points = binary.persistentContext && !LevelDownContext.IsNextElement(binary, ModeElement)
                ? Main.Settings.UseAlternateSpellPointsSystem
                : serializer.SerializeElement(ModeElement, false);

            // Only drop the pool here. A points-mode hero missing its pool gets one in PostLoad
            // (InitializeMode), once the hero is complete enough to compute the pool's size.
            if (!mode.Points)
            {
                ApplyMode(hero, false);
            }
        }
        else
        {
            serializer.SerializeElement(ModeElement, mode.Points);
        }
    }

    internal static void InitializeMode(RulesetCharacterHero hero)
    {
        if (!Modes.TryGetValue(hero, out var mode))
        {
            mode = Modes.GetOrCreateValue(hero);
            mode.Points = MultiplayerSessionContext.WantedSpellPoints;
        }

        ApplyMode(hero, mode.Points);
    }

    // The campaign and location rest wrappers call the real recovery, before RestCompleted can
    // trigger an autosave. The character pool importer and the rest preview bypass both wrappers.
    internal static void FinishRest(RulesetCharacter character, RestType restType)
    {
        if (restType != RestType.LongRest || character is not RulesetCharacterHero hero ||
            hero.IsSpellPointsEnabled() == MultiplayerSessionContext.WantedSpellPoints)
        {
            return;
        }

        var mode = Modes.GetOrCreateValue(hero);
        mode.Points = MultiplayerSessionContext.WantedSpellPoints;
        ApplyMode(hero, mode.Points);
        hero.RefreshSpellRepertoires();
    }

    internal static int GetMaxSpellPoints(this RulesetCharacter rulesetCharacter)
    {
        var usablePower = PowerProvider.Get(PowerSpellPoints, rulesetCharacter);

        return rulesetCharacter.GetMaxUsesOfPower(usablePower);
    }

    internal static int GetRemainingSpellPoints(this RulesetCharacter rulesetCharacter)
    {
        var usablePower = PowerProvider.Get(PowerSpellPoints, rulesetCharacter);

        return rulesetCharacter.GetRemainingUsesOfPower(usablePower);
    }

    internal static void AddSpellPoints(this RulesetCharacter rulesetCharacter, int points)
    {
        var usablePower = PowerProvider.Get(PowerSpellPoints, rulesetCharacter);
        usablePower.remainingUses = Math.Min(usablePower.remainingUses + points, rulesetCharacter.GetMaxSpellPoints());
    }

    internal static void HideSpellSlots(RulesetCharacter character, RectTransform table)
    {
        if (!character.IsSpellPointsEnabled() ||
            (character is RulesetCharacterHero hero &&
             SharedSpellsContext.GetWarlockSpellRepertoire(hero) != null))
        {
            return;
        }

        for (var index = 0; index < table.childCount; ++index)
        {
            var component = table.GetChild(index).GetComponent<SlotStatus>();

            component.Used.gameObject.SetActive(false);
            component.Available.gameObject.SetActive(false);
        }
    }

    internal static void DisplayRemainingSpellPointsOnCastActions(GuiCharacterAction guiCharacterAction,
        RectTransform useSlotsTable, GuiLabel highSlotNumber, GuiLabel attackNumber)
    {
        if (!guiCharacterAction.ActingCharacter.RulesetCharacter.IsSpellPointsEnabled() || !guiCharacterAction.ActionDefinition.IsCastSpellAction())
        {
            return;
        }

        var rulesetCharacter = guiCharacterAction.ActingCharacter.RulesetCharacter;
        var remainingSpellPoints = GetRemainingSpellPoints(rulesetCharacter).ToString();

        if (!highSlotNumber)
        {
            attackNumber.Text = remainingSpellPoints;
            attackNumber.transform.parent.gameObject.SetActive(true);
            return;
        }

        highSlotNumber.gameObject.SetActive(true);
        useSlotsTable.gameObject.SetActive(false);
        highSlotNumber.Text = remainingSpellPoints;
        highSlotNumber.GuiTooltip.Content =
            Gui.Format("Screen/&SpellAlternatePointsTooltip", remainingSpellPoints);
    }

    // NPCs don't have an inspection screen so safe to have a hero here
    internal static void DisplayMaxSpellPointsOnInspectionScreen(
        CharacterInspectionScreen __instance, RulesetCharacterHero heroCharacter)
    {
        var maxSpellPoints = GetMaxSpellPoints(heroCharacter).ToString();

        for (var i = 0; i < __instance.spellPanelsContainer.childCount; i++)
        {
            var child = __instance.spellPanelsContainer.GetChild(i);
            var repertoireTitle = child.GetChild(1).GetChild(1).GetComponent<TextMeshProUGUI>();
            var spellRepertoire = __instance.InspectedCharacter.RulesetCharacterHero.SpellRepertoires[i];

            if (heroCharacter.IsSpellPointsEnabled() &&
                (SharedSpellsContext.IsMulticaster(heroCharacter) ||
                 SharedSpellsContext.GetWarlockSpellRepertoire(heroCharacter) == null) &&
                spellRepertoire.SpellCastingFeature.SpellCastingOrigin
                    is CastingOrigin.Class
                    or CastingOrigin.Subclass)
            {
                var postfix = Gui.Format("Screen/&SpellAlternatePointsCostTooltip", maxSpellPoints);

                repertoireTitle.text = Gui.Localize("Screen/&RepertoireSpellsTitle") + ": " + postfix;
            }
            else
            {
                repertoireTitle.text = Gui.Localize("Screen/&RepertoireSpellsTitle");
            }
        }
    }

    internal static void DisplayCostOnSpellLevelBlocks(
        SlotStatusTable slotStatusTable, SlotStatus slotStatus, int slotLevel, int spellsAtLevel)
    {
        var cost = SpellCostByLevel[slotLevel].ToString();

        slotStatus.Used.gameObject.SetActive(false);
        slotStatus.Available.gameObject.SetActive(false);
        slotStatusTable.slotsText.gameObject.SetActive(true);
        slotStatusTable.slotsText.Text =
            spellsAtLevel < 2 ? cost : Gui.Format("Screen/&SpellAlternatePointsCostTooltip", cost);
    }

    private static void ApplyMode(RulesetCharacterHero hero, bool points)
    {
        BindRepertoires(hero);
        // A legacy save can contain either or both references. Preserve the deserialized instance
        // (including zero remaining uses) and initialize a rested pool only if it is absent.
        if (!points)
        {
            foreach (var features in hero.ActiveFeatures.Values)
            {
                features.RemoveAll(x => x == PowerSpellPoints);
            }

            hero.UsablePowers.RemoveAll(x => x.PowerDefinition == PowerSpellPoints);
            return;
        }

        if (!hero.ActiveFeatures.TryGetValue(AttributeDefinitions.TagRace, out var raceFeatures))
        {
            raceFeatures = [];
            hero.ActiveFeatures.Add(AttributeDefinitions.TagRace, raceFeatures);
        }

        if (!raceFeatures.Contains(PowerSpellPoints))
        {
            raceFeatures.Add(PowerSpellPoints);
        }

        if (hero.UsablePowers.All(x => x.PowerDefinition != PowerSpellPoints))
        {
            var usablePower = PowerProvider.Get(PowerSpellPoints, hero);
            usablePower.remainingUses = hero.GetMaxUsesOfPower(usablePower);
            hero.UsablePowers.Add(usablePower);
        }
    }

    internal static void ConsumeSlotsAtLevelsPointsCannotCastAnymore(
        RulesetCharacter character,
        RulesetSpellRepertoire repertoire,
        int slotLevel, bool consume = true, bool isMulticaster = false)
    {
        // consume points
        var usablePower = PowerProvider.Get(PowerSpellPoints, character);

        if (consume)
        {
            var cost = SpellCostByLevel[slotLevel];

            usablePower.remainingUses -= cost;
        }

        // NPCs cannot be multicasters so try to get a hero here
        var hero = character as RulesetCharacterHero;

        // consume spell slots at levels points cannot cast anymore
        var level = isMulticaster
            ? SharedSpellsContext.GetSharedSpellLevel(hero)
            : repertoire.MaxSpellLevelOfSpellCastingLevel;

        for (var i = level; i > 0; i--)
        {
            if (usablePower.RemainingUses >= SpellCostByLevel[i])
            {
                if (slotLevel > 5)
                {
                    ConsumeSlot(slotLevel);
                }

                continue;
            }

            ConsumeSlot(i);
        }

        repertoire.RepertoireRefreshed?.Invoke(repertoire);

        return;

        void ConsumeSlot(int slot)
        {
            var warlockLevel = SharedSpellsContext.GetWarlockSpellLevel(hero);
            var usedWarlockSlots = 0;

            if (level == warlockLevel &&
                repertoire.usedSpellsSlots.TryGetValue(SharedSpellsContext.PactMagicSlotsTab, out var usedSlots))
            {
                usedWarlockSlots = usedSlots;
            }

            var usedSpellsSlots = repertoire.usedSpellsSlots;

            usedSpellsSlots.TryAdd(slot, 0);
            usedSpellsSlots[slot] = usedWarlockSlots + 1;
        }
    }

    internal static void ConvertAdditionalSlotsIntoSpellPointsBeforeRefreshSpellRepertoire(RulesetCharacter character)
    {
        var usablePower = PowerProvider.Get(PowerSpellPoints, character);

        // need ToArray to avoid enumerator issues with RemoveCondition
        foreach (var activeCondition in character.ConditionsByCategory
                     .SelectMany(x => x.Value)
                     .ToArray())
        {
            var removeCondition = false;

            foreach (var magicAffinity in activeCondition.ConditionDefinition.Features
                         .OfType<FeatureDefinitionMagicAffinity>()
                         .Where(x => x.AdditionalSlots.Count > 0))
            {
                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
                foreach (var additionalSlot in magicAffinity.AdditionalSlots)
                {
                    var slotCount = additionalSlot.SlotsNumber;
                    var slotLevel = additionalSlot.SlotLevel;
                    var totalPoints = slotCount * SpellCostByLevel[slotLevel];

                    usablePower.remainingUses += totalPoints;

                    removeCondition = true;
                }
            }

            if (removeCondition)
            {
                character.RemoveCondition(activeCondition);
            }
        }
    }

    internal static void RefreshActionPanelAfterFlexibleCastingItem()
    {
        if (!Main.Settings.UseAlternateSpellPointsSystem)
        {
            return;
        }

        var gameLocationScreenExploration = Gui.GuiService.GetScreen<GameLocationScreenExploration>();

        if (gameLocationScreenExploration.Visible)
        {
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var panel in gameLocationScreenExploration.CharacterControlPanel.ActionPanels)
            {
                foreach (var characterActionItem in
                         panel.actionItems.Where(x =>
                             x.CurrentItemForm.GuiCharacterAction.ActionDefinition.IsCastSpellAction()))
                {
                    characterActionItem.CurrentItemForm.Refresh();
                }
            }

            return;
        }

        var gameLocationScreenBattle = Gui.GuiService.GetScreen<GameLocationScreenBattle>();

        // ReSharper disable once InvertIf
        if (gameLocationScreenBattle.Visible)
        {
            // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
            foreach (var panel in gameLocationScreenBattle.CharacterControlPanel.ActionPanels)
            {
                foreach (var characterActionItem in
                         panel.actionItems.Where(x =>
                             x.CurrentItemForm.GuiCharacterAction.ActionDefinition.IsCastSpellAction()))
                {
                    characterActionItem.CurrentItemForm.Refresh();
                }
            }
        }
    }

    private sealed class ModifyPowerPoolAmountPowerSpellPoints : IModifyPowerPoolAmount
    {
        private static readonly List<int> SpellPointsByLevel =
        [
            0, 4, 6, 14, 17,
            // 5
            27, 32, 38, 44, 57,
            // 10
            64, 73, 73, 83, 83,
            // 15
            94, 94, 107, 114, 123,
            // 20
            133
        ];

        public FeatureDefinitionPower PowerPool => PowerSpellPoints;

        public int PoolChangeAmount(RulesetCharacter character)
        {
            switch (character)
            {
                case RulesetCharacterHero hero:
                {
                    var casterLevel = GetCasterLevel(hero);

                    return SpellPointsByLevel[casterLevel];
                }
                case RulesetCharacterMonster monster when
                    monster.SpellRepertoires.Count > 0:

                    return SpellPointsByLevel[monster.SpellRepertoires[0].MaxSpellLevelOfSpellCastingLevel];
                default:
                    return 0;
            }
        }

        private static int GetCasterLevel(RulesetCharacterHero hero)
        {
            if (SharedSpellsContext.IsMulticaster(hero))
            {
                return SharedSpellsContext.GetSharedCasterLevel(hero);
            }

            var spellRepertoire = hero.SpellRepertoires.FirstOrDefault(x =>
                x.SpellCastingFeature.SpellCastingOrigin
                    is CastingOrigin.Class
                    or CastingOrigin.Subclass);

            if (spellRepertoire == null)
            {
                return 0;
            }

            var characterLevel = hero.TryGetAttributeValue(AttributeDefinitions.CharacterLevel);
            var casterType = SharedSpellsContext.GetCasterTypeForClassOrSubclass(
                spellRepertoire.SpellCastingClass?.Name, spellRepertoire.SpellCastingSubclass?.Name);

            return casterType switch
            {
                CasterProgression.Full => characterLevel,
                CasterProgression.Half when characterLevel <= 1 => 0,
                CasterProgression.Half => (characterLevel + 1) / 2,
                CasterProgression.HalfRoundUp => (characterLevel + 1) / 2,
                CasterProgression.OneThird => (characterLevel + 2) / 3,
                _ => 0
            };
        }
    }
}
