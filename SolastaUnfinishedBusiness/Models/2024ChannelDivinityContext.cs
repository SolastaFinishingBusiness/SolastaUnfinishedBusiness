using System;
using System.Collections.Generic;
using System.Linq;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.CharacterClassDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Channel Divinity pools and their maximum (issue #68).
//
// The game keeps one Channel Divinity pool per character: a spent count (RulesetCharacter.UsedChannelDivinity) and a
// maximum (the attribute ChannelDivinityNumber). Under the 2014 rules that is right for a Paladin/Cleric: one pool, as
// many uses as the higher of the two classes gives. Under the 2024 rules each class has its own uses, so a class whose
// 2024 option is on (EnablePaladinChannelDivinity2024, EnableClericChannelDivinity2024) gets a pool of its own here,
// and only the classes on the 2014 rules share the game's pool.
//
// The maximum is worked out every time from the current options and class levels, not read from the attribute: a hero
// keeps its own copy of the class feature's modifier (for example Set 2 for a Paladin levelled with the 2024 option
// on), and switching the option doesn't change that copy, so the attribute follows the option as it was at level-up.
// The saved copy is never rewritten, so the save keeps the value it was made with; it is only ignored for the class
// part. Modifiers from anything else, such as the Blessed Soul feat's +1, still count.
public static partial class Tabletop2024Context
{
    internal enum ChannelDivinityPool
    {
        Shared,
        Paladin,
        Cleric
    }

    // the spent count of each 2024 pool lives in the amount of a hidden permanent condition, so saves keep it; its own
    // category keeps rests, dispels and "remove all effects" from touching it
    private const string ChannelDivinityPoolCategory = "ChannelDivinityPool";

    private static readonly ConditionDefinition ConditionChannelDivinityUsedPaladin = ConditionDefinitionBuilder
        .Create("ConditionChannelDivinityUsedPaladin")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .AddToDB();

    private static readonly ConditionDefinition ConditionChannelDivinityUsedCleric = ConditionDefinitionBuilder
        .Create("ConditionChannelDivinityUsedCleric")
        .SetGuiPresentationNoContent(true)
        .SetSilent(Silent.WhenAddedOrRemoved)
        .AddToDB();

    private static int PaladinChannelDivinityLevel(RulesetCharacter character)
    {
        var level = character.GetClassLevel(Paladin);

        return level >= 3 ? level : 0;
    }

    private static int ClericChannelDivinityLevel(RulesetCharacter character)
    {
        var level = character.GetClassLevel(Cleric);

        return level >= 2 ? level : 0;
    }

    private static bool IsPaladinChannelDivinity2024(RulesetCharacter character)
    {
        return Main.Settings.EnablePaladinChannelDivinity2024 && PaladinChannelDivinityLevel(character) > 0;
    }

    private static bool IsClericChannelDivinity2024(RulesetCharacter character)
    {
        return Main.Settings.EnableClericChannelDivinity2024 && ClericChannelDivinityLevel(character) > 0;
    }

    // true when a class gives the character Channel Divinity: then the maximum is worked out here; otherwise (a
    // monster, a wild shape, Channel Divinity from something else) the game's own attribute is left as it is
    private static bool HasClassChannelDivinity(RulesetCharacter character)
    {
        return PaladinChannelDivinityLevel(character) > 0 || ClericChannelDivinityLevel(character) > 0;
    }

    // Paladin 3+: 1 use under the 2014 rules; 2 under the 2024 rules, 3 from level 11
    private static int PaladinClassUses(int level, bool rules2024)
    {
        if (level == 0)
        {
            return 0;
        }

        if (!rules2024)
        {
            return 1;
        }

        return level >= 11 ? 3 : 2;
    }

    // Cleric 2+: 1, 2 at level 6 and 3 at 18 under the 2014 rules; 2, 3 and 4 under the 2024 rules
    private static int ClericClassUses(int level, bool rules2024)
    {
        if (level == 0)
        {
            return 0;
        }

        var uses = level >= 18 ? 3 : level >= 6 ? 2 : 1;

        return rules2024 ? uses + 1 : uses;
    }

    // which pool a bonus to ChannelDivinityNumber from something other than a class feature belongs to, from its
    // feature tags (a feat's tag carries the feat's name, such as Blessed Soul's Cleric and Paladin versions)
    private static ChannelDivinityPool PoolOfBonusTags(RulesetCharacter character, List<string> tags)
    {
        if (tags.Any(t => t.Contains("Paladin")) && IsPaladinChannelDivinity2024(character))
        {
            return ChannelDivinityPool.Paladin;
        }

        if (tags.Any(t => t.Contains("Cleric")) && IsClericChannelDivinity2024(character))
        {
            return ChannelDivinityPool.Cleric;
        }

        return ChannelDivinityPool.Shared;
    }

    // additive bonuses to the attribute that don't come from the Paladin's or the Cleric's class or subclass features
    // (those are replaced by the numbers above)
    private static int ChannelDivinityBonus(RulesetCharacter character, ChannelDivinityPool pool)
    {
        var attribute = character.GetAttribute(AttributeDefinitions.ChannelDivinityNumber, true);

        if (attribute == null)
        {
            return 0;
        }

        var bonus = 0;

        foreach (var modifier in attribute.ActiveModifiers)
        {
            if (modifier.Operation != FeatureDefinitionAttributeModifier.AttributeModifierOperation.Additive)
            {
                continue;
            }

            var tags = modifier.Tags;

            if (tags.Any(t =>
                    t.StartsWith(AttributeDefinitions.TagClass + "Paladin", StringComparison.Ordinal) ||
                    t.StartsWith(AttributeDefinitions.TagClass + "Cleric", StringComparison.Ordinal) ||
                    t.StartsWith(AttributeDefinitions.TagSubclass, StringComparison.Ordinal)))
            {
                continue;
            }

            if (PoolOfBonusTags(character, tags) == pool)
            {
                bonus += (int)modifier.Value;
            }
        }

        return bonus;
    }

    /// <summary>The maximum uses of a pool, from the current options and class levels, plus bonuses.</summary>
    internal static int GetChannelDivinityMax(RulesetCharacter character, ChannelDivinityPool pool)
    {
        if (!HasClassChannelDivinity(character))
        {
            return pool == ChannelDivinityPool.Shared
                ? character.TryGetAttributeValue(AttributeDefinitions.ChannelDivinityNumber)
                : 0;
        }

        var paladinLevel = PaladinChannelDivinityLevel(character);
        var clericLevel = ClericChannelDivinityLevel(character);
        var paladin2024 = IsPaladinChannelDivinity2024(character);
        var cleric2024 = IsClericChannelDivinity2024(character);

        switch (pool)
        {
            case ChannelDivinityPool.Paladin:
                return paladin2024
                    ? PaladinClassUses(paladinLevel, true) + ChannelDivinityBonus(character, pool)
                    : 0;
            case ChannelDivinityPool.Cleric:
                return cleric2024
                    ? ClericClassUses(clericLevel, true) + ChannelDivinityBonus(character, pool)
                    : 0;
            default:
                // the 2014 rule for the classes still sharing the game's pool: the higher of their numbers
                var classUses = Math.Max(
                    paladin2024 ? 0 : PaladinClassUses(paladinLevel, false),
                    cleric2024 ? 0 : ClericClassUses(clericLevel, false));

                var bonus = ChannelDivinityBonus(character, pool);

                return classUses > 0 || bonus > 0 ? classUses + bonus : 0;
        }
    }

    private static ConditionDefinition ChannelDivinityUsedCondition(ChannelDivinityPool pool)
    {
        return pool == ChannelDivinityPool.Paladin
            ? ConditionChannelDivinityUsedPaladin
            : ConditionChannelDivinityUsedCleric;
    }

    internal static int GetChannelDivinityUsed(RulesetCharacter character, ChannelDivinityPool pool)
    {
        if (pool == ChannelDivinityPool.Shared)
        {
            return character.UsedChannelDivinity;
        }

        return character.TryGetConditionOfCategoryAndType(
            ChannelDivinityPoolCategory, ChannelDivinityUsedCondition(pool).Name, out var condition)
            ? condition.Amount
            : 0;
    }

    private static void SetChannelDivinityUsed(RulesetCharacter character, ChannelDivinityPool pool, int used)
    {
        used = Math.Max(0, used);

        if (pool == ChannelDivinityPool.Shared)
        {
            character.UsedChannelDivinity = used;

            return;
        }

        var conditionName = ChannelDivinityUsedCondition(pool).Name;

        if (character.TryGetConditionOfCategoryAndType(ChannelDivinityPoolCategory, conditionName, out var condition))
        {
            condition.Amount = used;

            return;
        }

        if (used == 0)
        {
            return;
        }

        // InflictCondition's amount is only a source amount; the condition's own amount comes from its AmountOrigin,
        // which these conditions don't set, so it starts at 0 and is set here (else a pool's first use would be free)
        var newCondition = character.InflictCondition(
            conditionName,
            DurationType.Permanent,
            0,
            TurnOccurenceType.EndOfTurn,
            ChannelDivinityPoolCategory,
            character.Guid,
            character.CurrentFaction.Name,
            1,
            conditionName,
            used,
            0,
            0);

        newCondition.Amount = used;
    }

    internal static int GetChannelDivinityRemaining(RulesetCharacter character, ChannelDivinityPool pool)
    {
        return Math.Max(0, GetChannelDivinityMax(character, pool) - GetChannelDivinityUsed(character, pool));
    }

    /// <summary>The pool a class's Channel Divinity spends from: its own when its 2024 option is on.</summary>
    internal static ChannelDivinityPool GetChannelDivinityPool(
        RulesetCharacter character, CharacterClassDefinition originClass)
    {
        if (originClass == Paladin && IsPaladinChannelDivinity2024(character))
        {
            return ChannelDivinityPool.Paladin;
        }

        if (originClass == Cleric && IsClericChannelDivinity2024(character))
        {
            return ChannelDivinityPool.Cleric;
        }

        return ChannelDivinityPool.Shared;
    }

    internal static ChannelDivinityPool GetChannelDivinityPool(
        RulesetCharacter character, FeatureDefinitionPower power)
    {
        var usablePower = character.UsablePowers.FirstOrDefault(x => x.PowerDefinition == power);

        return GetChannelDivinityPool(character, ChannelDivinityOriginClass(character, power, usablePower?.OriginClass));
    }

    // The class a Channel Divinity power belongs to. A bundle's sub-power (such as Divine Spark's Heal, Necrotic and
    // Radiant) has no class origin of its own, only its master power does, so the master's is used; without this the
    // sub-powers would fall into the shared pool, which has no uses when every Channel Divinity class is on the 2024
    // rules (Divine Spark's choices would be greyed out).
    private static CharacterClassDefinition ChannelDivinityOriginClass(
        RulesetCharacter character, FeatureDefinitionPower power, CharacterClassDefinition originClass)
    {
        if (originClass)
        {
            return originClass;
        }

        foreach (var master in Behaviors.Specific.PowerBundle.GetMasterPowersBySubPower(power))
        {
            var masterUsablePower = character.UsablePowers.FirstOrDefault(x => x.PowerDefinition == master);

            if (masterUsablePower?.OriginClass)
            {
                return masterUsablePower.OriginClass;
            }
        }

        return null;
    }

    /// <summary>The pools a character has uses in, for the portrait counter and the reaction cost.</summary>
    internal static IEnumerable<ChannelDivinityPool> GetChannelDivinityPools(RulesetCharacter character)
    {
        return Enum.GetValues(typeof(ChannelDivinityPool))
            .Cast<ChannelDivinityPool>()
            .Where(pool => GetChannelDivinityMax(character, pool) > 0);
    }

    internal static int GetChannelDivinityTotalMax(RulesetCharacter character)
    {
        return GetChannelDivinityPools(character).Sum(pool => GetChannelDivinityMax(character, pool));
    }

    internal static int GetChannelDivinityTotalRemaining(RulesetCharacter character)
    {
        return GetChannelDivinityPools(character).Sum(pool => GetChannelDivinityRemaining(character, pool));
    }

    /// <summary>Spends one use from a pool (for features that spend Channel Divinity themselves).</summary>
    internal static void SpendChannelDivinity(RulesetCharacter character, ChannelDivinityPool pool)
    {
        SetChannelDivinityUsed(character, pool, GetChannelDivinityUsed(character, pool) + 1);
    }

    // RulesetCharacter.UsePower postfix: the game has just added one to the shared count for a Channel Divinity power;
    // when the power's class has its own pool, the use moves there
    internal static void MoveChannelDivinityUse(RulesetCharacter character, RulesetUsablePower usablePower)
    {
        if (usablePower.PowerDefinition.RechargeRate != RechargeRate.ChannelDivinity)
        {
            return;
        }

        var pool = GetChannelDivinityPool(character,
            ChannelDivinityOriginClass(character, usablePower.PowerDefinition, usablePower.OriginClass));

        if (pool == ChannelDivinityPool.Shared)
        {
            return;
        }

        character.UsedChannelDivinity = Math.Max(0, character.UsedChannelDivinity - 1);
        SpendChannelDivinity(character, pool);
    }

    // RulesetCharacter.GetRemainingUsesOfPower / GetMaxUsesOfPower postfixes for Channel Divinity powers
    internal static void ModifyChannelDivinityUses(
        RulesetCharacter character, RulesetUsablePower usablePower, ref int result, bool remaining)
    {
        if (usablePower.PowerDefinition.RechargeRate != RechargeRate.ChannelDivinity ||
            !HasClassChannelDivinity(character))
        {
            return;
        }

        var pool = GetChannelDivinityPool(character,
            ChannelDivinityOriginClass(character, usablePower.PowerDefinition, usablePower.OriginClass));

        result = remaining
            ? GetChannelDivinityRemaining(character, pool) / Math.Max(1, usablePower.PowerDefinition.CostPerUse)
            : GetChannelDivinityMax(character, pool);
    }

    // RulesetCharacter.ApplyRest postfix. The game sets the shared count to 0 on any rest (the 2014 rule, and the shared
    // pool only holds 2014 classes now). The 2024 pools: one use back on a short rest, all on a long rest.
    internal static void RestChannelDivinityPools(RulesetCharacter character, RestType restType, bool simulate)
    {
        foreach (var pool in new[] { ChannelDivinityPool.Paladin, ChannelDivinityPool.Cleric })
        {
            var used = GetChannelDivinityUsed(character, pool);

            if (used == 0)
            {
                continue;
            }

            // A pool keeps its spent count while its class's 2024 option is off (the count shows again if the option
            // is turned back on), and it still recovers on a rest so those uses aren't left spent until then.
            if (!simulate)
            {
                SetChannelDivinityUsed(character, pool, restType == RestType.ShortRest ? used - 1 : 0);
            }

            // Such an unused pool isn't shown anywhere, so it isn't listed either: otherwise the rest screen shows
            // "Channel Divinity" a second time beside the game's own line for the shared pool (e.g. with both options
            // off and a use left in the Paladin pool from earlier).
            if (GetChannelDivinityMax(character, pool) == 0)
            {
                continue;
            }

            // the rest screen lists the recovered Channel Divinity under its class
            character.recoveredFeatures.Add(pool == ChannelDivinityPool.Paladin
                ? Api.DatabaseHelper.FeatureDefinitionAttributeModifiers.AttributeModifierPaladinChannelDivinity
                : Api.DatabaseHelper.FeatureDefinitionAttributeModifiers.AttributeModifierClericChannelDivinity);
        }
    }
}
