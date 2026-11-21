using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Properties;
using static ActionDefinitions;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ActionDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.MonsterDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionConditionAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionRegenerations;

namespace SolastaUnfinishedBusiness.Models;

internal static class Tabletop2014Context
{
    internal static readonly HashSet<MonsterDefinition> ConjuredMonsters =
    [
        ConjuredOneBeastTiger_Drake,
        ConjuredTwoBeast_Direwolf,
        ConjuredFourBeast_BadlandsSpider,
        ConjuredEightBeast_Wolf,

        // Conjure minor elemental (4)
        SkarnGhoul, // CR 2
        WindSnake, // CR 2
        Fire_Jester, // CR 1

        // Conjure woodland beings (4) - not implemented

        // Conjure elemental (5)
        Air_Elemental, // CR 5
        Fire_Elemental, // CR 5
        Earth_Elemental, // CR 5

        InvisibleStalker, // CR 6

        // Conjure fey (6)
        FeyGiantApe, // CR 6
        FeyGiant_Eagle, // CR 5
        FeyBear, // CR 4
        Green_Hag, // CR 3
        FeyWolf, // CR 2
        FeyDriad
    ];

    internal static readonly FeatureDefinitionActionAffinity ActionAffinityConditionBlind =
        FeatureDefinitionActionAffinityBuilder
            .Create("ActionAffinityConditionBlind")
            .SetGuiPresentationNoContent(true)
            .SetForbiddenActions(Id.AttackOpportunity)
            .AddToDB();

    internal static readonly FeatureDefinitionPower FeatureDefinitionPowerHelpAction = FeatureDefinitionPowerBuilder
        .Create("PowerHelp")
        .SetGuiPresentation(Category.Feature, Sprites.GetSprite("PowerHelp", Resources.PowerHelp, 256, 128))
        .SetUsesFixed(ActivationTime.Action)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetDurationData(DurationType.Round, 1, TurnOccurenceType.EndOfSourceTurn)
                .SetTargetingData(Side.Enemy, RangeType.Touch, 0, TargetType.IndividualsUnique)
                .SetEffectForms(EffectFormBuilder.ConditionForm(CustomConditionsContext.Distracted))
                .Build())
        .SetUniqueInstance()
        .AddToDB();

    internal static void SwitchFullyControlConjurations()
    {
        foreach (var conjuredMonster in ConjuredMonsters)
        {
            conjuredMonster.fullyControlledWhenAllied = Main.Settings.FullyControlConjurations;
        }
    }

    internal static void LateLoad()
    {
        LoadConditionInvisibleShouldNotAllowOpportunityAttack();
        LoadGravitySlam();
        SwitchBardHealingBalladOnLongRest();
        SwitchColdResistanceAndImmunityAlsoGrantsWeatherImmunity();
        SwitchEldritchBlastRange();
        SwitchFullyControlConjurations();
        SwitchGrappleAction();
        SwitchGravitySlam();
        SwitchHelpPower();
        SwitchOfficialFoodRationsWeight();
        SwitchProneAction();
        SwitchRingOfRegenerationHealRate();
    }

    internal static void SwitchEldritchBlastRange()
    {
        EldritchBlast.effectDescription.rangeParameter = Main.Settings.FixEldritchBlastRange ? 24 : 16;
    }

    internal static void SwitchBardHealingBalladOnLongRest()
    {
        FeatureDefinitionRestHealingModifiers.RestHealingModifierBardHealingBallad.applyDuringLongRest =
            Main.Settings.EnableBardHealingBalladOnLongRest;
    }

    internal static void SwitchRingOfRegenerationHealRate()
    {
        var ringDefinition = RegenerationRing;

        if (Main.Settings.FixRingOfRegenerationHealRate)
        {
            // Heal by 1 hp per 3 minutes which is roughly the same as 
            // RAW of 1d6 (avg 3.5) every 10 minutes.
            ringDefinition.tickType = DurationType.Minute;
            ringDefinition.tickNumber = 3;
            ringDefinition.diceNumber = 1;
        }
        else
        {
            ringDefinition.tickType = DurationType.Round;
            ringDefinition.tickNumber = 1;
            ringDefinition.diceNumber = 2;
        }

        ringDefinition.dieType = DieType.D1;
    }

    internal static void SwitchOfficialFoodRationsWeight()
    {
        var foodSrdWeight = Food_Ration;
        var foodForagedSrdWeight = Food_Ration_Foraged;

        if (Main.Settings.UseOfficialFoodRationsWeight)
        {
            foodSrdWeight.weight = 2.0f;
            foodForagedSrdWeight.weight = 2.0f;
        }
        else
        {
            foodSrdWeight.weight = 3.0f;
            foodForagedSrdWeight.weight = 3.0f;
        }
    }

    // Undo steps for the weather immunities this switch added, so a later switch removes exactly those and never
    // the immunities the game's own definitions carry (Frozen, Fire Shield, Ring of Cold Resistance and others).
    private static readonly List<System.Action> ColdWeatherImmunityUndo = [];

    // A heavy blizzard (EnvironmentEffectDefinition BlizzardArea_Heavy) applies both ConditionFrozen and
    // ConditionChilledInsteadOfFrozen, and Frozen makes its bearer immune to the latter. So immunity to Frozen alone
    // turns a heavy blizzard into Chilled ("reduces frozen to chilled"), while immunity to ChilledInsteadOfFrozen too
    // removes that as well.
    internal static void SwitchColdResistanceAndImmunityAlsoGrantsWeatherImmunity()
    {
        foreach (var undo in ColdWeatherImmunityUndo)
        {
            undo();
        }

        ColdWeatherImmunityUndo.Clear();

        var resistanceAffinities = Main.Settings.ColdResistanceAlsoGrantsImmunityToChilledCondition
            ? new FeatureDefinition[] { ConditionAffinityWeatherChilledImmunity, ConditionAffinityWeatherFrozenImmunity }
            : [];
        var immunityAffinities = Main.Settings.ColdImmunityAlsoGrantsImmunityToChilledAndFrozenCondition
            ? new FeatureDefinition[]
            {
                ConditionAffinityWeatherChilledImmunity, ConditionAffinityWeatherChilledInsteadOfFrozenImmunity,
                ConditionAffinityWeatherFrozenImmunity
            }
            : [];

        foreach (var featureSet in DatabaseRepository.GetDatabase<FeatureDefinitionFeatureSet>())
        {
            AddMissing(featureSet.FeatureSet, WeatherAffinitiesFor(featureSet.FeatureSet.Contains));
        }

        foreach (var condition in DatabaseRepository.GetDatabase<ConditionDefinition>())
        {
            AddMissing(condition.Features, WeatherAffinitiesFor(condition.Features.Contains));
        }

        foreach (var item in DatabaseRepository.GetDatabase<ItemDefinition>())
        {
            var properties = item.staticProperties;

            foreach (var (source, affinities) in new[]
                     {
                         (FeatureDefinitionDamageAffinitys.DamageAffinityColdResistance, resistanceAffinities),
                         (FeatureDefinitionDamageAffinitys.DamageAffinityColdImmunity, immunityAffinities)
                     })
            {
                var sourceProperty = properties.FirstOrDefault(x => x.FeatureDefinition == source);

                if (sourceProperty == null)
                {
                    continue;
                }

                foreach (var affinity in affinities)
                {
                    if (properties.Any(x => x.FeatureDefinition == affinity))
                    {
                        continue;
                    }

                    var added = new ItemPropertyDescription(sourceProperty) { featureDefinition = affinity };

                    properties.Add(added);
                    ColdWeatherImmunityUndo.Add(() => properties.Remove(added));
                }
            }
        }

        return;

        IEnumerable<FeatureDefinition> WeatherAffinitiesFor(System.Func<FeatureDefinition, bool> has)
        {
            var wanted = new List<FeatureDefinition>();

            if (has(FeatureDefinitionDamageAffinitys.DamageAffinityColdResistance))
            {
                wanted.AddRange(resistanceAffinities);
            }

            if (has(FeatureDefinitionDamageAffinitys.DamageAffinityColdImmunity))
            {
                wanted.AddRange(immunityAffinities);
            }

            return wanted.Distinct();
        }

        static void AddMissing(List<FeatureDefinition> features, IEnumerable<FeatureDefinition> wanted)
        {
            foreach (var affinity in wanted.ToList())
            {
                if (features.Contains(affinity))
                {
                    continue;
                }

                features.Add(affinity);
                ColdWeatherImmunityUndo.Add(() => features.Remove(affinity));
            }
        }
    }

    private static void LoadConditionInvisibleShouldNotAllowOpportunityAttack()
    {
        // shadow murder must be removed whenever invisible is
        ConditionDefinitions.ConditionShadowMurder.parentCondition = ConditionDefinitions.ConditionInvisibleBase;

        foreach (var condition in DatabaseRepository.GetDatabase<ConditionDefinition>()
                     .Where(x => x.IsSubtypeOf(ConditionInvisible)))
        {
            condition.AddCustomSubFeatures(new IgnoreAoOOnMeConditionInvisible());
        }
    }

    internal static void SwitchGrappleAction()
    {
        GrappleContext.ActionGrapple.formType = Main.Settings.EnableGrappleAction
            ? ActionFormType.Large
            : ActionFormType.Invisible;

        GrappleContext.ActionDisableGrapple.formType = Main.Settings.EnableGrappleAction
            ? ActionFormType.Large
            : ActionFormType.Invisible;
    }

    internal static void SwitchProneAction()
    {
        DropProne.actionType = ActionType.NoCost;
        DropProne.formType = Main.Settings.EnableProneAction
            ? ActionFormType.Small
            : ActionFormType.Invisible;
    }


    internal static void SwitchHelpPower()
    {
        var dbCharacterRaceDefinition = DatabaseRepository.GetDatabase<CharacterRaceDefinition>();
        var subRaces = dbCharacterRaceDefinition
            .SelectMany(x => x.SubRaces);
        var races = dbCharacterRaceDefinition
            .Where(x => !subRaces.Contains(x));

        if (Main.Settings.EnableHelpAction)
        {
            foreach (var characterRaceDefinition in races
                         .Where(a => !a.FeatureUnlocks.Exists(x =>
                             x.Level == 1 && x.FeatureDefinition == FeatureDefinitionPowerHelpAction)))
            {
                characterRaceDefinition.FeatureUnlocks.Add(
                    new FeatureUnlockByLevel(FeatureDefinitionPowerHelpAction, 1));
            }
        }
        else
        {
            foreach (var characterRaceDefinition in races
                         .Where(a => a.FeatureUnlocks.Exists(x =>
                             x.Level == 1 && x.FeatureDefinition == FeatureDefinitionPowerHelpAction)))
            {
                characterRaceDefinition.FeatureUnlocks.RemoveAll(x =>
                    x.Level == 1 && x.FeatureDefinition == FeatureDefinitionPowerHelpAction);
            }
        }
    }

    private sealed class IgnoreAoOOnMeConditionInvisible : IIgnoreAoOOnMe
    {
        public bool CanIgnoreAoOOnSelf(RulesetCharacter defender, RulesetCharacter attacker)
        {
            var locationAttacker = GameLocationCharacter.GetFromActor(attacker);
            var locationDefender = GameLocationCharacter.GetFromActor(defender);

            return Main.Settings.BlindedConditionDontAllowAttackOfOpportunity &&
                   !locationAttacker.CanPerceiveTarget(locationDefender);
        }
    }

    #region Gravity Slam

    private static EffectDescription _gravitySlamVanilla;
    private static EffectDescription _gravitySlamModified;

    private static void LoadGravitySlam()
    {
        _gravitySlamVanilla = GravitySlam.EffectDescription;
        _gravitySlamModified = EffectDescriptionBuilder
            .Create(_gravitySlamVanilla)
            .SetTargetingData(Side.All, RangeType.Distance, 20, TargetType.Cylinder, 4, 10)
            .AddEffectForms(EffectFormBuilder.MotionForm(ExtraMotionType.PushDown, 10))
            .Build();
    }

    internal static void SwitchGravitySlam()
    {
        if (Main.Settings.EnablePullPushOnVerticalDirection && Main.Settings.ModifyGravitySlam)
        {
            GravitySlam.effectDescription = _gravitySlamModified;
        }
        else
        {
            GravitySlam.effectDescription = _gravitySlamVanilla;
        }

        Global.RefreshControlledCharacter();
    }

    #endregion
}
