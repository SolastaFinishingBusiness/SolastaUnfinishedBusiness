using System;
using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Models;

/// <summary>
///     Combines the concentration checks of one damage instance. The game rolls a concentration save for each damage
///     form a creature takes (GameLocationCharacter.CheckConcentration, from DamageSustained and
///     DamageAbsorbedByTemporaryHitPoints), so one hit with two damage types gives two saves. With the
///     CombineConcentrationChecks setting, a scope is opened around the synchronous application of one damage instance's
///     forms: one attack's hit with all its riders, one target entry of a spell or power (a Magic Missile dart, a Radiant
///     Motes mote and an Eldritch Blast beam are each their own entry), or one recurring tick of a condition. Inside the
///     scope each still-conscious creature's checks only add up their damage; when the scope closes each such creature
///     makes one check against the total, without a damage type. Losing consciousness still ends concentration at once,
///     through the game's own path. Every computer runs the same scopes in the same order, so the single roll happens
///     at the same point of the shared dice sequence.
/// </summary>
internal static class ConcentrationDamageContext
{
    private static readonly Stack<List<(GameLocationCharacter character, int damage)>> Scopes = new();

    // set while the combined check of a closing scope is rolled, so that check is not gathered again
    private static bool _rollingCombined;

    private static bool IsActive => Main.Settings.CombineConcentrationChecks && Scopes.Count > 0 && !_rollingCombined;

    /// <summary>Runs one damage instance's application inside a scope and then rolls its combined checks.</summary>
    internal static T Apply<T>(Func<T> apply)
    {
        if (!Main.Settings.CombineConcentrationChecks)
        {
            return apply();
        }

        Scopes.Push([]);

        List<(GameLocationCharacter character, int damage)> gathered;

        try
        {
            return apply();
        }
        finally
        {
            gathered = Scopes.Pop();
            RollCombined(gathered);
        }
    }

    /// <summary>
    ///     Called from the CheckConcentration prefix for a creature still conscious: adds the damage to the open scope
    ///     and returns true (the game's own check is then skipped), or returns false outside a scope.
    /// </summary>
    internal static bool TryGather(GameLocationCharacter character, int damage)
    {
        if (!IsActive)
        {
            return false;
        }

        var scope = Scopes.Peek();
        var index = scope.FindIndex(x => x.character == character);

        if (index < 0)
        {
            scope.Add((character, damage));
        }
        else
        {
            scope[index] = (character, scope[index].damage + damage);
        }

        return true;
    }

    private static void RollCombined(List<(GameLocationCharacter character, int damage)> gathered)
    {
        _rollingCombined = true;

        try
        {
            foreach (var (character, damage) in gathered)
            {
                // the creature may have lost concentration or consciousness later in the same instance; the game's
                // check returns at once without a concentrated spell
                if (character.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false } rulesetCharacter)
                {
                    continue;
                }

                character.CheckConcentration(rulesetCharacter, damage, string.Empty, true);
            }
        }
        finally
        {
            _rollingCombined = false;
        }
    }
}
