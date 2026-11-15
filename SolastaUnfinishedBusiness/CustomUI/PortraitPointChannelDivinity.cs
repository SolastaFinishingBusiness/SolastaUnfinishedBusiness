using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Api;
using System.Linq;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine.AddressableAssets;

namespace SolastaUnfinishedBusiness.CustomUI;

public class PortraitPointChannelDivinity : ICustomPortraitPointPoolProvider
{
    private PortraitPointChannelDivinity()
    {
    }

    public static ICustomPortraitPointPoolProvider Instance { get; } = new PortraitPointChannelDivinity();
    public string Name => "ChannelDivinity";

    public bool IsActive(RulesetCharacter character)
    {
        return true;
    }

    // One line per pool (#68): the shared pool (classes on the 2014 rules) with the game's own text, and the own pool of
    // a class on the 2024 rules with its class name and its rest rule (one use back on a short rest, all on a long one).
    string ICustomPortraitPointPoolProvider.Tooltip(RulesetCharacter character)
    {
        var lines = Tabletop2024Context.GetChannelDivinityPools(character)
            .Select(pool =>
            {
                var max = Tabletop2024Context.GetChannelDivinityMax(character, pool);
                var remaining = Tabletop2024Context.GetChannelDivinityRemaining(character, pool);

                if (pool == Tabletop2024Context.ChannelDivinityPool.Shared)
                {
                    return "ChannelDivinityPortraitPoolFormat".Formatted(Category.Tooltip, remaining, max);
                }

                var classDefinition = pool == Tabletop2024Context.ChannelDivinityPool.Paladin
                    ? DatabaseHelper.CharacterClassDefinitions.Paladin
                    : DatabaseHelper.CharacterClassDefinitions.Cleric;

                return "ChannelDivinityPortraitPoolClassFormat".Formatted(
                    Category.Tooltip, remaining, max, classDefinition.FormatTitle());
            });

        return string.Join("\n", lines);
    }

    public AssetReferenceSprite Icon => Sprites.ChannelDivinityResourceIcon;

    // every pool's remaining uses together (#68)
    public string GetPoints(RulesetCharacter character)
    {
        return $"{Tabletop2024Context.GetChannelDivinityTotalRemaining(character)}";
    }
}
