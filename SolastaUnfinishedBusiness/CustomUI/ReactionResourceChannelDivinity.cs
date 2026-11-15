using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Interfaces;
using UnityEngine.AddressableAssets;

namespace SolastaUnfinishedBusiness.CustomUI;

public class ReactionResourceChannelDivinity : ICustomReactionResource
{
    private ReactionResourceChannelDivinity()
    {
    }

    public static ReactionResourceChannelDivinity Instance { get; } = new();

    public AssetReferenceSprite Icon => Sprites.ChannelDivinityResourceIcon;

    public string GetUses(RulesetCharacter character)
    {
        // every pool's remaining uses together: the shared one and any 2024 class's own (#68)
        return $"{Tabletop2024Context.GetChannelDivinityTotalRemaining(character)}";
    }
}
