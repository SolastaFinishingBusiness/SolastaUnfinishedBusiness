using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterStageClassSelectionPanelPatcher
{
    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel), nameof(CharacterStageClassSelectionPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] CharacterStageClassSelectionPanel __instance)
        {
            //PATCH: avoids a restart when enabling / disabling classes on the Mod UI panel
            var visibleClasses = DatabaseRepository.GetDatabase<CharacterClassDefinition>()
                .Where(x => !x.GuiPresentation.Hidden)
                .OrderBy(x => x.FormatTitle());

            __instance.compatibleClasses.SetRange(visibleClasses);

            if (!LevelUpHelper.IsLevelingUp(__instance.currentHero))
            {
                return;
            }

            //PATCH: mark we started selecting classes (MULTICLASS)
            LevelUpHelper.SetIsClassSelectionStage(__instance.currentHero, true);

            //PATCH: apply in/out logic (MULTICLASS)
            MulticlassInOutRulesHelper.EnumerateHeroAllowedClassDefinitions(
                __instance.currentHero,
                __instance.compatibleClasses,
                out __instance.selectedClass);

            //PATCH: refresh the panel (MULTICLASS)
            var commonData = __instance.CommonData;

            // NOTE: don't use AttackModesPanel?. which bypasses Unity object lifetime check
            if (commonData.AttackModesPanel)
            {
                commonData.AttackModesPanel.RefreshNow();
            }

            // NOTE: don't use PersonalityMapPanel?. which bypasses Unity object lifetime check
            if (commonData.PersonalityMapPanel)
            {
                commonData.PersonalityMapPanel.RefreshNow();
            }
        }

        //PATCH: draws the class tiles' bottom lines and corner marks for the grid's real column count. The game's
        //OnBeginShow works them out for two columns (index / 2, index % 2), but ClassesTable lays the 326 px tiles out
        //three to a 980 px row, so with a class count that leaves a short last row the frames land on the wrong tiles.
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageClassSelectionPanel __instance)
        {
            var table = __instance.classesTable;

            if (!table)
            {
                return;
            }

            var columns = 1;
            var grid = table.GetComponent<GridLayoutGroup>();

            if (grid)
            {
                columns = grid.constraint == GridLayoutGroup.Constraint.FixedColumnCount
                    ? grid.constraintCount
                    : Mathf.FloorToInt((table.rect.width + grid.spacing.x) / (grid.cellSize.x + grid.spacing.x));
            }

            columns = Math.Max(1, columns);

            var slots = new List<GridSelectionSlot>();

            for (var i = 0; i < table.childCount; i++)
            {
                var child = table.GetChild(i);

                if (child.gameObject.activeSelf && child.TryGetComponent<GridSelectionSlot>(out var slot))
                {
                    slots.Add(slot);
                }
            }

            for (var i = 0; i < slots.Count; i++)
            {
                // a bottom line where no tile is below; a corner mark on every inner corner (not the first row or
                // column)
                if (slots[i].bottomLine)
                {
                    slots[i].bottomLine.gameObject.SetActive(i + columns >= slots.Count);
                }

                if (slots[i].crossHair)
                {
                    slots[i].crossHair.gameObject.SetActive(i % columns > 0 && i / columns > 0);
                }
            }
        }
    }

    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel),
        nameof(CharacterStageClassSelectionPanel.RefreshCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshCharacter_Patch
    {
        private static void ResetCantripsPool(RulesetCharacterHero hero, string poolName)
        {
            var buildingData = hero.GetHeroBuildingData();

            if (buildingData.PointPoolStacks.TryGetValue(HeroDefinitions.PointsPoolType.Cantrip, out var pointPool))
            {
                pointPool.ActivePools.Remove(poolName);
            }
        }

        [UsedImplicitly]
        public static void Prefix(CharacterStageClassSelectionPanel __instance)
        {
            var hero = __instance.currentHero;

            //PATCH: avoid Druid Primal Order to break level up with the cantrip pool it gets
            ResetCantripsPool(hero, $"{AttributeDefinitions.TagClass}Druid1PrimalOrder");
        }
    }

    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel),
        nameof(CharacterStageClassSelectionPanel.FillClassFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillClassFeatures_Patch
    {
        //PATCH: hides the features list for already acquired classes (MULTICLASS)
        private static int Level(
            [NotNull] FeatureUnlockByLevel featureUnlockByLevel,
            [NotNull] RulesetCharacterHero hero)
        {
            var isLevelingUp = LevelUpHelper.IsLevelingUp(hero);
            var selectedClass = LevelUpHelper.GetSelectedClass(hero);

            if (!isLevelingUp)
            {
                return featureUnlockByLevel.Level;
            }

            var levels = 0;

            if (selectedClass
                && hero.ClassesAndLevels.TryGetValue(selectedClass, out levels)
                && featureUnlockByLevel.Level != levels + 1)
            {
                return int.MaxValue;
            }

            if (levels == 0)
            {
                return featureUnlockByLevel.Level;
            }

            return featureUnlockByLevel.Level - 1;
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var levelMethod = typeof(FeatureUnlockByLevel).GetMethod("get_Level");
            var myLevelMethod = new Func<FeatureUnlockByLevel, RulesetCharacterHero, int>(Level).Method;
            var currentHeroField =
                typeof(CharacterStageClassSelectionPanel).GetField("currentHero",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return instructions.ReplaceCalls(levelMethod, "CharacterStageClassSelectionPanel.FillClassFeatures",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, currentHeroField),
                new CodeInstruction(OpCodes.Call, myLevelMethod));
        }
    }

    //PATCH: hides the equipment panel group (MULTICLASS)
    [HarmonyPatch(typeof(CharacterStageClassSelectionPanel), nameof(CharacterStageClassSelectionPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        private static bool SetActive([NotNull] RulesetCharacterHero currentHero)
        {
            return !LevelUpHelper.IsLevelingUp(currentHero);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var setActiveMethod = typeof(GameObject).GetMethod("SetActive");
            var mySetActiveMethod = new Func<RulesetCharacterHero, bool>(SetActive).Method;
            var currentHeroField =
                typeof(CharacterStageClassSelectionPanel).GetField("currentHero",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            return instructions.ReplaceCall(setActiveMethod,
                4, "CharacterStageClassSelectionPanel.Refresh",
                new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, currentHeroField),
                new CodeInstruction(OpCodes.Call, mySetActiveMethod),
                new CodeInstruction(OpCodes.Call, setActiveMethod)); // checked for Call vs CallVirtual
        }
    }
}
