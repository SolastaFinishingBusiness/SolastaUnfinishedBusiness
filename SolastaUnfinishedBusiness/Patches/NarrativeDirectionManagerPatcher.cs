using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeDirectionManagerPatcher
{
    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.PrepareDialogSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PrepareDialogSequence_Patch
    {
        [UsedImplicitly]
        public static void Prefix(List<GameLocationCharacter> involvedGameCharacters)
        {
            //PATCH: Don't offer controlled conjurations on dialogue sequences (FullyControlConjurations)
            if (Main.Settings.FullyControlConjurations)
            {
                involvedGameCharacters.RemoveAll(
                    x => x.RulesetCharacter is RulesetCharacterMonster rulesetCharacterMonster
                         && Tabletop2014Context.ConjuredMonsters.Contains(rulesetCharacterMonster
                             .MonsterDefinition));
            }

#if FB_DEVELOPMENT
            // Development builds only: play cutscenes with the first N party members, to test the cutscene placement with
            // parties of 2, 3 and 5 on saves that have 6 (MultiplayerContext.DevCutsceneHeroLimit, mod menu > Gameplay >
            // Campaigns; 0 = off). Only who appears in the scene changes, not the party or the save; roles of a smaller cast
            // are filled by the game's padding (NarrativeSequenceBuilderPatcher), as for a real small party. Single player
            // only: it changes who holds the scene's roles, and it isn't part of the settings the host sends, so in a
            // multiplayer room one computer could build a different cast.
            if (MultiplayerContext.DevCutsceneHeroLimit > 0 && !Global.IsMultiplayer)
            {
                var party = Gui.GameCampaign.Party.CharactersList.Select(x => x.RulesetCharacter).ToList();

                involvedGameCharacters.RemoveAll(
                    x => party.IndexOf(x.RulesetCharacter) >= MultiplayerContext.DevCutsceneHeroLimit);
            }
#endif

            //PATCH: Only offer the first 4 players on dialogue sequences (PARTYSIZE)
            if (Main.Settings.OverridePartySize <= ToolsContext.GamePartySize
                || Main.Settings.AllowAllPlayersOnNarrativeSequences)
            {
                return;
            }

            {
                var party = Gui.GameCampaign.Party.CharactersList
                    .Select(x => x.RulesetCharacter)
                    .ToList();

                involvedGameCharacters.RemoveAll(
                    x => party.IndexOf(x.RulesetCharacter) >= ToolsContext.GamePartySize);
            }
        }
    }

    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.PrepareCharacters))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PrepareCharacters_Patch
    {
        [UsedImplicitly]
        public static void Postfix(NarrativeSequence narrativeSequence)
        {
            //PATCH: places every hero past the 4th behind the 4 authored seats (PARTYSIZE)
            MultiplayerContext.PlaceExtraHeroes(narrativeSequence);
        }
    }

    //PATCH: EnableLogDialoguesToConsole
    [HarmonyPatch(typeof(NarrativeDirectionManager), nameof(NarrativeDirectionManager.StartDialogSequence))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartDialogSequence_Patch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            if (!Main.Settings.EnableLogDialoguesToConsole)
            {
                return;
            }

            var screen = Gui.GuiService.GetScreen<GuiConsoleScreen>();

            screen.Show();
        }
    }
}
