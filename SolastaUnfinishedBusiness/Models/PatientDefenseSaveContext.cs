using System.Collections.Generic;
using System.Linq;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.FeatureDefinitionPowers;

namespace SolastaUnfinishedBusiness.Models;

internal static class PatientDefenseSaveContext
{
    private const string Marker = "FbMonkPatientDefenseClassGrant";
    private static string Tag => AttributeDefinitions.GetClassTag(Monk, 2);

    private static bool CanAlias(Dictionary<string, List<FeatureDefinition>> features)
    {
        var wrapper = FixesContext.FeatureSetMonkPatientDefense;
        return wrapper.FeatureSet.Count == 1 && wrapper.FeatureSet[0] == PowerMonkPatientDefense &&
               features.TryGetValue(Tag, out var grants) && grants.Contains(wrapper) &&
               !grants.Contains(PowerMonkPatientDefense);
    }

    // Only the class-grant writer sees this snapshot. The live feature list, feat grants and the
    // usable power keep their identity even if the serializer fails midway through a character.
    internal static Dictionary<string, List<FeatureDefinition>> ForWrite(
        Dictionary<string, List<FeatureDefinition>> features, IElementsSerializer serializer)
    {
        if (serializer is not BinarySerializer { persistentContext: true } || !CanAlias(features))
        {
            return features;
        }

        return new Dictionary<string, List<FeatureDefinition>>(features)
        {
            [Tag] = features[Tag].Select(x => x == FixesContext.FeatureSetMonkPatientDefense
                ? PowerMonkPatientDefense : x).ToList()
        };
    }

    internal static void SerializeMarker(RulesetCharacterHero hero, IElementsSerializer serializer)
    {
        if (serializer is not BinarySerializer binary)
        {
            return;
        }

        if (serializer.Mode == Serializer.SerializationMode.Write)
        {
            var aliased = binary.persistentContext && CanAlias(hero.ActiveFeatures);
            if (aliased || !binary.persistentContext)
            {
                serializer.SerializeElement(Marker, aliased);
            }

            return;
        }

        // The network already transmits the wrapper directly. A disk marker distinguishes this
        // alias from the identical grant in a character created in vanilla, even after Focus changes.
        if ((!binary.persistentContext || LevelDownContext.IsNextElement(binary, Marker)) &&
            serializer.SerializeElement(Marker, false) && hero.ActiveFeatures.TryGetValue(Tag, out var grants))
        {
            var index = grants.IndexOf(PowerMonkPatientDefense);
            if (index >= 0)
            {
                grants[index] = FixesContext.FeatureSetMonkPatientDefense;
            }
        }
    }
}
