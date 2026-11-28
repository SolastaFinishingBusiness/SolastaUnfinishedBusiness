using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Subclasses;
using UnityEngine.UI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class SpellRepertoireLinePatcher
{
    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Bind_Patch
    {
        [UsedImplicitly]
        public static void Postfix(SpellRepertoireLine __instance)
        {
            //PATCH: a line that shows its slot box is at least as tall as that box. The line's height comes from
            //its HorizontalLayoutGroup, which only counts the table of level boxes: the header and the slot box
            //(uniqueLevelSlotsGroup) are left out of the layout (LayoutElement.ignoreLayout) and the group has no
            //vertical padding; Bind only makes room for the slot box sideways (the group's left padding). A line
            //with the slot box and no level box would so ask for no height, and the box, which stands on the
            //line's bottom edge, would cover the row above.
            //This runs after every Bind, also the one in SpellRepertoireLine.Refresh and the first one of a line
            //taken from the pool again, so the height always follows the line's current binding.
            var slotsGroup = __instance.uniqueLevelSlotsGroup;
            var layoutElement = __instance.GetComponent<LayoutElement>();

            if (!slotsGroup.gameObject.activeSelf)
            {
                // NOTE: don't use layoutElement?. which bypasses Unity object lifetime check
                if (layoutElement)
                {
                    // a negative value takes the element out of the height calculation
                    layoutElement.minHeight = -1;
                }

                return;
            }

            if (!layoutElement)
            {
                layoutElement = __instance.gameObject.AddComponent<LayoutElement>();
            }

            // the layout group has priority 0; at the same priority the larger minimum counts, so a line whose
            // level boxes are taller than the slot box keeps their height (a LayoutElement's default priority of
            // 1 would replace the group's minimum instead)
            layoutElement.layoutPriority = 0;
            layoutElement.minHeight = slotsGroup.rect.height;
        }
    }

    [HarmonyPatch(typeof(SpellRepertoireLine), nameof(SpellRepertoireLine.FindAndSortRelevantSpells))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FindAndSortRelevantSpells_Patch
    {
        [UsedImplicitly]
        public static void Prefix([NotNull] List<SpellDefinition> spellDefinitions)
        {
            //PATCH: hide reaction spells from spell panel
            spellDefinitions.RemoveAll(x => x.ActivationTime == ActivationTime.Reaction);
            //PATCH: hide smite spells from spell panel
            spellDefinitions.RemoveAll(x => x.ActivationTime == ActivationTime.OnAttackHit);
        }

        [UsedImplicitly]
        public static void Postfix([NotNull] List<SpellDefinition> spellDefinitions, SpellRepertoireLine __instance)
        {
            //PATCH: Enable Blast Reload feature
            var hero = __instance.caster.rulesetCharacter;

            hero?.GetSubFeaturesByType<PatronEldritchSurge.IQualifySpellToRepertoireLine>()
                .ForEach(f => f.QualifySpells(hero, __instance, spellDefinitions));
        }
    }
}
