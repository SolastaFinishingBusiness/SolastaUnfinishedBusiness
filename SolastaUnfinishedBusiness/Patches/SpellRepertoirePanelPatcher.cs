using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using static SolastaUnfinishedBusiness.Models.Level20Context;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellRepertoirePanelPatcher
{
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            //PATCH: filters how spells and slots are displayed on inspection (MULTICLASS)
            MulticlassGameUi.RebuildSlotsTable(__instance);

            //PATCH: displays sorcery point box for sorcerers only
            if (!Main.Settings.EnableDisplaySorceryPointBoxSorcererOnly)
            {
                return;
            }

            if (__instance.SpellRepertoire.SpellCastingClass != DatabaseHelper.CharacterClassDefinitions.Sorcerer)
            {
                __instance.sorceryPointsBox.gameObject.SetActive(false);
            }
        }
    }

    //PATCH: Supports Wizard Mastery and Signature spell features
    //UI allows other spells to be selected so easier to prevent it here
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnSpellSelectedForPreparation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnSpellSelectedForPreparation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance, SpellBox spellBox)
        {
            // nothing can be picked while this panel's Prepare is on its way (SpellPreparationScreen)
            if (SpellPreparationScreen.IsPendingOn(__instance))
            {
                return false;
            }

            var rulesetCharacter = __instance.GuiCharacter.RulesetCharacter;
            var spellDefinition = spellBox.SpellDefinition;

            return !Tabletop2024Context.IsInvalidMemorizeSelectedSpell(__instance, rulesetCharacter, spellDefinition) &&
                   !WizardSpellMastery.IsInvalidSelectedSpell(__instance.SpellRepertoire, spellDefinition) &&
                   !WizardSignatureSpells.IsInvalidSelectedSpell(__instance.SpellRepertoire, spellDefinition);
        }
    }

    //PATCH: in Memorize Spell, Spell Mastery or Signature Spells mode, Prepare sends the choice to every computer in one
    //mod command (Tabletop2024Context.TryPrepareMemorizeSpell, TaggedSpellPreparation.TryPrepare) instead of the game's
    //PrepareSpells command
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.DoValidate))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class DoValidate_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            // another Prepare of these features while one is on its way only warns (a shortcut gets here too)
            if (Tabletop2024Context.IsMemorizeSpellPreparation(__instance.GuiCharacter?.RulesetCharacter,
                    __instance.SpellRepertoire) || TaggedSpellPreparation.IsInMode(__instance))
            {
                if (SpellPreparationScreen.WarnIfPending())
                {
                    return false;
                }
            }

            return !Tabletop2024Context.TryPrepareMemorizeSpell(__instance) &&
                   !TaggedSpellPreparation.TryPrepare(__instance);
        }
    }

    //PATCH: while a Prepare of the mod's Wizard features is on its way, Prepare (with its too-few-spells question),
    //Revert, Clear and Auto do nothing on that panel (SpellPreparationScreen); the buttons are also off
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnValidatePreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnValidatePreparationCb_Patch
    {
        // before the game's too-few-spells question: another Prepare of these features while one is on its way only
        // warns that the previous changes haven't been synced yet (SpellPreparationScreen)
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            if (!Tabletop2024Context.IsMemorizeSpellPreparation(__instance.GuiCharacter?.RulesetCharacter,
                    __instance.SpellRepertoire) && !TaggedSpellPreparation.IsInMode(__instance))
            {
                return true;
            }

            return !SpellPreparationScreen.WarnIfPending();
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnClearPreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnClearPreparationCb_Patch
    {
        // Clear never runs in Memorize Spell mode either: it empties the list without the one-swap check, and the
        // controller's Clear button is shown there
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            return !SpellPreparationScreen.IsPendingOn(__instance) &&
                   !Tabletop2024Context.IsMemorizeSpellPreparation(__instance.GuiCharacter?.RulesetCharacter,
                       __instance.SpellRepertoire);
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnAutomatePreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnAutomatePreparationCb_Patch
    {
        // Auto never runs in Memorize Spell, Spell Mastery or Signature Spells mode: it prepares the repertoire's usual
        // spells, bypassing the mode's limits. The mouse button is moved off screen there (RepaintPanel), but the game
        // shows a separate controller button, which would reach it
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            return !SpellPreparationScreen.IsPendingOn(__instance) &&
                   !TaggedSpellPreparation.IsInMode(__instance) &&
                   !Tabletop2024Context.IsMemorizeSpellPreparation(__instance.GuiCharacter?.RulesetCharacter,
                       __instance.SpellRepertoire);
        }
    }

    //PATCH: in Spell Mastery or Signature Spells mode, the screen lists the current picks where the game lists the
    //prepared spells, when it opens and on Revert (only on this computer's screen; nothing shared changes)
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnPrepareSpellsCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnPrepareSpellsCb_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            SpellPreparationScreen.NewSession();
            TaggedSpellPreparation.ShowPicks(__instance);
        }
    }

    //PATCH: a cancelled preparation ends its session, so a late Prepare answer won't close another one
    //(SpellPreparationScreen)
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnCancelPreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnCancelPreparationCb_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            SpellPreparationScreen.NewSession();
        }
    }

    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.OnRevertPreparationCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnRevertPreparationCb_Patch
    {
        // Revert does nothing while this panel's Prepare is on its way (SpellPreparationScreen)
        [UsedImplicitly]
        public static bool Prefix(SpellRepertoirePanel __instance)
        {
            return !SpellPreparationScreen.IsPendingOn(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            if (!SpellPreparationScreen.IsPendingOn(__instance))
            {
                TaggedSpellPreparation.ShowPicks(__instance);
            }
        }
    }

    //PATCH: Supports Wizard Mastery and Signature spell features
    [HarmonyPatch(typeof(SpellRepertoirePanel), nameof(SpellRepertoirePanel.RefreshPreparation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshPreparation_Patch
    {
        //PATCH: in Spell Mastery or Signature Spells mode, Revert is enabled when the list differs from the picks the
        //screen opened with (TaggedSpellPreparation.RefreshRevert)
        [UsedImplicitly]
        public static void Postfix(SpellRepertoirePanel __instance)
        {
            TaggedSpellPreparation.RefreshRevert(__instance);

            // and every selection button off while this panel's Prepare is on its way (SpellPreparationScreen)
            SpellPreparationScreen.RefreshButtons(__instance);
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var refreshInteractivePreparationMethod =
                typeof(SpellsByLevelGroup).GetMethod("RefreshInteractivePreparation");
            var myRefreshInteractivePreparationMethod =
                new Action<SpellsByLevelGroup, bool, bool, List<SpellDefinition>, SpellRepertoirePanel>(
                    RefreshInteractivePreparation).Method;

            return instructions.ReplaceCalls(refreshInteractivePreparationMethod,
                "SpellRepertoirePanel.RefreshPreparation",
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, myRefreshInteractivePreparationMethod));
        }

        private static void RepaintPanel(
            SpellRepertoirePanel __instance,
            string title,
            bool showDesc, bool showAutoButton, bool showClearRevertButtons, string byPassInstruction = null)
        {
            var preparationPanelTransform = __instance.PreparationPanel.transform;
            var titleTransform = preparationPanelTransform.FindChildRecursive("Title");
            var descriptionTransform = preparationPanelTransform.FindChildRecursive("Description");
            var automateButtonTransform = preparationPanelTransform.FindChildRecursive("AutomateButton");
            var clearButtonTransform = preparationPanelTransform.FindChildRecursive("ClearButton");
            var revertButtonTransform = preparationPanelTransform.FindChildRecursive("RevertButton");
            var instructionTransform = preparationPanelTransform.FindChildRecursive("Instruction");

            titleTransform!.GetComponentInChildren<TextMeshProUGUI>().text = title;

            descriptionTransform!.gameObject.SetActive(showDesc);

            // not the best solution but this object is getting re-activated somewhere else so moving off-screen
            automateButtonTransform!.localPosition = showAutoButton
                ? new Vector3(-12.5f, -61)
                : new Vector3(-1000, -1000);

            clearButtonTransform!.gameObject.SetActive(showClearRevertButtons);
            revertButtonTransform!.gameObject.SetActive(showClearRevertButtons);

            if (byPassInstruction != null)
            {
                instructionTransform!.GetComponentInChildren<TextMeshProUGUI>().text = byPassInstruction;
            }
        }

        private static void RefreshInteractivePreparation(
            SpellsByLevelGroup spellsByLevelGroup,
            bool canSelectSpells,
            bool maxReached,
            List<SpellDefinition> preparedSpells,
            SpellRepertoirePanel spellRepertoirePanel)
        {
            var rulesetCharacter = spellRepertoirePanel.GuiCharacter.RulesetCharacter;

            if (Tabletop2024Context.IsMemorizeSpellPreparation(rulesetCharacter, spellRepertoirePanel.SpellRepertoire))
            {
                RepaintPanel(
                    spellRepertoirePanel, Tabletop2024Context.FeatureMemorizeSpell.FormatTitle(),
                    false, false, false,
                    Gui.Localize("Screen/&PreparePanelMemorizeSpellSelect"));
            }
            else if (WizardSpellMastery.IsPreparation(spellRepertoirePanel.SpellRepertoire, out _))
            {
                RepaintPanel(
                    spellRepertoirePanel, WizardSpellMastery.FeatureSpellMastery.FormatTitle(),
                    true, false, true);

                canSelectSpells = spellsByLevelGroup.SpellLevel is 1 or 2;
            }
            else if (WizardSignatureSpells.IsPreparation(spellRepertoirePanel.SpellRepertoire, out _))
            {
                RepaintPanel(
                    spellRepertoirePanel, WizardSignatureSpells.PowerSignatureSpells.FormatTitle(),
                    Main.Settings.EnableSignatureSpellsRelearn, false, true);

                canSelectSpells = spellsByLevelGroup.SpellLevel is 3;
            }
            else
            {
                RepaintPanel(
                    spellRepertoirePanel,
                    Gui.Localize("Screen/&PreparePanelTitle"),
                    true, true, true);
            }

            // no spell can be picked while this panel's Prepare is on its way (SpellPreparationScreen)
            if (SpellPreparationScreen.IsPendingOn(spellRepertoirePanel))
            {
                canSelectSpells = false;
            }

            spellsByLevelGroup.RefreshInteractivePreparation(canSelectSpells, maxReached, preparedSpells);
        }
    }
}
