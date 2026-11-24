namespace SolastaUnfinishedBusiness.Interfaces;

/// <summary>
///     Lets a power's tooltip text follow the effect the tooltip shows. GuiPowerDefinition.Description otherwise returns
///     the text fixed when the power was built, while the effect it shows (GuiPowerDefinition.EffectDescription) already
///     has the selected character's IModifyEffectDescription changes applied.
/// </summary>
public interface IModifyPowerDescription
{
    /// <param name="power">the power the tooltip is for</param>
    /// <param name="effectDescription">the effect as the tooltip shows it, with the character's modifiers applied</param>
    /// <param name="description">the text the game would show</param>
    /// <returns>the text to show instead</returns>
    public string GetDescription(
        FeatureDefinitionPower power,
        EffectDescription effectDescription,
        string description);
}
