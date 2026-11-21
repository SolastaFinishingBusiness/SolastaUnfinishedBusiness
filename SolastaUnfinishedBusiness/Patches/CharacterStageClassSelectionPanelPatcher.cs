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
        //A tile's prefab has a top, left and bottom frame but no right one (the next tile's left frame or the panel's
        //edge draws it), so the last tile of a short row gets a right frame of its own.
        [UsedImplicitly]
        public static void Postfix([NotNull] CharacterStageClassSelectionPanel __instance)
        {
            var table = __instance.classesTable;

            if (!table)
            {
                return;
            }

            var slots = new List<GridSelectionSlot>();

            for (var i = 0; i < table.childCount; i++)
            {
                var child = table.GetChild(i);

                if (child.gameObject.activeSelf && child.TryGetComponent<GridSelectionSlot>(out var slot))
                {
                    slots.Add(slot);
                }
            }

            var columns = GetColumnCount(table, slots.Count);

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

                // the last tile, when it isn't in the last column, has an empty cell to its right
                SetRightFrame(slots[i], i == slots.Count - 1 && i % columns != columns - 1);
            }
        }

        // a copy of the tile's left frame, moved to its right edge; made once per tile and only shown or hidden after
        // that, as the panel reuses its tiles
        private static void SetRightFrame(GridSelectionSlot slot, bool show)
        {
            var frameRight = slot.transform.Find(FrameRightName);

            if (!frameRight)
            {
                if (!show || slot.transform.Find("FrameLeft") is not RectTransform frameLeft)
                {
                    return;
                }

                var copy = UnityEngine.Object.Instantiate(frameLeft.gameObject, slot.transform);
                var rect = (RectTransform)copy.transform;

                copy.name = FrameRightName;
                copy.transform.SetSiblingIndex(frameLeft.GetSiblingIndex() + 1);
                rect.anchorMin = new Vector2(1f, frameLeft.anchorMin.y);
                rect.anchorMax = new Vector2(1f, frameLeft.anchorMax.y);
                rect.pivot = new Vector2(1f, frameLeft.pivot.y);
                rect.anchoredPosition = new Vector2(-frameLeft.anchoredPosition.x, frameLeft.anchoredPosition.y);
                rect.sizeDelta = frameLeft.sizeDelta;
                frameRight = copy.transform;
            }

            frameRight.gameObject.SetActive(show);
        }

        private const string FrameRightName = "FrameRight";

        // The column count GridLayoutGroup lays the tiles out in (its SetCellsAlongAxis): a fixed column count, the
        // children over a fixed row count rounded up, or as many cells as fit in the width inside the padding.
        private static int GetColumnCount(RectTransform table, int childCount)
        {
            var grid = table.GetComponent<GridLayoutGroup>();

            if (!grid)
            {
                return 1;
            }

            int columns;

            switch (grid.constraint)
            {
                case GridLayoutGroup.Constraint.FixedColumnCount:
                    columns = grid.constraintCount;
                    break;
                case GridLayoutGroup.Constraint.FixedRowCount:
                    columns = Mathf.CeilToInt(childCount / (float)grid.constraintCount - 0.001f);
                    break;
                default:
                    var width = table.rect.width;

                    columns = grid.cellSize.x + grid.spacing.x <= 0
                        ? int.MaxValue
                        : Mathf.FloorToInt(
                            (width - grid.padding.horizontal + grid.spacing.x + 0.001f) /
                            (grid.cellSize.x + grid.spacing.x));
                    break;
            }

            // never more columns than tiles: a flexible grid with no horizontal pitch lays every tile out in one row
            return Math.Max(1, Math.Min(columns, childCount));
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
