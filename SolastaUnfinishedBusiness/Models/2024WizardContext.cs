using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSubclassChoices;

namespace SolastaUnfinishedBusiness.Models;

public static partial class Tabletop2024Context
{
    private static readonly FeatureDefinitionPointPool PointPoolWizardScholar = FeatureDefinitionPointPoolBuilder
        .Create("PointPoolWizardScholar")
        .SetGuiPresentation(Category.Feature)
        .SetPool(HeroDefinitions.PointsPoolType.Expertise, 1)
        .RestrictChoices(
            SkillDefinitions.Arcana,
            SkillDefinitions.History,
            SkillDefinitions.Investigation,
            SkillDefinitions.Medecine,
            SkillDefinitions.Nature,
            SkillDefinitions.Religion)
        .AddToDB();

    internal static readonly FeatureDefinition FeatureMemorizeSpell = FeatureDefinitionBuilder
        .Create("FeatureWizardMemorizeSpell")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    private static readonly RestActivityDefinition RestActivityMemorizeSpell = RestActivityDefinitionBuilder
        .Create("RestActivityMemorizeSpell")
        .SetGuiPresentation("FeatureWizardMemorizeSpell", Category.Feature)
        .SetRestData(
            RestDefinitions.RestStage.AfterRest,
            RestType.ShortRest,
            RestActivityDefinition.ActivityCondition.CanPrepareSpells,
            nameof(FunctorMemorizeSpell),
            string.Empty)
        .AddToDB();

    private static readonly ConditionDefinition ConditionMemorizeSpell = ConditionDefinitionBuilder
        .Create("ConditionMemorizeSpell")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .SetFixedAmount(0)
        .AddToDB();


    internal static void SwitchWizardSchoolOfMagicLearningLevel()
    {
        var schools = DatabaseRepository.GetDatabase<CharacterSubclassDefinition>()
            .Where(x =>
                SubclassChoiceWizardArcaneTraditions.Subclasses.Contains(x.Name) ||
                x.Name.StartsWith(WizardClass))
            .ToList();

        var fromLevel = 3;
        var toLevel = 2;

        if (Main.Settings.EnableWizardToLearnSchoolAtLevel3)
        {
            fromLevel = 2;
            toLevel = 3;
        }

        foreach (var featureUnlock in schools
                     .SelectMany(school => school.FeatureUnlocks
                         .Where(featureUnlock => featureUnlock.level == fromLevel)))
        {
            featureUnlock.level = toLevel;
        }

        // change spell casting level on Wizard itself
        Wizard.FeatureUnlocks
                .FirstOrDefault(x =>
                    x.FeatureDefinition == SubclassChoiceWizardArcaneTraditions)!
                .level =
            toLevel;

        foreach (var school in schools)
        {
            school.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
        }

        Wizard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static void SwitchWizardScholar()
    {
        Wizard.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == PointPoolWizardScholar);

        if (Main.Settings.EnableWizardToLearnScholarAtLevel2)
        {
            Wizard.FeatureUnlocks.Add(new FeatureUnlockByLevel(PointPoolWizardScholar, 2));
        }

        Wizard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    internal static bool IsRestActivityMemorizeSpellAvailable(
        RestActivityDefinition activity, RulesetCharacterHero hero)
    {
        return activity != RestActivityMemorizeSpell ||
               (Main.Settings.EnableWizardMemorizeSpell2024 && hero.GetClassLevel(Wizard) >= 5);
    }

    private static bool TryGetMemorizeSpellCondition(RulesetCharacter character, out RulesetCondition condition)
    {
        return character.TryGetConditionOfCategoryAndType(
            AttributeDefinitions.TagEffect, ConditionMemorizeSpell.Name, out condition);
    }

    // Memorize Spell is a Wizard feature: its mode only applies to the Wizard repertoire, so a multiclass hero's other
    // prepared repertoire (a Cleric's) prepares as usual
    private static bool IsWizardRepertoire(RulesetSpellRepertoire repertoire)
    {
        return repertoire?.SpellCastingClass == Wizard;
    }

    internal static bool IsMemorizeSpellPreparation(RulesetCharacter character, RulesetSpellRepertoire repertoire)
    {
        return IsWizardRepertoire(repertoire) && TryGetMemorizeSpellCondition(character, out _);
    }

    // The one swap Memorize Spell allows is recorded in the condition, as upstream did: Amount (1 once a spell was
    // unselected) and SourceProficiencyBonus (the spell last unselected or selected, by SpellKey; -1 before either).
    // The condition lasts until the next rest and is saved with the game, so reopening Memorize Spell or loading a
    // save can't grant a second swap. A click changes only this computer's screen session below, and the record is
    // written on every computer together with the chosen spells when Prepare is pressed (TryPrepareMemorizeSpell);
    // closing without it changes nothing.
    private static MemorizeSession _memorizeSession;

    private sealed class MemorizeSession(ulong conditionGuid, int amount, int spellIndex)
    {
        internal readonly ulong ConditionGuid = conditionGuid;
        internal int Amount = amount;
        internal int SpellIndex = spellIndex;
    }

    // the screen session for this condition, started from the committed record
    private static MemorizeSession GetMemorizeSession(RulesetCondition condition)
    {
        if (_memorizeSession == null || _memorizeSession.ConditionGuid != condition.Guid)
        {
            _memorizeSession = new MemorizeSession(condition.Guid, condition.Amount, condition.SourceProficiencyBonus);
        }

        return _memorizeSession;
    }

    // The spell in the swap record, the same on every computer and in every language. Older builds stored the spell's
    // position in SpellsContext.Spells, which is sorted by translated title, so a host in another language loading the
    // save, or taking over the hero, would read it as another spell. This is an FNV-1a hash of
    // the spell's internal name, always at least 2^30, so a position saved by an older build (a few hundred at most)
    // never equals one. Such a record, still active in a save from before this build, blocks unselecting any spell,
    // the swapped one included, until the next rest ends it. It isn't translated: it's a
    // position in a list sorted in the language that saved it, which isn't known here.
    private static int SpellKey(SpellDefinition spell)
    {
        var hash = 2166136261u;

        foreach (var c in spell.Name)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)((hash & 0x3FFFFFFFu) | 0x40000000u);
    }

    internal static bool IsInvalidMemorizeSelectedSpell(
        SpellRepertoirePanel spellRepertoirePanel, RulesetCharacter rulesetCharacter, SpellDefinition spell)
    {
        if (!IsWizardRepertoire(spellRepertoirePanel.SpellRepertoire) ||
            !TryGetMemorizeSpellCondition(rulesetCharacter, out var activeCondition))
        {
            return false;
        }

        var session = GetMemorizeSession(activeCondition);
        var spellIndex = SpellKey(spell);
        var isUncheck = spellRepertoirePanel.preparedSpells.Contains(spell);

        if (isUncheck)
        {
            if (session.SpellIndex != -1 && session.SpellIndex != spellIndex)
            {
                return true;
            }

            session.Amount = 1;
            session.SpellIndex = spellIndex;

            return false;
        }

        session.Amount = 0;
        session.SpellIndex = spellIndex;

        return false;
    }

    // The preparation screen's Prepare button in Memorize Spell mode (SpellRepertoirePanel.DoValidate prefix): the
    // chosen spells and the swap record go to every computer in one command, MemorizeSpellPrepare, in place of the
    // game's PrepareSpells, so no computer can get one without the other. The screen closes as the game's own would,
    // once the command has run here; if it doesn't run, the screen stays open with nothing changed. A press that
    // arrives late still applies, alike on every computer, with no undo or reconciliation.
    // False: not Memorize Spell mode.
    internal static bool TryPrepareMemorizeSpell(SpellRepertoirePanel panel)
    {
        var character = panel.GuiCharacter?.RulesetCharacter;

        if (character is not RulesetCharacterHero hero || !IsWizardRepertoire(panel.SpellRepertoire) ||
            !TryGetMemorizeSpellCondition(hero, out var condition))
        {
            return false;
        }

        var session = GetMemorizeSession(condition);
        var argument = string.Join("|", condition.Guid, panel.SpellRepertoire.SpellCastingFeature.Name,
            $"{session.Amount},{session.SpellIndex}",
            string.Join(";", panel.preparedSpells.Select(x => x.Name)));

        // the after-rest button stays off until this has come back (SpellPreparationScreen)
        SpellPreparationScreen.Submit(panel, hero, nameof(MemorizeSpellPrepare), argument,
            () => _memorizeSession = null);

        return true;
    }

    // runs on every computer at the same tick (RestCommandContext): the screen's chosen spells, and the swap made on it
    // as the record, for the condition the press was made for (a press for a condition a rest has replaced changes
    // nothing). Nothing changes unless every part is found, alike everywhere.
    // condition ID|spellcasting feature|amount,spell index|spell;spell...
    private static bool MemorizeSpellPrepare(RulesetCharacterHero hero, string argument)
    {
        var parts = argument.Split('|');
        var values = parts.Length == 4 ? parts[2].Split(',') : [];
        var spells = new List<SpellDefinition>();
        var database = DatabaseRepository.GetDatabase<SpellDefinition>();

        if (parts.Length != 4 || values.Length != 2 ||
            !ulong.TryParse(parts[0], out var conditionGuid) ||
            !int.TryParse(values[0], out var amount) ||
            !int.TryParse(values[1], out var spellIndex) ||
            !TryGetMemorizeSpellCondition(hero, out var condition) ||
            condition.Guid != conditionGuid ||
            hero.SpellRepertoires.FirstOrDefault(x => x.SpellCastingFeature.Name == parts[1]) is not { } repertoire ||
            !IsWizardRepertoire(repertoire))
        {
            Main.Error($"[RestCommand] Memorize Spell preparation for {hero.Name} not applied ({argument})");

            return false;
        }

        foreach (var name in parts[3].Split([';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!database.TryGetElement(name, out var spell))
            {
                Main.Error($"[RestCommand] Memorize Spell preparation for {hero.Name}: no spell {name}");

                return false;
            }

            spells.Add(spell);
        }

        // one swap at most, checked here too so no screen path can store more (a controller's Clear empties the list
        // without the swap check): at most one spell out of the prepared spells and one in.
        // Alike on every computer, as the prepared spells are the same everywhere at this tick
        if (repertoire.PreparedSpells.Except(spells).Count() > 1 || spells.Except(repertoire.PreparedSpells).Count() > 1)
        {
            Main.Error($"[RestCommand] Memorize Spell preparation for {hero.Name} changes more than one spell; not applied");

            return false;
        }

        repertoire.PreparedSpells.SetRange(spells);
        condition.Amount = amount;
        condition.SourceProficiencyBonus = spellIndex;

        return true;
    }

    // runs on every computer at the same tick (RestCommandContext): the condition that puts the hero's preparation
    // screen in Memorize Spell mode until the next rest, so every game creates it, with the same object ID
    private static bool BeginMemorizeSpell(RulesetCharacterHero hero, string argument)
    {
        // make this until any rest to ensure users cannot cheat by reopening the prep screen
        // as conditions on refresh won't update source amount nor source ability bonus used for tracking
        hero.InflictCondition(
            ConditionMemorizeSpell.Name,
            DurationType.UntilAnyRest,
            0,
            TurnOccurenceType.EndOfTurn,
            AttributeDefinitions.TagEffect,
            hero.guid,
            hero.CurrentFaction.Name,
            1,
            ConditionMemorizeSpell.Name,
            0,
            0,
            -1);

        return true;
    }

    private static void LoadWizardMemorizeSpell()
    {
        ServiceRepository.GetService<IFunctorService>()
            .RegisterFunctor(nameof(FunctorMemorizeSpell), new FunctorMemorizeSpell());
        RestCommandContext.Register(nameof(BeginMemorizeSpell), BeginMemorizeSpell);
        RestCommandContext.Register(nameof(MemorizeSpellPrepare), MemorizeSpellPrepare);
    }

    internal static void SwitchWizardMemorizeSpell()
    {
        Wizard.FeatureUnlocks.RemoveAll(x => x.FeatureDefinition == FeatureMemorizeSpell);

        if (Main.Settings.EnableWizardMemorizeSpell2024)
        {
            Wizard.FeatureUnlocks.Add(new FeatureUnlockByLevel(FeatureMemorizeSpell, 5));
        }

        Wizard.FeatureUnlocks.Sort(Sorting.CompareFeatureUnlock);
    }

    private class FunctorMemorizeSpell : Functor
    {
        public override IEnumerator Execute(
            FunctorParametersDescription functorParameters,
            FunctorExecutionContext context)
        {
            var inspectionScreen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();
            var partyStatusScreen = Gui.GuiService.GetScreen<GamePartyStatusScreen>();
            var hero = functorParameters.RestingHero;

            Gui.GuiService.GetScreen<RestModal>().KeepCurrentState = true;

            // the Wizard repertoire: a multiclass hero can have another prepared one listed first
            var spellRepertoire = hero.SpellRepertoires.FirstOrDefault(x =>
                IsWizardRepertoire(x) && x.SpellCastingFeature.SpellReadyness == SpellReadyness.Prepared);

            if (spellRepertoire == null)
            {
                yield break;
            }

            // the condition is added on every computer (added on the clicker's only, it would desync); the
            // preparation screen only opens once this computer has it too. A late Begin (TimedOut) adds the condition
            // everywhere at once, which is what a successful one does, so there's nothing to undo.
            var outcome = RestCommandContext.Outcome.Failed;

            yield return RestCommandContext.Send(hero, nameof(BeginMemorizeSpell), string.Empty, x => outcome = x);

            if (outcome != RestCommandContext.Outcome.Ran)
            {
                yield break;
            }

            // a fresh screen session from the committed record
            _memorizeSession = null;

            // the screen's choice and the swap record reach every computer together: TryPrepareMemorizeSpell
            partyStatusScreen.SetupDisplayPreferences(false, false, false);
            SpellPreparationScreen.Show(
                inspectionScreen, hero, Gui.GuiService.GetScreen<RestModal>(), spellRepertoire);

            while (context.Async && inspectionScreen.Visible)
            {
                yield return null;
            }

            // a Prepare press still on its way keeps Memorize Spell running, and so its after-rest button off
            while (SpellPreparationScreen.IsPending)
            {
                yield return null;
            }

            _memorizeSession = null;

            partyStatusScreen.SetupDisplayPreferences(true, true, true);
        }
    }
}
