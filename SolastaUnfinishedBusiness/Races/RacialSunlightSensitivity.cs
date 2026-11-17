using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Models;
using TA;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Races;

/// <summary>
///     Light Sensitivity of Dark Kobolds, Dark Elves and Gray Dwarves, in two rules chosen by the
///     "outdoors only" setting (<c>RaceLightSensitivityApplyOutdoorsOnly</c>, Races menu):
///     <list type="bullet">
///         <item>
///             Off: the original mod rule. Each racial light affinity gives the condition while the hero's own
///             illumination is Bright, indoors or out, a torch or a light spell included. The game's light
///             affinity code (<c>GameLocationCharacter.CheckLightingAffinityEffects</c>, patched in
///             GameLocationCharacterPatcher) applies it in the shared light category.
///         </item>
///         <item>
///             On: the daylight rule here, with a condition of its own, Sunlight Sensitivity, whose tooltip states
///             these terms. It applies while the hero stands in an exterior sector whose
///             hourly outdoor light is Bright (08:00 to 18:00 in Solasta 1.5.97), whatever light sources are near: a
///             torch at night doesn't trigger it. Magical darkness or a heavily obscuring area (Fog Cloud, smoke)
///             around the hero stops it (<see cref="IsShelteredFromSunlight" />). The light affinity code then skips
///             these three features, so the two rules never both apply.
///         </item>
///     </list>
///     Both conditions have the same effects (-1d4 on attack rolls, disadvantage on Wisdom (Perception)).
/// </summary>
[HarmonyPatch(typeof(GameLocationVisibilityManager), nameof(GameLocationVisibilityManager.ComputeIllumination))]
internal static class RacialSunlightSensitivity
{
    // The daylight rule keeps its condition in a category of its own, so the light affinity code, which owns the
    // shared light category, never sees it as surplus and removes it, and the two rules can't remove each other's.
    private const string ConditionCategory = "RacialSunlightSensitivity";
    private static readonly HashSet<FeatureDefinition> RacialFeatures = [];

    // Sunlight Sensitivity, the daylight rule's condition (BuildSunlightSensitivity)
    private static ConditionDefinition SunlightSensitivity;

    // the mod's magical darkness condition (LightingAndObscurementContext, official obscurement rules on)
    private const string ConditionInMagicalDarknessName = "ConditionInMagicalDarkness";

    // reused for the cells a hero occupies; only touched on the main thread during light updates
    private static readonly List<int3> PositionCache = [];

    /// <summary>
    ///     True when the daylight rule is in use. Read at every check, so switching the setting takes effect at each
    ///     hero's next light update (a move, an hour change, a refresh). In a multiplayer room the host's settings are
    ///     in use on every computer.
    /// </summary>
    internal static bool Enabled => Main.Settings.RaceLightSensitivityApplyOutdoorsOnly;

    internal static bool IsRacialFeature(FeatureDefinition feature)
    {
        return RacialFeatures.Contains(feature);
    }

    /// <summary>
    ///     Builds one race's light affinity. It keeps its original name, so its ID in saves and characters is
    ///     unchanged, and its original table (Bright: Light Sensitivity), which the original rule uses while the
    ///     setting is off. The feature is registered so the daylight rule can recognise the race and the light
    ///     affinity code can skip it while the setting is on.
    /// </summary>
    internal static FeatureDefinitionLightAffinity Build(string name)
    {
        SunlightSensitivity ??= BuildSunlightSensitivity();

        var feature = FeatureDefinitionLightAffinityBuilder.Create(name)
            .SetGuiPresentation(CustomConditionsContext.LightSensitivity.Name, Category.Condition)
            .AddLightingEffectAndCondition(
                new FeatureDefinitionLightAffinity.LightingEffectAndCondition
                {
                    lightingState = LocationDefinitions.LightingState.Bright,
                    condition = CustomConditionsContext.LightSensitivity
                })
            .AddToDB();

        RacialFeatures.Add(feature);

        return feature;
    }

    /// <summary>
    ///     The daylight rule's condition, Sunlight Sensitivity (a condition of its own for
    ///     these races, whose tooltip says when it applies). It copies Light Sensitivity, so its effects are the same
    ///     (-1d4 on attack rolls, disadvantage on Wisdom (Perception)), shown as detrimental and silent when added or
    ///     removed; only its name and description differ. The original rule (setting off) keeps Light Sensitivity.
    ///     Built once, on the first racial feature, after CustomConditionsContext has made Light Sensitivity.
    /// </summary>
    private static ConditionDefinition BuildSunlightSensitivity()
    {
        var condition = ConditionDefinitionBuilder
            .Create(CustomConditionsContext.LightSensitivity, "ConditionSunlightSensitivity")
            .SetOrUpdateGuiPresentation(Category.Condition)
            .AddToDB();

        // set both terms explicitly: the copy keeps Light Sensitivity's description term otherwise
        condition.GuiPresentation.title = "Condition/&ConditionSunlightSensitivityTitle";
        condition.GuiPresentation.description = "Condition/&ConditionSunlightSensitivityDescription";

        return condition;
    }

    [UsedImplicitly]
    public static void Postfix(IIlluminable illuminable, GameLocationCharacter previewCharacter)
    {
        // Previews (attack previews, hovered cells, character labels) run on one computer only and must not change
        // a character or use an object ID; the game's real update passes no preview character. Only a hero whose
        // subrace has one of the three racial features is handled. A hero in a shape change (wild shape,
        // polymorph) is a monster here and is skipped until back in the hero's form.
        if (previewCharacter != null ||
            illuminable is not GameLocationCharacter { RulesetCharacter: RulesetCharacterHero hero } character ||
            hero.SubRaceDefinition?.FeatureUnlocks.Exists(x => RacialFeatures.Contains(x.FeatureDefinition)) != true)
        {
            return;
        }

        var lightSensitivity = CustomConditionsContext.LightSensitivity;
        var sensitivity = SunlightSensitivity;

        // Development builds of this rule (1.13.0-dev.102e1124 to 674f98f6) gave Light Sensitivity in this category,
        // and a save made with one ("TEST - Combat Hamlet wolves") keeps it. This category only ever holds the
        // daylight rule's condition, so anything else in it is such a leftover and is removed.
        if (hero.ConditionsByCategory.TryGetValue(ConditionCategory, out var stale) &&
            stale.FirstOrDefault(x => x.ConditionDefinition != sensitivity) is { } leftover)
        {
            hero.RemoveConditionOfCategory(ConditionCategory, leftover, true, false, true);
        }

        var active = hero.ConditionsByCategory.TryGetValue(ConditionCategory, out var conditions)
            ? conditions.FirstOrDefault(x => x.ConditionDefinition == sensitivity)
            : null;

        // Setting off: the original rule applies through the light affinity code. Only a daylight-rule condition left
        // from while the setting was on (or in a save made then) is removed; nothing else is touched.
        if (!Enabled)
        {
            if (active != null)
            {
                hero.RemoveConditionOfCategory(ConditionCategory, active, true, false, true);
            }

            return;
        }

        var location = ServiceRepository.GetService<IGameLocationService>()?.GameLocation;

        if (location?.SectorBounds == null)
        {
            return;
        }

        // Exterior sectors receive their global light from the campaign hour
        // (GameLocationVisibilityManager.UpdateGlobalIlluminationState, which also marks every character for a light
        // update when it changes). Torches and other light sources don't enter this test. A position outside every
        // sector counts as not exposed.
        var sector = new GridAccessor(character.LocationPosition).sector;
        var exposed = sector is { IsExterior: true, GlobalLightingState: LocationDefinitions.LightingState.Bright } &&
                      !IsShelteredFromSunlight(character, hero);

        // Light Sensitivity given by the original rule (an older save, or a check made while the setting was off) sits
        // in the shared light category. Remove that one condition only, leaving other light affinities' conditions.
        if (hero.ConditionsByCategory.TryGetValue(AttributeDefinitions.TagLightSensitivity, out var previous) &&
            previous.FirstOrDefault(x => x.ConditionDefinition == lightSensitivity) is { } legacy)
        {
            hero.RemoveConditionOfCategory(AttributeDefinitions.TagLightSensitivity, legacy, true, false, true);
        }

        // Change only what differs, so repeated checks use no new object ID (object-ID desyncs in multiplayer).
        if (exposed && active == null)
        {
            hero.AddConditionOfCategory(ConditionCategory, RulesetCondition.CreateActiveCondition(
                hero.Guid, sensitivity, DurationType.Irrelevant, 0, TurnOccurenceType.StartOfTurn,
                character.Guid, hero.CurrentFaction.Name), true);
        }
        else if (!exposed && active != null)
        {
            hero.RemoveConditionOfCategory(ConditionCategory, active, true, false, true);
        }
    }

    /// <summary>
    ///     True when magical darkness or an area that fully obscures the hero keeps the sun off them: the Darkness
    ///     spell and other magical darkness, Fog Cloud, smoke and the other heavily obscuring clouds. These are the
    ///     same two signs the game's own light check (<c>GameLocationVisibilityManager.ComputeIllumination</c>) tests
    ///     first:
    ///     <list type="bullet">
    ///         <item>
    ///             A darkness condition on the hero: the game's <c>ConditionDarkness</c>, which Darkness, Way of
    ///             Shadow's Darkness and Maddening Darkness give with the official obscurement rules off, or the
    ///             mod's <c>ConditionInMagicalDarkness</c>, which they give with those rules on
    ///             (LightingAndObscurementContext.SwitchMagicalDarknessOnObscurementRules).
    ///         </item>
    ///         <item>
    ///             A cell the hero occupies marked sight impaired (<c>CellFlags.Runtime.DynamicSightImpaired</c>). The
    ///             game sets it on every cell covered by an effect with a sight-impaired topology form: heavily
    ///             obscuring areas such as Fog Cloud, Stinking Cloud, Cloudkill, Sleet Storm, Petal Storm and smoke.
    ///             Lightly obscuring areas don't set it (the mod's Insect Plague under official rules), so they don't
    ///             shelter a hero, as dim light or light fog doesn't.
    ///         </item>
    ///     </list>
    ///     A hero standing in such an area at noon loses the penalty; it comes back when they leave it or it ends,
    ///     at the hero's next light update.
    /// </summary>
    private static bool IsShelteredFromSunlight(GameLocationCharacter character, RulesetCharacterHero hero)
    {
        if (hero.HasConditionOfType(ConditionDarkness) ||
            hero.HasConditionOfType(ConditionInMagicalDarknessName))
        {
            return true;
        }

        // A large hero occupies several cells; any one of them inside the area counts, as in the game's check.
        PositionCache.Clear();
        ((IIlluminable)character).GetAllPositionsToCheck(PositionCache);

        foreach (var position in PositionCache)
        {
            if ((new GridAccessor(position).RuntimeFlags(position) & CellFlags.Runtime.DynamicSightImpaired) != 0)
            {
                return true;
            }
        }

        return false;
    }
}
