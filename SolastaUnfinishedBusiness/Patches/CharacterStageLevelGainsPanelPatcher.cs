using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageLevelGainPanelPatcher
{
    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel), nameof(CharacterStageLevelGainsPanel.EnterStage))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EnterStage_Patch
    {
        // ReSharper disable once UnusedMember.Global
        public static void GetLastAssignedClassAndLevel(
            ICharacterBuildingService characterBuildingService,
            RulesetCharacterHero hero,
            out CharacterClassDefinition lastClassDefinition,
            out int level)
        {
            if (LevelUpHelper.IsLevelingUp(hero))
            {
                //PATCH: mark we are beyond selecting classes (MULTICLASS)
                LevelUpHelper.SetIsClassSelectionStage(hero, false);

                //PATCH: gets my own class and level for level up (MULTICLASS)
                lastClassDefinition = LevelUpHelper.GetSelectedClass(hero);
                level = hero.ClassesHistory.Count;
            }
            else
            {
                characterBuildingService.GetLastAssignedClassAndLevel(hero, out lastClassDefinition, out level);
            }
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var getLastAssignedClassAndLevelMethod =
                typeof(ICharacterBuildingService).GetMethod("GetLastAssignedClassAndLevel");
            var customGetLastAssignedClassAndLevelMethod =
                typeof(EnterStage_Patch).GetMethod("GetLastAssignedClassAndLevel");

            return instructions.ReplaceCalls(getLastAssignedClassAndLevelMethod,
                "CharacterStageLevelGainPanel.EnterStage",
                new CodeInstruction(OpCodes.Call, customGetLastAssignedClassAndLevelMethod));
        }
    }

    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel),
        nameof(CharacterStageLevelGainsPanel.RefreshSpellcastingFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshSpellcastingFeatures_Patch
    {
        //PATCH: only displays spell casting features from the current class (MULTICLASS)
        private static List<RulesetSpellRepertoire> SpellRepertoires(
            [NotNull] RulesetCharacterHero rulesetCharacterHero)
        {
            if (LevelUpHelper.IsLevelingUp(rulesetCharacterHero) && LevelUpHelper.IsMulticlass(rulesetCharacterHero))
            {
                return rulesetCharacterHero.SpellRepertoires
                    .Where(x => LevelUpHelper.IsRepertoireFromSelectedClassSubclass(rulesetCharacterHero, x))
                    .ToList();
            }

            return rulesetCharacterHero.SpellRepertoires;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var spellRepertoiresMethod = typeof(RulesetCharacter).GetMethod("get_SpellRepertoires");
            var filteredSpellRepertoiresMethod =
                new Func<RulesetCharacterHero, List<RulesetSpellRepertoire>>(SpellRepertoires).Method;

            return instructions.ReplaceCalls(spellRepertoiresMethod,
                "CharacterStageLevelGainPanel.RefreshSpellcastingFeatures",
                new CodeInstruction(OpCodes.Call, filteredSpellRepertoiresMethod));
        }
    }

    //PATCH: shows the slot gains of the repertoire's own casting mode (SPELL_POINTS)
    // The game reads the gained slots per spell level from the shared definition table, which always holds the
    // ordinary slot table. A hero casting with spell points has its own capacity table, so the read takes the
    // repertoire (the method's first argument) and gets that hero's table instead.
    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel),
        nameof(CharacterStageLevelGainsPanel.FillSpellcastingGains))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillSpellcastingGains_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var slotsPerLevels = AccessTools.PropertyGetter(
                typeof(FeatureDefinitionCastSpell), nameof(FeatureDefinitionCastSpell.SlotsPerLevels));
            var capacityTable = AccessTools.Method(
                typeof(SpellPointsContext), nameof(SpellPointsContext.GetCapacityTable));

            return instructions.ReplaceCalls(slotsPerLevels,
                "CharacterStageLevelGainPanel.FillSpellcastingGains",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, capacityTable));
        }
    }
}
