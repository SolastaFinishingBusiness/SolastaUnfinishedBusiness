using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellSelectionPanelPatcher
{
    [HarmonyPatch(typeof(SpellSelectionPanel), nameof(SpellSelectionPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Prefix(
            SpellSelectionPanel __instance,
            GuiCharacter caster,
            ref bool cantripOnly,
            ActionDefinitions.ActionType actionType)
        {
            //PATCH: takes down the previous binding when the panel is bound again without an Unbind in between.
            //The game's Bind only clears spellRepertoireLines and binds the pooled lines again, and each
            //SpellRepertoireLine.Bind adds a RepertoireRefreshed handler to its spell list, so a line would keep
            //a handler on the list it was bound to before. Unbinding here, while the lines still know their
            //lists, removes those handlers; the extra row tables go with their lines. After the panel's own
            //Unbind the list is empty and this does nothing.
            foreach (var spellRepertoireLine in __instance.spellRepertoireLines)
            {
                spellRepertoireLine.Unbind();
            }

            __instance.spellRepertoireLines.Clear();
            CampaignsContext.SpellSelectionPanelRowsUnbind();

            //PATCH: supports `IReplaceAttackWithCantrip`
            var gameLocationCaster = caster.GameLocationCharacter;

            if (gameLocationCaster.RulesetCharacter.HasSubFeatureOfType<IAttackReplaceWithCantrip>()
                && gameLocationCaster.UsedMainAttacks > 0 && actionType == ActionDefinitions.ActionType.Main)
            {
                cantripOnly = true;
            }

            ActionSwitching.CheckSpellcastingCantrips(caster.GameLocationCharacter, actionType, ref cantripOnly);
        }

        [UsedImplicitly]
        public static void Postfix(
            SpellSelectionPanel __instance,
            GuiCharacter caster,
            SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
            ActionDefinitions.ActionType actionType,
            bool cantripOnly)
        {
            //PATCH: lays out the panel's spell rows from what each spell list offers on it (empty lines left out,
            //every offered level shown, the Warlock's pact slots on its list's first line), with or without
            //multi-line spell offering
            CampaignsContext.SpellSelectionPanelRowsBind(
                __instance, caster, spellCastEngaged, actionType, cantripOnly);
        }

        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: the game's Bind only visits the spell lists the panel shows. It binds a line for every list
            //it is given, and SpellRepertoireLine.Bind hands the list's own KnownCantrips to
            //FindAndSortRelevantSpells, whose prefix in this mod removes spells from the list it gets; a hidden
            //spell list must not be changed by a panel that never shows it.
            var getRepertoires = typeof(RulesetCharacter).GetMethod("get_SpellRepertoires");
            var getPanelRepertoires =
                new Func<RulesetCharacter, List<RulesetSpellRepertoire>>(CampaignsContext.GetPanelRepertoires).Method;

            return instructions.ReplaceCalls(getRepertoires, "SpellSelectionPanel.Bind",
                new CodeInstruction(OpCodes.Call, getPanelRepertoires));
        }
    }

    [HarmonyPatch(typeof(SpellSelectionPanel), nameof(SpellSelectionPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Unbind_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            CampaignsContext.SpellSelectionPanelRowsUnbind();
        }
    }
}
