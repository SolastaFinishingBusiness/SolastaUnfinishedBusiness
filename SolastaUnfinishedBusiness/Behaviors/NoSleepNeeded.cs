namespace SolastaUnfinishedBusiness.Behaviors;

/// <summary>
///     Marks a feature whose holder needs no sleep. The game takes a hero's sleep hours from
///     RulesetCharacterHero.ComputeRequiredSleepHours (6, lowered only by a camp affinity's override above 0, so an
///     override of 0 can't express it); RulesetCharacterHeroPatcher makes it return 0 for a holder of this marker. Rests
///     and travel camps then give the hero no sleep hours: the rest's length is unchanged, and the hours the hero would
///     have slept are free for crafting.
/// </summary>
internal sealed class NoSleepNeeded
{
    private NoSleepNeeded()
    {
    }

    internal static NoSleepNeeded Marker { get; } = new();
}
