using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Subclasses;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAdditionalDamages;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionAttributeModifiers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionCastSpells;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionDamageAffinitys;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionFeatureSets;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPointPools;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionSenses;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.SpellListDefinitions;
using static SolastaUnfinishedBusiness.Builders.Features.AutoPreparedSpellsGroupBuilder;
using static SolastaUnfinishedBusiness.Builders.Features.FeatureDefinitionCastSpellBuilder;
using Resources = SolastaUnfinishedBusiness.Properties.Resources;

namespace SolastaUnfinishedBusiness.Models;

internal static class Level20Context
{
    internal const string PowerWarlockEldritchMasterName = "PowerWarlockEldritchMaster";
    internal const int ModMaxLevel = 20;
    internal const int ModMaxExperience = 355000;
    internal const int GameMaxLevel = 16;

    private const string PowerSorcerousRestorationName = "PowerSorcererSorcerousRestoration";

    internal static readonly FeatureDefinitionPower PowerMonkEmptyBody = FeatureDefinitionPowerBuilder
        .Create("PowerMonkEmptyBody")
        .SetGuiPresentation(Category.Feature, Sprites.GetSprite("EmptyBody", Resources.EmptyBody, 128, 64))
        .SetUsesFixed(ActivationTime.Action, RechargeRate.KiPoints, 4, 4)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 0, TargetType.Self)
                .SetDurationData(DurationType.Minute, 1)
                .SetEffectForms(
                    EffectFormBuilder
                        .Create()
                        .SetConditionForm(DatabaseHelper.ConditionDefinitions.ConditionInvisibleGreater,
                            ConditionForm.ConditionOperation.Add)
                        .Build(),
                    EffectFormBuilder
                        .Create()
                        .SetConditionForm(
                            ConditionDefinitionBuilder
                                .Create("ConditionMonkEmptyBody")
                                .SetGuiPresentation(
                                    Category.Condition,
                                    DatabaseHelper.ConditionDefinitions.ConditionShielded)
                                .AddFeatures(
                                    DamageAffinityAcidResistance,
                                    DamageAffinityBludgeoningResistanceTrue,
                                    DamageAffinityColdResistance,
                                    DamageAffinityFireResistance,
                                    DamageAffinityLightningResistance,
                                    DamageAffinityNecroticResistance,
                                    DamageAffinityPiercingResistanceTrue,
                                    DamageAffinityPoisonResistance,
                                    DamageAffinityPsychicResistance,
                                    DamageAffinityRadiantResistance,
                                    DamageAffinitySlashingResistanceTrue,
                                    DamageAffinityThunderResistance)
                                .SetPossessive()
                                .AddToDB(),
                            ConditionForm.ConditionOperation.Add)
                        .Build())
                .Build())
        .AddToDB();

    internal static readonly FeatureDefinition FeatureMonkPerfectSelf = FeatureDefinitionBuilder
        .Create("BattleStartedListenerMonkPerfectSelf")
        .SetGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(new BattleStartedListenerMonkPerfectSelf())
        .AddToDB();

    internal static readonly FeatureDefinitionAutoPreparedSpells AutoPreparedSpellsBardWordOfCreation =
        FeatureDefinitionAutoPreparedSpellsBuilder
            .Create("AutoPreparedSpellsBardWordOfCreation")
            .SetGuiPresentation(Category.Feature)
            .SetAutoTag("College")
            .SetSpellcastingClass(Bard)
            .SetPreparedSpellGroups(BuildSpellGroup(9, SpellsContext.PowerWordHeal, SpellsContext.PowerWordKill))
            .AddCustomSubFeatures(new CustomBehaviorBardWordOfCreation())
            .AddToDB();

    internal static readonly FeatureDefinition FeatureBardSuperiorInspiration = FeatureDefinitionBuilder
        .Create("FeatureBardSuperiorInspiration")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    internal static readonly FeatureDefinition FeatureBardSuperiorInspiration2024 = FeatureDefinitionBuilder
        .Create("FeatureBardSuperiorInspiration2024")
        .SetGuiPresentation(Category.Feature)
        .AddToDB();

    internal static readonly FeatureDefinitionPointPool PointPoolBardMagicalSecrets18 =
        FeatureDefinitionPointPoolBuilder
            .Create(PointPoolBardMagicalSecrets14, "PointPoolBardMagicalSecrets18")
            .AddToDB();

    internal static readonly FeatureDefinitionPower PowerSorcerousRestoration = FeatureDefinitionPowerBuilder
        .Create(PowerSorcerousRestorationName)
        .SetGuiPresentation("PowerSorcererSorcerousRestoration", Category.Feature)
        .SetUsesFixed(ActivationTime.Rest)
        .SetEffectDescription(
            EffectDescriptionBuilder
                .Create()
                .SetTargetingData(Side.Ally, RangeType.Self, 1, TargetType.Self)
                .SetParticleEffectParameters(PowerWizardArcaneRecovery)
                .Build())
        .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
        .AddToDB();

    internal static readonly FeatureDefinitionPower PowerWarlockEldritchMaster = FeatureDefinitionPowerBuilder
        .Create(PowerWizardArcaneRecovery, PowerWarlockEldritchMasterName)
        .SetGuiPresentation(Category.Feature)
        .SetUsesFixed(ActivationTime.Minute1, RechargeRate.LongRest)
        .AddToDB();

    internal static readonly FeatureDefinitionSense SenseRangerFeralSenses = FeatureDefinitionSenseBuilder
        .Create(SenseSeeInvisible12, "SenseRangerFeralSenses")
        .SetGuiPresentation(Category.Feature)
        .SetSense(SenseMode.Type.DetectInvisibility, 6)
        .AddToDB();

    internal static readonly FeatureDefinitionSense SenseRangerFeralSenses2024 = FeatureDefinitionSenseBuilder
        .Create(SenseSeeInvisible12, "SenseRangerFeralSenses2024")
        .SetGuiPresentation(Category.Feature)
        .SetSense(SenseMode.Type.Blindsight, 6)
        .AddToDB();

    internal static readonly FeatureDefinition FeatureFoeSlayer = FeatureDefinitionBuilder
        .Create("FeatureRangerFoeSlayer")
        .SetGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(new ModifyWeaponAttackModeRangerFoeSlayer())
        .AddToDB();

    internal static readonly FeatureDefinition FeatureFoeSlayer2024 = FeatureDefinitionBuilder
        .Create("FeatureRangerFoeSlayer2024")
        .SetGuiPresentation(Category.Feature)
        .AddCustomSubFeatures(new ModifyAdditionalDamageRangerFoeSlayer2024())
        .AddToDB();

    internal static readonly FeatureDefinitionPower PowerClericTurnUndead17 = FeatureDefinitionPowerBuilder
        .Create(PowerClericTurnUndead14, "PowerClericTurnUndead17")
        .AddToDB();

    internal static readonly FeatureDefinitionMagicAffinity MagicAffinityArchDruid =
        FeatureDefinitionMagicAffinityBuilder
            .Create("MagicAffinityArchDruid")
            .SetGuiPresentation(Category.Feature)
            .SetHandsFullCastingModifiers(true, true, true)
            .AddToDB();

    internal static void Load()
    {
        BarbarianLoad();
        BardLoad();
        ClericLoad();
        DruidLoad();
        FighterLoad();
        MonkLoad();
        PaladinLoad();
        RangerLoad();
        RogueLoad();
        SorcererLoad();
        WarlockLoad();
        WizardLoad();
        MartialSpellBladeLoad();
        RoguishShadowcasterLoad();

        Level20SubclassesContext.Load();

        InitExperienceThresholdsTable();
    }

    internal static void LateLoad()
    {
        const BindingFlags PrivateBinding = BindingFlags.Instance | BindingFlags.NonPublic;

        var harmony = new Harmony("SolastaUnfinishedBusiness");
        var transpiler = new Func<IEnumerable<CodeInstruction>, IEnumerable<CodeInstruction>>(Level20Transpiler).Method;

        // these are currently the hard-coded levels on below methods
        var methods = new[]
        {
            typeof(ArchetypesPreviewModal).GetMethod("Refresh", PrivateBinding),
            typeof(CharactersPanel).GetMethod("Refresh", PrivateBinding),
            typeof(FeatureDefinitionCastSpell).GetMethod("EnsureConsistency"),
            typeof(HigherLevelFeaturesModal).GetMethod("Bind"), typeof(InvocationSubPanel).GetMethod("SetState"),
            typeof(RulesetCharacterHero).GetMethod("RegisterAttributes"),
            typeof(RulesetCharacterHero).GetMethod("SerializeElements"),
            typeof(RulesetEntity).GetMethod("SerializeElements"),
            typeof(UserCampaignEditorScreen).GetMethod("OnMaxLevelEndEdit"),
            typeof(UserCampaignEditorScreen).GetMethod("OnMinLevelEndEdit"),
            typeof(UserLocationSettingsModal).GetMethod("OnMaxLevelEndEdit"),
            typeof(UserLocationSettingsModal).GetMethod("OnMinLevelEndEdit")
        };

        foreach (var method in methods)
        {
            try
            {
                harmony.Patch(method, transpiler: new HarmonyMethod(transpiler));
            }
            catch
            {
                Main.Error($"Failed to apply Level20Transpiler patch to {method.DeclaringType}.{method.Name}");
            }
        }
    }

    [NotNull]
    // ReSharper disable once SuggestBaseTypeForParameter
    private static List<CodeInstruction> Level20Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);

        if (!Main.Settings.EnableLevel20)
        {
            return code;
        }

        var result = code
            .FindAll(x => x.opcode == OpCodes.Ldc_I4_S && Convert.ToInt32(x.operand) == GameMaxLevel);

        if (result.Count > 0)
        {
            result.ForEach(x => x.operand = ModMaxLevel);
        }
        else
        {
            Main.Error("Level20Transpiler");
        }

        return code;
    }

    private static void BarbarianLoad()
    {
        var changeAbilityCheckBarbarianIndomitableMight = FeatureDefinitionBuilder
            .Create("ChangeAbilityCheckBarbarianIndomitableMight")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new ModifyAbilityCheckBarbarianIndomitableMight())
            .AddToDB();

        var customCodeBarbarianPrimalChampion = FeatureDefinitionBuilder
            .Create("CustomCodeBarbarianPrimalChampion")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new CustomLevelUpLogicBarbarianPrimalChampion())
            .AddToDB();

        Barbarian.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(AttributeModifierBarbarianBrutalCriticalAdd, 17),
            // vanilla already adds this even with top level 16
            // new(AttributeModifierBarbarianRagePointsAdd, 17),
            new FeatureUnlockByLevel(changeAbilityCheckBarbarianIndomitableMight, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(customCodeBarbarianPrimalChampion, 20)
        );
    }

    private static void BardLoad()
    {
        FeatureBardSuperiorInspiration.AddCustomSubFeatures(
            new BattleStartedListenerBardSuperiorInspiration(FeatureBardSuperiorInspiration));

        FeatureBardSuperiorInspiration2024.AddCustomSubFeatures(
            new BattleStartedListenerBardSuperiorInspiration(FeatureBardSuperiorInspiration2024));

        Bard.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(PointPoolBardMagicalSecrets18, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(FeatureBardSuperiorInspiration, 20)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Full,
            CastSpellBard.SlotsPerLevels);

        EnumerateKnownSpells(
            4,
            CasterProgression.Full,
            CastSpellBard.KnownSpells);

        EnumerateReplacedSpells(
            2, 1, CastSpellBard.ReplacedSpells);

        SpellListBard.maxSpellLevel = 9;
    }

    private static void ClericLoad()
    {
        PowerClericTurnUndead17.EffectDescription.EffectForms[0].KillForm.challengeRating = 4;

        Cleric.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(PowerClericTurnUndead17, 17),
            new FeatureUnlockByLevel(AttributeModifierClericChannelDivinityAdd, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Full,
            CastSpellCleric.SlotsPerLevels);

        SpellListCleric.maxSpellLevel = 9;
    }

    private static void DruidLoad()
    {
        //BUGFIX: add PB to wildshape saving checks whenever summoner is proficient
        var summoningAffinity = FeatureDefinitionSummoningAffinityBuilder
            .Create("SummoningAffinityDruidWildshape")
            .SetGuiPresentationNoContent(true)
            .SetRequiredMonsterTag(TagsDefinitions.CreatureTagWildShape)
            .SetAddedConditions(
                ConditionDefinitionBuilder
                    .Create("ConditionSummoningAffinityDruidWildshape")
                    .SetGuiPresentationNoContent(true)
                    .SetSilent(Silent.WhenAddedOrRemoved)
                    .SetFeatures(
                        FeatureDefinitionSavingThrowAffinityBuilder
                            .Create("SavingThrowAffinityDruidWildshape")
                            .SetGuiPresentation("Feedback/&BeastCompanionBonusTitle", Gui.NoLocalization)
                            .AddCustomSubFeatures(
                                new AddPBToSummonCheckOnlyIfSummonerIsProficient(),
                                new AddPBToSummonCheck(1,
                                    AttributeDefinitions.Intelligence,
                                    AttributeDefinitions.Wisdom,
                                    AttributeDefinitions.Charisma))
                            .AddToDB())
                    .AddToDB())
            .AddToDB();

        // only a placeholder to display the feature name as this is solved on CanCastSpells patch
        var featureDruidBeastSpells = FeatureDefinitionBuilder
            .Create("FeatureDruidBeastSpells")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        MagicAffinityArchDruid.AddCustomSubFeatures(new ActionFinishedByMeArchDruid(MagicAffinityArchDruid));

        Druid.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(summoningAffinity, 2),
            new FeatureUnlockByLevel(featureDruidBeastSpells, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(MagicAffinityArchDruid, 20)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Full,
            CastSpellDruid.SlotsPerLevels);

        SpellListDruid.maxSpellLevel = 9;
    }

    private static void FighterLoad()
    {
        var powerFighterActionSurge2 = FeatureDefinitionPowerBuilder
            .Create(PowerFighterActionSurge, "PowerFighterActionSurge2")
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.ShortRest, 1, 2)
            .SetOverriddenPower(PowerFighterActionSurge)
            .AddToDB();

        Fighter.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(powerFighterActionSurge2, 17),
            new FeatureUnlockByLevel(AttributeModifierFighterIndomitableAdd1, 17),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(AttributeModifierFighterExtraAttack, 20)
        );
    }

    private static void MonkLoad()
    {
        Monk.FeatureUnlocks.AddRange(new List<FeatureUnlockByLevel>
        {
            new(PowerMonkEmptyBody, 18), new(FeatureSetAbilityScoreChoice, 19), new(FeatureMonkPerfectSelf, 20)
        });
    }

    private static void PaladinLoad()
    {
        var powerPaladinAuraOfCourage18 = FeatureDefinitionPowerBuilder
            .Create(PowerPaladinAuraOfCourage, "PowerPaladinAuraOfCourage18")
            .SetOrUpdateGuiPresentation(Category.Feature)
            .SetOverriddenPower(PowerPaladinAuraOfCourage)
            .AddToDB();

        powerPaladinAuraOfCourage18.EffectDescription.targetParameter = 13;

        var powerPaladinAuraOfProtection18 = FeatureDefinitionPowerBuilder
            .Create(PowerPaladinAuraOfProtection, "PowerPaladinAuraOfProtection18")
            .SetOrUpdateGuiPresentation(Category.Feature)
            .SetOverriddenPower(PowerPaladinAuraOfProtection)
            .AddToDB();

        powerPaladinAuraOfProtection18.EffectDescription.targetParameter = 13;

        Paladin.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(powerPaladinAuraOfCourage18, 18),
            new FeatureUnlockByLevel(powerPaladinAuraOfProtection18, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Half,
            CastSpellPaladin.SlotsPerLevels);

        SpellListPaladin.maxSpellLevel = 5;
    }

    private static void RangerLoad()
    {
        Ranger.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(SenseRangerFeralSenses, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(FeatureFoeSlayer, 20)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Half,
            CastSpellRanger.SlotsPerLevels);

        EnumerateKnownSpells(
            2,
            CasterProgression.Half,
            CastSpellRanger.KnownSpells);

        EnumerateReplacedSpells(
            3, 1, CastSpellRanger.ReplacedSpells);

        SpellListRanger.maxSpellLevel = 5;
    }

    private static void RogueLoad()
    {
        var featureRogueElusive = FeatureDefinitionBuilder
            .Create("FeatureRogueElusive")
            .SetGuiPresentation(Category.Feature)
            .AddCustomSubFeatures(new PhysicalAttackInitiatedOnMeRogueElusive())
            .AddToDB();

        var powerRogueStrokeOfLuck = FeatureDefinitionPowerBuilder
            .Create("PowerRogueStrokeOfLuck")
            .SetGuiPresentation(Category.Feature)
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.ShortRest)
            .SetShowCasting(false)
            .AddToDB();

        powerRogueStrokeOfLuck.AddCustomSubFeatures(
            ModifyPowerVisibility.Hidden,
            new TryAlterOutcomeAttackRogueStrokeOfLuck(powerRogueStrokeOfLuck));

        Rogue.FeatureUnlocks.AddRange(
        [
            new FeatureUnlockByLevel(featureRogueElusive, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(powerRogueStrokeOfLuck, 20)
        ]);
    }

    private static void SorcererLoad()
    {
        _ = RestActivityDefinitionBuilder
            .Create("RestActivitySorcererSorcerousRestoration")
            .SetGuiPresentation(PowerSorcerousRestorationName, Category.Feature)
            .SetRestData(
                RestDefinitions.RestStage.AfterRest,
                RestType.ShortRest,
                RestActivityDefinition.ActivityCondition.CanUsePower,
                FunctorDefinitions.FunctorUsePower,
                PowerSorcerousRestorationName)
            .AddToDB();

        var effectFormRestoration = EffectFormBuilder
            .Create()
            .SetSpellForm(9)
            .Build();

        effectFormRestoration.SpellSlotsForm.type = SpellSlotsForm.EffectType.GainSorceryPoints;
        effectFormRestoration.SpellSlotsForm.sorceryPointsGain = 4;

        PowerSorcerousRestoration.EffectDescription.EffectForms.Add(effectFormRestoration);

        Sorcerer.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(PointPoolSorcererAdditionalMetamagic, 17),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(PowerSorcerousRestoration, 20)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Full,
            CastSpellSorcerer.SlotsPerLevels);

        EnumerateKnownSpells(
            2,
            CasterProgression.Full,
            CastSpellSorcerer.KnownSpells);

        EnumerateReplacedSpells(
            2, 1, CastSpellSorcerer.ReplacedSpells);

        SpellListSorcerer.maxSpellLevel = 9;
    }

    private static void WarlockLoad()
    {
        var pointPoolWarlockMysticArcanum9 = FeatureDefinitionPointPoolBuilder
            .Create("PointPoolWarlockMysticArcanum9")
            .SetGuiPresentation(Category.Feature, "Feature/&PointPoolWarlockMysticArcanumDescription")
            .SetSpellOrCantripPool(HeroDefinitions.PointsPoolType.Spell, 1, null, "MysticArcanum", 9, 9)
            .AddToDB();

        pointPoolWarlockMysticArcanum9.minSpellLevel = 9;
        pointPoolWarlockMysticArcanum9.maxSpellLevel = 9;

        var pointPoolWarlockInvocation18 = FeatureDefinitionPointPoolBuilder
            .Create("PointPoolWarlockInvocation18")
            .SetGuiPresentation("PointPoolWarlockInvocationAdditional", Category.Feature)
            .SetPool(HeroDefinitions.PointsPoolType.Invocation, 1)
            .AddToDB();

        Warlock.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(pointPoolWarlockMysticArcanum9, 17),
            new FeatureUnlockByLevel(pointPoolWarlockInvocation18, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(PowerWarlockEldritchMaster, 20)
        );

        CastSpellWarlock.KnownSpells.SetRange(SharedSpellsContext.WarlockKnownSpells);

        EnumerateReplacedSpells(
            2, 1, CastSpellWarlock.ReplacedSpells);

        SpellListWarlock.maxSpellLevel = 9;
    }

    private static void WizardLoad()
    {
        var spellMastery = WizardSpellMastery.BuildWizardSpellMastery();
        var signatureSpells = WizardSignatureSpells.BuildWizardSignatureSpells();

        Wizard.FeatureUnlocks.AddRange(
            new FeatureUnlockByLevel(spellMastery, 18),
            new FeatureUnlockByLevel(FeatureSetAbilityScoreChoice, 19),
            new FeatureUnlockByLevel(signatureSpells, 20)
        );

        EnumerateSlotsPerLevel(
            CasterProgression.Full,
            CastSpellWizard.SlotsPerLevels);

        EnumerateKnownSpells(
            6,
            CasterProgression.Full,
            CastSpellWizard.KnownSpells);

        SpellListWizard.maxSpellLevel = 9;
    }

    private static void MartialSpellBladeLoad()
    {
        EnumerateSlotsPerLevel(
            CasterProgression.OneThird,
            CastSpellMartialSpellBlade.SlotsPerLevels);

        EnumerateKnownSpells(
            3,
            CasterProgression.OneThird,
            CastSpellMartialSpellBlade.KnownSpells);

        EnumerateReplacedSpells(
            4, 1, CastSpellMartialSpellBlade.ReplacedSpells);
    }

    private static void RoguishShadowcasterLoad()
    {
        EnumerateSlotsPerLevel(
            CasterProgression.OneThird,
            CastSpellShadowcaster.SlotsPerLevels);

        EnumerateKnownSpells(
            3,
            CasterProgression.OneThird,
            CastSpellShadowcaster.KnownSpells);

        EnumerateReplacedSpells(
            4, 1, CastSpellShadowcaster.ReplacedSpells);
    }

    private static void InitExperienceThresholdsTable()
    {
        var len = ExperienceThresholds.Length;
        var experience = new int[len + 1];

        Array.Copy(ExperienceThresholds, experience, len);
        experience[len] = experience[len - 1];

        ExperienceThresholds = experience;
    }

    private sealed class CustomBehaviorBardWordOfCreation : IModifyEffectDescription
    {
        public bool IsValid(BaseDefinition definition, RulesetCharacter character, EffectDescription effectDescription)
        {
            return definition == SpellsContext.PowerWordHeal || definition == SpellsContext.PowerWordKill;
        }

        public EffectDescription GetEffectDescription(
            BaseDefinition definition,
            EffectDescription effectDescription,
            RulesetCharacter character,
            RulesetEffect rulesetEffect)
        {
            effectDescription.targetParameter = 2;

            return effectDescription;
        }
    }

    //
    // HELPERS
    //

    // Spell Mastery and Signature Spells reuse the spell preparation screen to pick their spells (kept in
    // ExtraSpellsByTag). Switching the hero's prepared spells to the picks before the screen opens and back after, on
    // the clicker's computer only, while the screen's choice reaches everyone through the game's PrepareSpells command,
    // would leave the other computers with the picks as their prepared spells and no picks at all (desync issue 04).
    //
    // So nothing shared changes until Prepare:
    // - the mode lives on the clicker's screen only (TaggedSpellPreparation.Mode): which hero and feature the screen is
    //   picking for, what IsPreparation and IsInvalidSelectedSpell read, and what fills the screen's list with the
    //   current picks (ShowPicks, after the game fills it from the prepared spells);
    // - Prepare sends one command with the chosen spells (TryPrepare, from the DoValidate prefix); on every computer
    //   at the same tick it makes them the picks and takes them out of the prepared spells. The screen closes, as the
    //   game's own does, once the command has run here; closing it without Prepare changes nothing.
    // While the screen is open, the clicker's game also gives the repertoire the mode's limit of 2 spells
    // (MaxPreparedSpell), on that computer alone.
    internal static class TaggedSpellPreparation
    {
        // the wizard repertoire and feature the clicker's preparation screen is picking for, or null. It's the
        // repertoire, not the hero, so a multiclass hero's other prepared repertoire (a Cleric's) keeps its own limit,
        // filter and title while the screen is open
        private static (RulesetSpellRepertoire Repertoire, string Tag)? Mode { get; set; }

        // the screen's list when it opened in the mode (auto-prepared spells and picks), which Revert goes back to and
        // compares with
        private static List<SpellDefinition> _initialPicks = [];

        // the feature's stored picks when the screen opened, as sent with Prepare (see SetPicks)
        private static string _openedWithPicks = string.Empty;

        // the stored picks as text: their names in order, or "-" when the feature has none yet
        private static string StoredPicks(RulesetSpellRepertoire repertoire, string tag)
        {
            return repertoire.ExtraSpellsByTag.TryGetValue(tag, out var picks)
                ? string.Join(";", picks.Select(x => x.Name))
                : "-";
        }

        // whether this panel is the screen in either feature's mode
        internal static bool IsInMode(SpellRepertoirePanel panel)
        {
            return Mode is { } mode && panel && panel.SpellRepertoire == mode.Repertoire;
        }

        internal static bool IsMode(RulesetSpellRepertoire repertoire, string tag)
        {
            return repertoire != null && Mode is { } mode && mode.Repertoire == repertoire && mode.Tag == tag;
        }

        // the hero's Wizard repertoire: the features are the Wizard's, and a multiclass hero can have another prepared
        // repertoire listed first (upstream took the first prepared one)
        private static RulesetSpellRepertoire GetRepertoire(RulesetCharacterHero hero)
        {
            return hero.SpellRepertoires.FirstOrDefault(x =>
                x.SpellCastingClass == Wizard && x.SpellCastingFeature.SpellReadyness == SpellReadyness.Prepared);
        }

        internal static void Register(string tag)
        {
            RestCommandContext.Register($"Set{tag}", (hero, argument) => SetPicks(hero, tag, argument));
        }

        // the screen's list in the mode: the auto-prepared spells and the current picks (upstream showed the
        // auto-prepared spells the first time, the picks after)
        internal static void ShowPicks(SpellRepertoirePanel panel)
        {
            if (Mode is not { } mode || panel.SpellRepertoire != mode.Repertoire)
            {
                return;
            }

            var repertoire = panel.SpellRepertoire;

            panel.preparedSpells.SetRange(repertoire.AutoPreparedSpells);

            if (repertoire.ExtraSpellsByTag.TryGetValue(mode.Tag, out var picks))
            {
                panel.preparedSpells.AddRange(picks.Where(x => !panel.preparedSpells.Contains(x)));
            }

            _initialPicks = [.. panel.preparedSpells];
            _openedWithPicks = StoredPicks(repertoire, mode.Tag);
            panel.RefreshPreparation(false);
        }

        // RefreshPreparation postfix: the game enables Revert when the list differs from the prepared spells; in the
        // mode it's enabled when the list differs from the picks the screen opened with, and disabled once back to them
        internal static void RefreshRevert(SpellRepertoirePanel panel)
        {
            if (Mode is not { } mode || panel.SpellRepertoire != mode.Repertoire || !panel.revertPreparationButton)
            {
                return;
            }

            // in order: Signature Spells charges its picks by their position in the list
            panel.revertPreparationButton.interactable = !panel.preparedSpells.SequenceEqual(_initialPicks);
        }

        // DoValidate prefix: in the mode, Prepare sends the chosen spells as the picks. False: not in the mode
        internal static bool TryPrepare(SpellRepertoirePanel panel)
        {
            var character = panel.GuiCharacter?.RulesetCharacter;

            if (Mode is not { } mode || character is not RulesetCharacterHero hero ||
                panel.SpellRepertoire != mode.Repertoire)
            {
                return false;
            }

            // the repertoire by its spellcasting feature, so every computer changes the same one
            var argument = mode.Repertoire.SpellCastingFeature.Name + "|" + _openedWithPicks + "|" +
                           string.Join(";", panel.preparedSpells.Select(x => x.Name));

            // the after-rest button stays off until this has come back (SpellPreparationScreen)
            SpellPreparationScreen.Submit(panel, hero, $"Set{mode.Tag}", argument, null);

            return true;
        }

        // runs on every computer at the same tick (RestCommandContext): the chosen spells, less the auto-prepared
        // ones, become the picks and leave the prepared spells. Nothing changes unless the repertoire named is the
        // hero's Wizard repertoire, the stored picks are still the ones the screen opened with, and every spell is
        // found. The second check stops a second Prepare pressed before the first came back: each press takes its
        // picks out of the prepared spells, so the first press's picks would stay out even if the second didn't keep
        // them. Only the first press of a screen can apply, alike on every computer.
        // spellcasting feature|stored picks when the screen opened|spell;spell...
        private static bool SetPicks(RulesetCharacterHero hero, string tag, string argument)
        {
            var parts = argument.Split(['|'], 3);
            var repertoire = GetRepertoire(hero);
            var database = DatabaseRepository.GetDatabase<SpellDefinition>();
            var chosen = new List<SpellDefinition>();

            if (parts.Length != 3 || repertoire == null || repertoire.SpellCastingFeature.Name != parts[0])
            {
                Main.Error($"[RestCommand] {tag} for {hero.Name}: not the Wizard repertoire ({argument}); nothing changed");

                return false;
            }

            if (StoredPicks(repertoire, tag) != parts[1])
            {
                Main.Info($"[RestCommand] {tag} for {hero.Name}: the picks changed since that screen opened; not applied");

                return false;
            }

            foreach (var name in parts[2].Split([';'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!database.TryGetElement(name, out var spell))
                {
                    Main.Error($"[RestCommand] {tag} for {hero.Name}: no spell {name}; nothing changed");

                    return false;
                }

                chosen.Add(spell);
            }

            var picks = chosen.Except(repertoire.AutoPreparedSpells).ToList();

            // the feature's own limits, checked here too, so no screen path can store picks the screen wouldn't allow
            // (a controller's Auto button reaches the game's usual preparation): at most 2,
            // Spell Mastery levels 1 and 2, Signature Spells level 3. Alike on every computer
            if (picks.Count > 2 || picks.Any(x => tag == "SignatureSpells" ? x.SpellLevel != 3 : x.SpellLevel is not (1 or 2)))
            {
                Main.Error($"[RestCommand] {tag} for {hero.Name}: picks outside the feature's limits; nothing changed");

                return false;
            }

            repertoire.ExtraSpellsByTag[tag] = picks;
            repertoire.PreparedSpells.RemoveAll(x => picks.Contains(x));

            return true;
        }

        // the after-rest functor on the clicker: the screen in the feature's mode, here only
        internal static IEnumerator Execute(
            FunctorParametersDescription functorParameters, Functor.FunctorExecutionContext context, string tag)
        {
            var inspectionScreen = Gui.GuiService.GetScreen<CharacterInspectionScreen>();
            var partyStatusScreen = Gui.GuiService.GetScreen<GamePartyStatusScreen>();
            var hero = functorParameters.RestingHero;

            Gui.GuiService.GetScreen<RestModal>().KeepCurrentState = true;

            var spellRepertoire = GetRepertoire(hero);

            if (spellRepertoire == null)
            {
                yield break;
            }

            Mode = (spellRepertoire, tag);

            try
            {
                partyStatusScreen.SetupDisplayPreferences(false, false, false);
                SpellPreparationScreen.Show(inspectionScreen, hero, Gui.GuiService.GetScreen<RestModal>(), spellRepertoire);

                while (context.Async && inspectionScreen.Visible)
                {
                    yield return null;
                }

                // a Prepare press still on its way keeps this feature running, and so its after-rest button off
                while (SpellPreparationScreen.IsPending)
                {
                    yield return null;
                }
            }
            finally
            {
                Mode = null;
                _initialPicks = [];
                partyStatusScreen.SetupDisplayPreferences(true, true, true);
            }
        }
    }

    internal static class WizardSpellMastery
    {
        private const string Mastery = "SpellMastery";

        internal static readonly FeatureDefinition FeatureSpellMastery = FeatureDefinitionBuilder
            .Create("FeatureWizardSpellMastery")
            .SetGuiPresentation(Category.Feature)
            .AddToDB();

        private static readonly RestActivityDefinition RestActivitySpellMastery = RestActivityDefinitionBuilder
            .Create($"RestActivity{Mastery}")
            .SetGuiPresentation(Category.RestActivity)
            .SetRestData(
                RestDefinitions.RestStage.AfterRest,
                RestType.LongRest,
                RestActivityDefinition.ActivityCondition.CanPrepareSpells,
                nameof(FunctorSpellMastery),
                string.Empty)
            .AddToDB();

        internal static bool IsRestActivityAvailable(RestActivityDefinition activity, RulesetCharacterHero hero)
        {
            return activity != RestActivitySpellMastery || hero.GetClassLevel(Wizard) >= 18;
        }

        internal static bool IsInvalidSelectedSpell(RulesetSpellRepertoire repertoire, SpellDefinition spell)
        {
            return TaggedSpellPreparation.IsMode(repertoire, Mastery) && spell.SpellLevel is not (1 or 2);
        }

        internal static bool IsPreparation(RulesetSpellRepertoire repertoire, out int maxPreparedSpell)
        {
            maxPreparedSpell = 2;

            return TaggedSpellPreparation.IsMode(repertoire, Mastery);
        }

        internal static bool ShouldConsumeSlot(RulesetCharacter caster, RulesetEffectSpell activeSpell)
        {
            if (!activeSpell.SpellRepertoire.ExtraSpellsByTag.TryGetValue(Mastery,
                    out var signaturePreparedSpells) ||
                !signaturePreparedSpells.Contains(activeSpell.SpellDefinition) ||
                activeSpell.EffectLevel != activeSpell.SpellDefinition.SpellLevel)
            {
                return true;
            }

            caster.LogCharacterUsedFeature(FeatureSpellMastery);

            return false;
        }

        internal static FeatureDefinition BuildWizardSpellMastery()
        {
            // no longer used (the mode is the screen's, TaggedSpellPreparation); kept so a save holding one loads
            _ = ConditionDefinitionBuilder
                .Create($"Condition{Mastery}")
                .SetGuiPresentationNoContent(true)
                .SetSilent(Silent.WhenAddedOrRemoved)
                .AddToDB();

            ServiceRepository.GetService<IFunctorService>()
                .RegisterFunctor(nameof(FunctorSpellMastery), new FunctorSpellMastery());
            TaggedSpellPreparation.Register(Mastery);

            return FeatureSpellMastery;
        }

        private class FunctorSpellMastery : Functor
        {
            public override IEnumerator Execute(
                FunctorParametersDescription functorParameters,
                FunctorExecutionContext context)
            {
                return TaggedSpellPreparation.Execute(functorParameters, context, Mastery);
            }
        }
    }

    internal static class WizardSignatureSpells
    {
        private const string Signature = "SignatureSpells";

        internal static readonly FeatureDefinitionPower PowerSignatureSpells = FeatureDefinitionPowerBuilder
            .Create("PowerWizardSignatureSpells")
            .SetGuiPresentation(Category.Feature)
            .SetUsesFixed(ActivationTime.NoCost, RechargeRate.ShortRest, 1, 3)
            .AddCustomSubFeatures(ModifyPowerVisibility.Hidden)
            .AddToDB();

        private static readonly RestActivityDefinition RestActivitySignatureSpells = RestActivityDefinitionBuilder
            .Create($"RestActivity{Signature}")
            .SetGuiPresentation(Category.RestActivity)
            .SetRestData(
                RestDefinitions.RestStage.AfterRest,
                RestType.LongRest,
                RestActivityDefinition.ActivityCondition.CanPrepareSpells,
                nameof(FunctorSignatureSpells),
                string.Empty)
            .AddToDB();

        internal static bool IsRestActivityAvailable(RestActivityDefinition activity, RulesetCharacterHero hero)
        {
            return
                activity != RestActivitySignatureSpells ||
                (hero.GetClassLevel(Wizard) >= 20 &&
                 (Main.Settings.EnableSignatureSpellsRelearn ||
                  hero.SpellRepertoires.All(x =>
                      !x.ExtraSpellsByTag.TryGetValue(Signature, out var spells) ||
                      spells.Count == 0)));
        }

        internal static bool IsPreparation(RulesetSpellRepertoire repertoire, out int maxPreparedSpell)
        {
            maxPreparedSpell = 2;

            return TaggedSpellPreparation.IsMode(repertoire, Signature);
        }

        internal static bool IsInvalidSelectedSpell(RulesetSpellRepertoire repertoire, SpellDefinition spell)
        {
            return TaggedSpellPreparation.IsMode(repertoire, Signature) && spell.SpellLevel is not 3;
        }

        internal static bool ShouldConsumeSlot(RulesetCharacter caster, RulesetEffectSpell activeSpell)
        {
            if (activeSpell.EffectLevel != activeSpell.SpellDefinition.SpellLevel)
            {
                return true;
            }

            if (!activeSpell.SpellRepertoire.ExtraSpellsByTag
                    .TryGetValue(Signature, out var signaturePreparedSpells))
            {
                return true;
            }

            var usablePower = PowerProvider.Get(PowerSignatureSpells, caster);

            if (usablePower.remainingUses == 0)
            {
                return true;
            }

            for (var i = 0; i < signaturePreparedSpells.Count; i++)
            {
                if (signaturePreparedSpells[i] == activeSpell.SpellDefinition)
                {
                    switch (i)
                    {
                        case 0 when usablePower.remainingUses == 2:
                        case 1 when usablePower.remainingUses == 1:
                            return true;
                        default:
                            usablePower.remainingUses -= i == 0 ? 1 : 2;
                            caster.LogCharacterUsedFeature(PowerSignatureSpells);
                            return false;
                    }
                }
            }

            return true;
        }

        internal static FeatureDefinition BuildWizardSignatureSpells()
        {
            // no longer used (the mode is the screen's, TaggedSpellPreparation); kept so a save holding one loads
            _ = ConditionDefinitionBuilder
                .Create($"Condition{Signature}")
                .SetGuiPresentationNoContent(true)
                .SetSilent(Silent.WhenAddedOrRemoved)
                .AddToDB();

            ServiceRepository.GetService<IFunctorService>()
                .RegisterFunctor(nameof(FunctorSignatureSpells), new FunctorSignatureSpells());
            TaggedSpellPreparation.Register(Signature);

            return PowerSignatureSpells;
        }

        private class FunctorSignatureSpells : Functor
        {
            public override IEnumerator Execute(
                FunctorParametersDescription functorParameters,
                FunctorExecutionContext context)
            {
                return TaggedSpellPreparation.Execute(functorParameters, context, Signature);
            }
        }
    }

    private sealed class ActionFinishedByMeArchDruid(FeatureDefinition featureDefinition) : IMagicEffectFinishedByMe
    {
        public IEnumerator OnMagicEffectFinishedByMe(
            CharacterAction action,
            GameLocationCharacter attacker,
            List<GameLocationCharacter> targets)
        {
            var source = action.ActionParams.RulesetEffect.SourceDefinition;
            var power = source == PowerDruidWildShape
                ? PowerDruidWildShape
                : source == CircleOfTheNight.PowerCircleOfTheNightWildShapeCombat
                    ? CircleOfTheNight.PowerCircleOfTheNightWildShapeCombat
                    : null;

            if (!power)
            {
                yield break;
            }

            var rulesetCharacter = attacker.RulesetCharacter;
            var usablePower = PowerProvider.Get(power, rulesetCharacter);

            usablePower.Recharge();
            rulesetCharacter.LogCharacterUsedFeature(featureDefinition);
        }
    }

    private sealed class ModifyAbilityCheckBarbarianIndomitableMight : IModifyAbilityCheck
    {
        public void MinRoll(
            RulesetCharacter character,
            int baseBonus,
            string abilityScoreName,
            string proficiencyName,
            List<TrendInfo> advantageTrends,
            List<TrendInfo> modifierTrends,
            ref int rollModifier,
            ref int minRoll)
        {
            if (abilityScoreName == AttributeDefinitions.Strength)
            {
                minRoll = Math.Max(minRoll, character.TryGetAttributeValue(AttributeDefinitions.Strength));
            }
        }
    }

    private sealed class CustomLevelUpLogicBarbarianPrimalChampion : ICustomLevelUpLogic
    {
        public void ApplyFeature([NotNull] RulesetCharacterHero hero, string tag)
        {
            hero.ModifyAttributeAndMax(AttributeDefinitions.Strength, 4);
            hero.ModifyAttributeAndMax(AttributeDefinitions.Constitution, 4);
            hero.RefreshAll();
        }

        public void RemoveFeature([NotNull] RulesetCharacterHero hero, string tag)
        {
            hero.ModifyAttributeAndMax(AttributeDefinitions.Strength, -4);
            hero.ModifyAttributeAndMax(AttributeDefinitions.Constitution, -4);
            hero.RefreshAll();
        }
    }

    private sealed class BattleStartedListenerMonkPerfectSelf : ICharacterBattleStartedListener
    {
        public void OnCharacterBattleStarted(GameLocationCharacter locationCharacter, bool surprise)
        {
            var character = locationCharacter.RulesetCharacter;

            if (character is not { RemainingKiPoints: 0 })
            {
                return;
            }

            character.ForceKiPointConsumption(-4);
            character.KiPointsAltered?.Invoke(character, character.RemainingKiPoints);
            character.LogCharacterUsedFeature(FeatureMonkPerfectSelf);
        }
    }

    private sealed class PhysicalAttackInitiatedOnMeRogueElusive : IPhysicalAttackInitiatedOnMe
    {
        public IEnumerator OnPhysicalAttackInitiatedOnMe(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            ActionModifier attackModifier,
            RulesetAttackMode attackMode)
        {
            var rulesetDefender = defender.RulesetCharacter;

            if (rulesetDefender.IsIncapacitated)
            {
                yield break;
            }

            attackModifier.AttackAdvantageTrends.Clear();
            attackModifier.ignoreAdvantage = true;
        }
    }

    private sealed class ModifyWeaponAttackModeRangerFoeSlayer : IModifyWeaponAttackMode
    {
        public void ModifyWeaponAttackMode(
            RulesetCharacter character,
            RulesetAttackMode attackMode,
            RulesetItem weapon,
            bool canAddAbilityDamageBonus)
        {
            var damage = attackMode.EffectDescription.FindFirstDamageForm();

            if (damage == null)
            {
                return;
            }

            var wisdom = character.TryGetAttributeValue(AttributeDefinitions.Wisdom);
            var wisdomModifier = AttributeDefinitions.ComputeAbilityScoreModifier(wisdom);

            damage.BonusDamage += wisdomModifier;
            damage.DamageBonusTrends.Add(
                new TrendInfo(
                    wisdomModifier, FeatureSourceType.CharacterFeature, FeatureFoeSlayer.Name, FeatureFoeSlayer));
        }
    }

    private sealed class ModifyAdditionalDamageRangerFoeSlayer2024 : IModifyAdditionalDamage
    {
        public void ModifyAdditionalDamage(
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            RulesetAttackMode attackMode,
            FeatureDefinitionAdditionalDamage featureDefinitionAdditionalDamage,
            List<EffectForm> actualEffectForms,
            ref DamageForm damageForm)
        {
            if (featureDefinitionAdditionalDamage != AdditionalDamageHuntersMark ||
                attacker.RulesetCharacter.GetClassLevel(Ranger) < 20)
            {
                return;
            }

            damageForm.dieType = DieType.D10;
        }
    }

    private class TryAlterOutcomeAttackRogueStrokeOfLuck(FeatureDefinitionPower power)
        : ITryAlterOutcomeAttack, ITryAlterOutcomeAttributeCheck, ITryAlterOutcomeSavingThrow
    {
        public int HandlerPriority => -10;

        public IEnumerator OnTryAlterOutcomeAttack(
            GameLocationBattleManager battleManager,
            CharacterAction action,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            ActionModifier attackModifier,
            RulesetAttackMode attackMode,
            RulesetEffect rulesetEffect)
        {
            var rulesetHelper = helper.RulesetCharacter;
            var usablePower = PowerProvider.Get(power, rulesetHelper);

            if (action.AttackRollOutcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure) ||
                helper != attacker ||
                rulesetHelper.GetRemainingUsesOfPower(usablePower) == 0)
            {
                yield break;
            }

            yield return helper.MyReactToSpendPower(
                usablePower,
                attacker,
                "RogueStrokeOfLuck",
                reactionValidated: ReactionValidated,
                battleManager: battleManager);

            yield break;

            void ReactionValidated()
            {
                action.AttackRoll = 20;
                action.AttackRollOutcome = RollOutcome.CriticalSuccess;
                action.AttackSuccessDelta = 0;
            }
        }

        public IEnumerator OnTryAlterAttributeCheck(
            GameLocationBattleManager battleManager,
            int rawRoll,
            AbilityCheckData abilityCheckData,
            GameLocationCharacter defender,
            GameLocationCharacter helper)
        {
            var rulesetHelper = helper.RulesetCharacter;
            var usablePower = PowerProvider.Get(power, rulesetHelper);

            if (abilityCheckData.AbilityCheckRollOutcome is not (RollOutcome.Failure or RollOutcome.CriticalFailure) ||
                helper != defender ||
                rulesetHelper.GetRemainingUsesOfPower(usablePower) == 0)
            {
                yield break;
            }

            yield return helper.MyReactToSpendPower(
                usablePower,
                defender,
                "RogueStrokeOfLuck",
                reactionValidated: ReactionValidated,
                battleManager: battleManager);

            yield break;

            void ReactionValidated()
            {
                abilityCheckData.AbilityCheckRoll += 20 - rawRoll;
                abilityCheckData.AbilityCheckSuccessDelta = 0;
                abilityCheckData.AbilityCheckRollOutcome = RollOutcome.CriticalSuccess;
            }
        }

        public IEnumerator OnTryAlterOutcomeSavingThrow(
            GameLocationBattleManager battleManager,
            GameLocationCharacter attacker,
            GameLocationCharacter defender,
            GameLocationCharacter helper,
            SavingThrowData savingThrowData,
            bool hasHitVisual)
        {
            var rulesetHelper = helper.RulesetCharacter;
            var usablePower = PowerProvider.Get(power, rulesetHelper);

            if (savingThrowData.SaveOutcome != RollOutcome.Failure ||
                helper != defender ||
                rulesetHelper.GetRemainingUsesOfPower(usablePower) == 0)
            {
                yield break;
            }

            yield return helper.MyReactToSpendPower(
                usablePower,
                defender,
                "RogueStrokeOfLuck",
                reactionValidated: ReactionValidated,
                battleManager: battleManager);

            yield break;

            void ReactionValidated()
            {
                savingThrowData.SaveOutcomeDelta = 0;
                savingThrowData.SaveOutcome = RollOutcome.Success;
            }
        }
    }

    private sealed class BattleStartedListenerBardSuperiorInspiration(FeatureDefinition featureDefinition)
        : ICharacterBattleStartedListener
    {
        public void OnCharacterBattleStarted(GameLocationCharacter locationCharacter, bool surprise)
        {
            var character = locationCharacter.RulesetCharacter;
            var limit = Main.Settings.EnableBardSuperiorInspiration2024 ? 2 : 1;

            if (character.RemainingBardicInspirations >= limit)
            {
                return;
            }

            while (character.RemainingBardicInspirations < limit)
            {
                character.usedBardicInspiration--;
            }

            character.BardicInspirationAltered?.Invoke(character, character.RemainingBardicInspirations);
            character.LogCharacterUsedFeature(featureDefinition);
        }
    }
}
