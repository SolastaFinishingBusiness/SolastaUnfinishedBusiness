using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Behaviors.Specific;

internal static class PowerBundle
{
    private static readonly Dictionary<SpellDefinition, FeatureDefinitionPower> Spells2Powers = [];
    private static readonly Dictionary<FeatureDefinitionPower, SpellDefinition> Powers2Spells = [];
    private static readonly Dictionary<FeatureDefinitionPower, Bundle> Bundles = [];

    // Modified effects are kept per live effect, not per power or spell. A cache keyed by caster, definition and
    // metamagic only, emptied only by RefreshAll, would have three faults:
    // - later uses of a power would get the first use's result, although modifiers read things that change between uses: the
    //   points spent on this use, a toggle such as College of Audacity's Masterful Whirl, remaining movement;
    // - a tooltip would fill it (with no live effect, so modifiers skip what depends on one), and the next real use on
    //   that computer would get the tooltip's result. Tooltips run on one computer only, so computers could disagree on rules;
    // - how long a result lasted would depend on which computer's panel called RefreshAll.
    // Each use makes a new RulesetEffect, so it gets a result worked out for it. The object stays the same for that
    // effect's life, because code changes a live effect's description and expects the change to last (gambits' RangeType,
    // the teleport cursor's targeting). RefreshAll still drops a caster's results: it moves the caster's
    // generation on, and a result from an older generation is worked out again.
    // Held by weak keys, so this table never decides how long an effect lives. Many effects never report ending: a spell
    // targeted and then cancelled, or one made only to read its forms or to preview a position. Whatever holds such an effect
    // goes, and its result goes with it. Not a ConditionalWeakTable, which never lets go on the game's Boehm runtime (see
    // WeakKeyTable). Nothing in an entry refers to the effect, which that needs.
    // Each map load and the game's end also clear it, to reset the generations below. An effect that's still going then gets
    // its result worked out again, as after any RefreshAll.
    private static readonly WeakKeyTable<RulesetEffect, Dictionary<string, LiveEffectEntry>> LiveEffects = new();

    // for casters that have kept results, until the next map load
    private static readonly Dictionary<ulong, int> CasterGenerations = [];

    // The stale check's cost is bounded: an effect is looked at once per this many frames, and only so many in a frame. It's
    // on by default and allocates, and garbage per frame is what caused multiplayer's hitches (issue 1), so it's sparing.
    private const int LiveEffectCheckIntervalFrames = 300;
    private const int LiveEffectChecksPerFrame = 1;

    private static int _liveEffectChecksFrame = -1;
    private static int _liveEffectChecksThisFrame;

    // Tooltips have no live effect. They ask often while drawing, and nothing in the rules reads this, so one frame's
    // result is reused for that frame only: a tooltip can't go on showing an old value.
    private static readonly Dictionary<ulong, Dictionary<string, EffectDescription>> GuiEffects = [];

    private static int _guiEffectsFrame = -1;

    private const string UnmodifiedEffect = "(unmodified)";

    private static Transform _parent;

    internal static void RechargeLinkedPowers(
        [NotNull] RulesetCharacter character,
        RestType restType)
    {
        var pointPoolPowerDefinitions = new List<FeatureDefinitionPower>();

        foreach (var usablePower in character.UsablePowers)
        {
            FeatureDefinitionPower rechargedPower;

            if (usablePower.PowerDefinition is FeatureDefinitionPowerSharedPool pool)
            {
                rechargedPower = pool.GetUsagePoolPower();
            }
            else if (usablePower.PowerDefinition.HasSubFeatureOfType<HasModifiedUses>())
            {
                rechargedPower = usablePower.PowerDefinition;
            }
            else
            {
                continue;
            }

            // Only add to recharge here if it (recharges on a short rest and this is a short or long rest) or
            // it recharges on a long rest and this is a long rest
            if (!pointPoolPowerDefinitions.Contains(rechargedPower)
                && (rechargedPower.RechargeRate == RechargeRate.ShortRest
                    || (rechargedPower.RechargeRate == RechargeRate.LongRest
                        && restType == RestType.LongRest)))
            {
                pointPoolPowerDefinitions.Add(rechargedPower);
            }
        }

        // Find the UsablePower of the point pool powers.
        foreach (var poolPower in character.UsablePowers)
        {
            if (!pointPoolPowerDefinitions.Contains(poolPower.PowerDefinition))
            {
                continue;
            }

            var poolSize = character.GetMaxUsesOfPower(poolPower);
            var modifier = poolPower.PowerDefinition.GetFirstSubFeatureOfType<ModifyPowerRechargeHandler>();
            if (modifier != null)
            {
                poolSize = modifier(character, poolPower, poolSize);
            }

            poolPower.remainingUses = poolSize;

            AssignUsesToSharedPowersForPool(character, poolPower, poolSize, poolSize);
        }
    }

    private static void AssignUsesToSharedPowersForPool(
        [NotNull] RulesetCharacter character,
        RulesetUsablePower poolPower,
        int remainingUses,
        int totalUses)
    {
        // Find powers that rely on this pool
        foreach (var usablePower in character.UsablePowers)
        {
            var power = usablePower.PowerDefinition;

            if (power is not FeatureDefinitionPowerSharedPool pool)
            {
                continue;
            }

            var pointPoolPower = pool.GetUsagePoolPower();

            if (pointPoolPower != poolPower.PowerDefinition)
            {
                continue;
            }

            if (power.CostPerUse == 0)
            {
                //Shared pool powers should have some cost, otherwise why make them shared?
                Main.Error($"Shared pool power '{power.Name}' has zero use cost!");
                usablePower.maxUses = totalUses;
                usablePower.remainingUses = remainingUses;
            }
            else
            {
                usablePower.maxUses = totalUses / power.CostPerUse;
                usablePower.remainingUses = remainingUses / power.CostPerUse;
            }
        }
    }

    [CanBeNull]
    internal static RulesetUsablePower GetPoolPower([NotNull] RulesetUsablePower power, RulesetCharacter character)
    {
        if (power.PowerDefinition is not FeatureDefinitionPowerSharedPool pool)
        {
            return null;
        }

        var poolPower = pool.GetUsagePoolPower();

        return PowerProvider.Get(poolPower, character);
    }

    internal static int GetMaxUsesForPool(
        this RulesetCharacter character,
        [NotNull] FeatureDefinitionPower power)
    {
        if (power is FeatureDefinitionPowerSharedPool poolPower)
        {
            power = poolPower.GetUsagePoolPower();
        }

        var usablePower = PowerProvider.Get(power, character);

        return character.GetMaxUsesOfPower(usablePower);
    }

    internal static void UpdateUsageForPower(
        this RulesetCharacter character,
        [NotNull] FeatureDefinitionPower power,
        int poolUsage)
    {
        if (power is FeatureDefinitionPowerSharedPool poolPower)
        {
            power = poolPower.GetUsagePoolPower();
        }

        var usablePower = PowerProvider.Get(power, character);

        UpdateUsageForPowerPool(character, poolUsage, usablePower);
    }

    internal static void UpdateUsageForPower(
        this RulesetCharacter character,
        [NotNull] RulesetUsablePower modifiedPower,
        int poolUsage)
    {
        RulesetUsablePower usablePower = null;

        if (modifiedPower.PowerDefinition is FeatureDefinitionPowerSharedPool sharedPoolPower)
        {
            var pointPoolPower = sharedPoolPower.GetUsagePoolPower();

            usablePower = PowerProvider.Get(pointPoolPower, character);
        }

        if (usablePower != null)
        {
            UpdateUsageForPowerPool(character, poolUsage, usablePower);
        }
    }

    internal static void UpdateUsageForPowerPool(
        this RulesetCharacter character,
        int poolUsage,
        RulesetUsablePower usablePower)
    {
        var maxUses = character.GetMaxUsesOfPower(usablePower);
        var remainingUses = Mathf.Clamp(usablePower.RemainingUses - poolUsage, 0, maxUses);

        usablePower.remainingUses = remainingUses;
        AssignUsesToSharedPowersForPool(character, usablePower, remainingUses, maxUses);

        // refresh character control panel after power pool usage is updated
        // needed for custom point pools on portrait to update properly in some cases
        CampaignsContext.GameHud.RefreshCharacterControlPanel();
    }

    internal static int GetRemainingPowerUses(this RulesetCharacter character, [NotNull] FeatureDefinitionPower power)
    {
        if (power.CostPerUse == 0 || power.RechargeRate == RechargeRate.AtWill)
        {
            return int.MaxValue;
        }

        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (power.RechargeRate)
        {
            case RechargeRate.BardicInspiration:
                return (character.TryGetAttributeValue(AttributeDefinitions.BardicInspirationNumber) -
                        character.UsedBardicInspiration) /
                       power.CostPerUse;
            case RechargeRate.BindChain:
                return (character.TryGetAttributeValue(AttributeDefinitions.BindChainNumber) -
                        character.UsedBindChain) /
                       power.CostPerUse;
            case RechargeRate.ChannelDivinity:
                // the power's own pool and the maximum of the current options (#68)
                return Tabletop2024Context.GetChannelDivinityRemaining(
                           character, Tabletop2024Context.GetChannelDivinityPool(character, power)) /
                       power.CostPerUse;
            case RechargeRate.HealingPool:
                return character.TryGetAttributeValue(AttributeDefinitions.HealingPool) - character.UsedHealingPool;
            case RechargeRate.MaxHitPoints:
                return character.TryGetAttributeValue(AttributeDefinitions.HitPoints) / power.CostPerUse;
            case RechargeRate.RagePoints:
                return (character.TryGetAttributeValue(AttributeDefinitions.RagePoints) - character.UsedRagePoints) /
                       power.CostPerUse;
            case RechargeRate.SorceryPoints:
                return (character.TryGetAttributeValue(AttributeDefinitions.SorceryPoints) -
                        character.UsedSorceryPoints) /
                       power.CostPerUse;
            case RechargeRate.KiPoints:
                return (character.TryGetAttributeValue(AttributeDefinitions.KiPoints) - character.UsedKiPoints) /
                       power.CostPerUse;
#if false
            case RechargeRate.AtWill:
                break;
            case RechargeRate.OneMinute:
                break;
            case RechargeRate.ShortRest:
                break;
            case RechargeRate.LongRest:
                break;
            case RechargeRate.Dawn:
                break;
            case RechargeRate.D6_6:
                break;
            case RechargeRate.SpellSlot:
                break;
            case RechargeRate.None:
                break;
            case RechargeRate.D6_56:
                break;
            case RechargeRate.TurnStart:
                break;
#endif
        }

        if (power is FeatureDefinitionPowerSharedPool poolPower)
        {
            return GetRemainingPowerPoolUses(character, poolPower) / power.CostPerUse;
        }

        var usablePower = PowerProvider.Get(power, character);

        return usablePower.RemainingUses / power.CostPerUse;
    }

    internal static int GetRemainingPowerCharges(
        this RulesetCharacter character,
        [NotNull] FeatureDefinitionPower power)
    {
        if (power is FeatureDefinitionPowerSharedPool poolPower)
        {
            return GetRemainingPowerPoolUses(character, poolPower);
        }

        var usablePower = PowerProvider.Get(power, character);

        return usablePower.RemainingUses;
    }

    private static int GetRemainingPowerPoolUses(
        this RulesetCharacter character,
        [NotNull] FeatureDefinitionPowerSharedPool sharedPoolPower)
    {
        var pointPoolPower = sharedPoolPower.GetUsagePoolPower();

        return PowerProvider.Get(pointPoolPower, character).RemainingUses;
    }

    internal static EffectDescription ModifySpellEffect(EffectDescription original, [NotNull] RulesetEffectSpell spell)
    {
        return ModifyMagicEffect(original, spell.SpellDefinition, spell.Caster, spell);
    }

    internal static EffectDescription ModifySpellEffect([NotNull] SpellDefinition spell, RulesetCharacter caster)
    {
        return ModifyMagicEffect(spell.EffectDescription, spell, caster, null);
    }

    internal static EffectDescription ModifyPowerEffect(EffectDescription original, [NotNull] RulesetEffectPower power)
    {
        return ModifyMagicEffect(original, power.PowerDefinition, power.User, power);
    }

    private static string Key(BaseDefinition definition, BaseDefinition metamagic)
    {
        var key = $"{definition.GetType()}:{definition.Name}:";

        if (metamagic)
        {
            key += metamagic.Name;
        }

        return key;
    }

    // A map load: see LiveEffects. The generations go too, as nothing is left that was stamped with them.
    internal static void ClearLiveEffects()
    {
        LiveEffects.Clear();
        CasterGenerations.Clear();
    }

    // For aiming a teleport, which changes the description's targeting on the aiming computer only. With no modifiers that apply,
    // an effect's description is the definition's own object, shared by every use of it: a change there would stay for the
    // session on this computer alone. So the effect reads a copy until EndAiming, which isn't replaced even by a RefreshAll, as
    // that would lose the targeting partway through. Null if the effect has no entry, as with no caster: nothing to change then.
    internal static EffectDescription BeginAiming(RulesetEffect effect)
    {
        // this read makes the entry
        _ = effect.EffectDescription;

        if (!TryGetLiveEntry(effect, out var entry))
        {
            return null;
        }

        entry.Aiming ??= EffectDescriptionBuilder.Create(entry.Description).Build();

        return entry.Aiming;
    }

    // Aiming is over, and the effect is back to what every computer has. A refresh during aiming now counts as usual.
    internal static void EndAiming(RulesetEffect effect)
    {
        if (TryGetLiveEntry(effect, out var entry))
        {
            entry.Aiming = null;
        }
    }

    private static bool TryGetLiveEntry(RulesetEffect effect, out LiveEffectEntry entry)
    {
        var metamagic = effect is RulesetEffectSpell spell ? spell.MetamagicOption : null;

        entry = null;

        return LiveEffects.TryGetValue(effect, out var entries) &&
               entries.TryGetValue(metamagic ? metamagic.Name : string.Empty, out entry);
    }

    internal static void ClearSpellEffectCache(RulesetCharacter caster)
    {
        // A caster with no entry has no kept results to make old. Monsters that never have any then add nothing here, which
        // otherwise holds one entry for every character refreshed since the game started.
        if (CasterGenerations.TryGetValue(caster.Guid, out var generation))
        {
            CasterGenerations[caster.Guid] = generation + 1;
        }

        GuiEffects.Remove(caster.Guid);
    }

    private static EffectDescription ModifyMagicEffect(
        EffectDescription original,
        BaseDefinition definition,
        [CanBeNull] RulesetCharacter caster,
        [CanBeNull] RulesetEffect effect)
    {
        if (caster == null)
        {
            return original;
        }

        // not tied to a live effect: nothing to keep it for, and a rules question gets today's answer
        if (effect == null)
        {
            return BuildModifiedEffect(original, definition, caster, null, null);
        }

        var metamagic = effect is RulesetEffectSpell spell ? spell.MetamagicOption : null;
        var metamagicName = metamagic ? metamagic.Name : string.Empty;

        if (!LiveEffects.TryGetValue(effect, out var entries))
        {
            entries = new Dictionary<string, LiveEffectEntry>();
            LiveEffects.Add(effect, entries);
        }


        CasterGenerations.TryGetValue(caster.Guid, out var generation);

        if (entries.TryGetValue(metamagicName, out var entry) && (entry.Generation == generation || entry.Aiming != null))
        {
            CheckLiveEffect(entry, original, definition, caster, effect, metamagic);

            return entry.Aiming ?? entry.Description;
        }

        // Noted before the modifiers run, and never written back: a refresh during them has to make this result old, as it
        // would with any other refresh, and writing the earlier number afterwards would make older results look current.
        if (!CasterGenerations.ContainsKey(caster.Guid))
        {
            CasterGenerations[caster.Guid] = generation;
        }

        var result = BuildModifiedEffect(original, definition, caster, effect, metamagic);

        entry = new LiveEffectEntry { Description = result, Generation = generation };
        entries[metamagicName] = entry;

        // The stale check's starting point, now, while the situation is still the one this result was worked out for. It
        // isn't subject to the check's budget: taken later, a change in between would become the starting point and be
        // missed. This runs once per result, so uses bound it, not frames.
        if (ObjectIdDiagnosticsContext.StaleEffectChecksActive)
        {
            // This counts as the entry's check, passed or failed: the first comparison or retry waits the usual interval,
            // rather than spending another rebuild on the next read, which could be in this same frame.
            entry.LastCheckedFrame = Time.frameCount;

            try
            {
                entry.Baseline = SummarizeWithoutEffect(original, definition, caster, metamagic);
            }
            catch (Exception e)
            {
                ObjectIdDiagnosticsContext.EffectCheckFailed(definition, e);
            }
        }

        // an unmodified effect is the definition's own, the same everywhere
        if (ObjectIdDiagnosticsContext.Active && !ReferenceEquals(result, original))
        {
            ObjectIdDiagnosticsContext.EffectWorkedOut(caster, definition, effect, result);
        }

        return result;
    }

    // Diagnostic only, for "when does a kept result go stale": an effect's result is kept for its life,
    // so a change made during that life, with no RefreshAll, still isn't seen. This records when the modifiers would now
    // give something different. It runs only when switched on (mod menu, Logging), so it must never change anything: a
    // computer with it on and one with it off have to agree.
    // So it never gives the modifiers the live effect. Three of the 70 write outside their copy
    // (ModifyEffectDescriptionSavingThrowRogue, a Gambits one and Dragon Breath set usablePower.saveDC), and all three do
    // that only when handed a live power, so replaying with the live effect would write. With no effect they run exactly as for a
    // tooltip. A listing of every assignment and call in all 70 bodies shows no other write; called helpers were checked by their
    // names, not read through. It compares two no-effect results, the first with the latest, so it sees changes in the caster's
    // situation. It can't see a modifier's effect-dependent part, such as points spent on this use or the save DC those three
    // set.
    private static void CheckLiveEffect(
        LiveEffectEntry entry,
        EffectDescription original,
        BaseDefinition definition,
        RulesetCharacter caster,
        RulesetEffect effect,
        BaseDefinition metamagic)
    {
        if (!ObjectIdDiagnosticsContext.StaleEffectChecksActive || effect.Terminated)
        {
            return;
        }

        var frame = Time.frameCount;

        if (frame - entry.LastCheckedFrame < LiveEffectCheckIntervalFrames)
        {
            return;
        }

        if (_liveEffectChecksFrame != frame)
        {
            _liveEffectChecksFrame = frame;
            _liveEffectChecksThisFrame = 0;
        }

        if (_liveEffectChecksThisFrame >= LiveEffectChecksPerFrame)
        {
            return;
        }

        _liveEffectChecksThisFrame++;
        entry.LastCheckedFrame = frame;

        try
        {
            var summary = SummarizeWithoutEffect(original, definition, caster, metamagic);

            // The check was off when this result was worked out, or that failed. This is only as far back as it can see,
            // not how the effect began, so reports from it say so.
            if (entry.Baseline == null)
            {
                entry.Baseline = summary;
                entry.BaselineIsLate = true;

                return;
            }

            if (summary != entry.Baseline && summary != entry.ReportedSummary)
            {
                entry.ReportedSummary = summary;
                ObjectIdDiagnosticsContext.EffectStale(
                    caster, definition, effect, entry.Baseline, summary, entry.BaselineIsLate);
            }
        }
        catch (Exception e)
        {
            // a modifier may not expect to run this late; the diagnostic never interrupts the game
            ObjectIdDiagnosticsContext.EffectCheckFailed(definition, e);
        }
    }

    private static string SummarizeWithoutEffect(
        EffectDescription original,
        BaseDefinition definition,
        RulesetCharacter caster,
        BaseDefinition metamagic)
    {
        var result = BuildModifiedEffect(original, definition, caster, null, metamagic);

        // Unmodified results are only marked, not described: the definition's own description is what some code changes on
        // purpose (gambits' RangeType), and that isn't an out-of-date result.
        return ReferenceEquals(result, original) ? UnmodifiedEffect : ObjectIdDiagnosticsContext.SummarizeEffect(result);
    }

    private static EffectDescription BuildModifiedEffect(
        EffectDescription original,
        BaseDefinition definition,
        RulesetCharacter caster,
        RulesetEffect effect,
        BaseDefinition metamagic)
    {
        //collect all valid modifiers from caster
        var result = original;
        var modifiers = new List<IModifyEffectDescription>();

        modifiers.AddRange(definition.GetAllSubFeaturesOfType<IModifyEffectDescription>()
            .Where(x => x.IsValid(definition, caster, result)));

        foreach (var modifier in caster.GetSubFeaturesByType<IModifyEffectDescription>()
                     .Where(x => x.IsValid(definition, caster, result)))
        {
            modifiers.TryAdd(modifier);
        }

        if (metamagic)
        {
            // all metamagic from metamagic feature are valid so no need to filter
            modifiers.AddRange(metamagic.GetAllSubFeaturesOfType<IModifyEffectDescription>());
        }

        if (modifiers.Count > 0)
        {
            result = modifiers.Aggregate(
                // ensure we get a copy as directly modifying blueprints can cause nasty effects
                EffectDescriptionBuilder
                    .Create(result)
                    .Build(),
                (current, f) =>
                {
                    var next = f.GetEffectDescription(definition, current, caster, effect);

                    // Some modifiers return an object of their own instead of the copy they were given (an upgrade kept in
                    // ModifyEffectDescriptionOnLevels). A later modifier, or code changing a live description, must not
                    // change that stored object, and modifiers now run once per use rather than once per refresh.
                    return ReferenceEquals(next, current)
                        ? next
                        : EffectDescriptionBuilder.Create(next).Build();
                });
        }

        return result;
    }

    private sealed class LiveEffectEntry
    {
        internal string Baseline;
        internal bool BaselineIsLate;
        internal EffectDescription Description;
        internal int Generation;
        internal int LastCheckedFrame = -1;
        internal EffectDescription Aiming;
        internal string ReportedSummary;
    }

    /**Modifies spell/power description for GUI purposes.*/
    internal static EffectDescription ModifyMagicEffectGui(
        EffectDescription original,
        [NotNull] BaseDefinition definition)
    {
        var caster = Global.CurrentCharacter;

        if (caster == null)
        {
            return original;
        }

        if (_guiEffectsFrame != Time.frameCount)
        {
            _guiEffectsFrame = Time.frameCount;
            GuiEffects.Clear();
        }

        if (!GuiEffects.TryGetValue(caster.Guid, out var effects))
        {
            effects = new Dictionary<string, EffectDescription>();
            GuiEffects.Add(caster.Guid, effects);
        }

        var key = Key(definition, null);

        if (!effects.TryGetValue(key, out var result))
        {
            result = BuildModifiedEffect(original, definition, caster, null, null);
            effects.Add(key, result);
        }

        return result;
    }

    internal static bool ValidatePrerequisites(
        [NotNull] RulesetCharacter character,
        [NotNull] BaseDefinition feature,
        [NotNull] IEnumerable<IValidateDefinitionPreRequisites.Validate> validators,
        [NotNull] out List<string> prerequisites)
    {
        var result = true;

        prerequisites = [];

        foreach (var validator in validators)
        {
            if (!validator(character, feature, out var line))
            {
                result = false;
            }

            if (line != null)
            {
                prerequisites.Add(line);
            }
        }

        return result;
    }

    internal static void RegisterPowerBundle(
        [NotNull] FeatureDefinitionPower masterPower,
        bool terminateAll,
        [NotNull] params FeatureDefinitionPower[] subPowers)
    {
        RegisterPowerBundle(masterPower, terminateAll, subPowers.ToList());
    }

    public static void RegisterPowerBundle(
        [NotNull] FeatureDefinitionPower masterPower,
        bool terminateAll,
        [NotNull] IEnumerable<FeatureDefinitionPower> subPowers)
    {
        if (Bundles.ContainsKey(masterPower))
        {
            throw new Exception($"Bundle '{masterPower.name}' already registered!");
        }

        var bundle = new Bundle(masterPower, subPowers, terminateAll);
        Bundles.Add(masterPower, bundle);
    }


    [CanBeNull]
    internal static Bundle GetBundle([CanBeNull] this FeatureDefinitionPower master)
    {
        if (!master)
        {
            return null;
        }

        return Bundles.TryGetValue(master, out var result) ? result : null;
    }

    // [CanBeNull]
    // internal static Bundle GetBundle([NotNull] SpellDefinition master)
    // {
    //     return GetBundle(GetPower(master));
    // }

    internal static bool IsBundlePower([NotNull] this FeatureDefinitionPower power)
    {
        return Bundles.ContainsKey(power);
    }

    // [CanBeNull]
    // internal static List<FeatureDefinitionPower> GetBundleSubPowers(this FeatureDefinitionPower master)
    // {
    //     return GetBundle(master)?.SubPowers;
    // }

    // [CanBeNull]
    // internal static List<FeatureDefinitionPower> GetBundleSubPowers([NotNull] SpellDefinition master)
    // {
    //     return GetBundleSubPowers(GetPower(master));
    // }

    private static SpellDefinition RegisterPower([NotNull] FeatureDefinitionPower power)
    {
        if (Powers2Spells.TryGetValue(power, out var value))
        {
            return value;
        }

        var spell = SpellDefinitionBuilder.Create($"Spell{power.name}")
            .SetGuiPresentation(power.GuiPresentation)
            .AddToDB();
        Spells2Powers[spell] = power;
        Powers2Spells[power] = spell;
        return spell;
    }

    [CanBeNull]
    internal static FeatureDefinitionPower GetPower([NotNull] SpellDefinition spell)
    {
        return Spells2Powers.TryGetValue(spell, out var result) ? result : null;
    }

    [CanBeNull]
    internal static FeatureDefinitionPower GetPower(string name)
    {
        return Bundles.Keys.FirstOrDefault(p => p.Name == name);
    }

    [NotNull]
    internal static List<FeatureDefinitionPower> GetMasterPowersBySubPower(FeatureDefinitionPower subPower)
    {
        return Bundles
            .Where(e => e.Value.SubPowers.Contains(subPower))
            .Select(e => e.Key)
            .ToList();
    }

    [CanBeNull]
    internal static SpellDefinition GetSpell([NotNull] FeatureDefinitionPower power)
    {
        return Powers2Spells.TryGetValue(power, out var result) ? result : null;
    }

    // [CanBeNull]
    // internal static List<SpellDefinition> GetSubSpells([CanBeNull] FeatureDefinitionPower masterPower)
    // {
    //     if (masterPower == null)
    //     {
    //         return null;
    //     }
    //
    //     var subPowers = GetBundleSubPowers(masterPower);
    //
    //     return subPowers?.Select(GetSpell).ToList();
    // }

    // Bundled sub-powers usually are not added to the character, so their UsablePower lacks class or race origin
    // This means that CharacterActionSpendPower will not call `UsePower` on them
    // This method fixes that
    internal static void SpendBundledPowerIfNeeded([NotNull] CharacterActionSpendPower action)
    {
        if (action.ActionParams.activeEffect is not RulesetEffectPower { OriginItem: null } activePower)
        {
            return;
        }

        var usablePower = activePower.UsablePower;

        if (usablePower.OriginClass)
        {
            return;
        }

        if (usablePower.OriginRace)
        {
            return;
        }

        if (usablePower.PowerDefinition.RechargeRate == RechargeRate.AtWill)
        {
            return;
        }

        if (GetMasterPowersBySubPower(usablePower.PowerDefinition).Count == 0)
        {
            return;
        }

        action.ActingCharacter.RulesetCharacter.UsePower(usablePower);
    }

    /**
     * Patch implementation
     * Shows sub-power selection modal for power bundles, then activates selected sub power
     * Returns true if nothing needs (or can) be done.
     */
    internal static bool PowerBoxActivated(UsablePowerBox box)
    {
        var masterPower = box.usablePower.PowerDefinition;
        var bundle = GetBundle(masterPower);

        if (bundle == null)
        {
            return true;
        }

        if (box.powerEngaged == null)
        {
            return true;
        }

        var subpowerSelectionModal = Gui.GuiService.GetScreen<SubpowerSelectionModal>();
        var transform = subpowerSelectionModal.transform;

        _parent = transform.parent;
        transform.parent = box.transform.parent.parent;

        subpowerSelectionModal.Bind(bundle.SubPowers, box.activator, (power, _) =>
        {
            if (box && box.powerEngaged != null)
            {
                box.powerEngaged(power);
            }
            else
            {
                Main.Error("Can't activate sub-power: box or handler is null");
            }
        }, box.RectTransform);
        subpowerSelectionModal.Show();

        return false;
    }

    /**
     * Patch implementation
     * Replaces invocation activation with sub-power selection modal, after sub-power is selected activates invocation selected handler with proper sub-power index
     * Returns true if nothing needs (or can) be done.
     */
    internal static bool InvocationPowerActivated(InvocationActivationBox box,
        InvocationSelectionPanel.InvocationSelectedHandler selected)
    {
        var invocation = box.Invocation;
        var masterPower = invocation.InvocationDefinition.GetPower();

        if (!masterPower)
        {
            return true;
        }

        var bundle = GetBundle(masterPower);

        if (bundle == null)
        {
            return true;
        }

        if (selected == null)
        {
            return true;
        }

        var subpowerSelectionModal = Gui.GuiService.GetScreen<SubpowerSelectionModal>();
        var transform = subpowerSelectionModal.transform;

        _parent = transform.parent;
        transform.parent = box.transform.parent.parent;

        subpowerSelectionModal.Bind(bundle.SubPowers, box.activator, (_, i) =>
        {
            if (box)
            {
                selected(invocation, i);
            }
            else
            {
                Main.Error("Can't activate invocation sub-power: box is null");
            }
        }, box.RectTransform);
        subpowerSelectionModal.Show();

        return false;
    }

    /**
     * Patch implementation
     * Closes sub-power selection modal
     */
    internal static void CloseSubPowerSelectionModal(bool instant)
    {
        var subpowerSelectionModal = Gui.GuiService.GetScreen<SubpowerSelectionModal>();

        if (!subpowerSelectionModal)
        {
            return;
        }

        // required to support the after rest action menu that doesn't keep state
        if (_parent)
        {
            subpowerSelectionModal.transform.parent = _parent;
        }

        subpowerSelectionModal.Hide(instant);
    }

    //TODO: decide if we need this, or can re-use native method of rest bundle powers
    /**
     * Patch implementation
     * Makes after rest action activation show sub-power selection for bundled powers
     */
    internal static bool ExecuteAfterRestCb(AfterRestActionItem instance)
    {
        if (instance.executing)
        {
            return true;
        }

        var activity = instance.RestActivityDefinition;

        if (activity.Functor != PowerBundleContext.UseCustomRestPowerFunctorName || activity.StringParameter == null)
        {
            return true;
        }

        var masterPower = GetPower(activity.StringParameter);
        var bundle = masterPower ? masterPower.GetBundle() : null;

        if (bundle == null)
        {
            return true;
        }

        var subpowerSelectionModal = Gui.GuiService.GetScreen<SubpowerSelectionModal>();

        subpowerSelectionModal.Bind(bundle.SubPowers, instance.Hero, (rulesetPower, _) =>
        {
            instance.button.interactable = false;

            var power = rulesetPower.powerDefinition.Name;

            ServiceRepository.GetService<IGameRestingService>().ExecuteAsync(ExecuteAsync(instance, power), power);
        }, instance.RectTransform);

        subpowerSelectionModal.Show();

        return false;
    }

    private static IEnumerator ExecuteAsync(AfterRestActionItem item, string powerName)
    {
        item.executing = true;

        var parameters = new FunctorParametersDescription { RestingHero = item.Hero, StringParameter = powerName };
        var gameRestingService = ServiceRepository.GetService<IGameRestingService>();

        yield return ServiceRepository.GetService<IFunctorService>()
            .ExecuteFunctorAsync(item.RestActivityDefinition.Functor, parameters, gameRestingService);

        yield return null;

        var actionService = ServiceRepository.GetService<IGameLocationActionService>();
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();

        bool needsToWait;

        do
        {
            needsToWait = characterService.PartyCharacters
                .Any(partyCharacter => actionService.IsCharacterActing(partyCharacter));

            if (needsToWait)
            {
                yield return null;
            }
        } while (needsToWait);

        item.AfterRestActionTaken?.Invoke();
        item.executing = false;

        var button = item.button;

        if (button)
        {
            button.interactable = true;
        }
    }

    internal sealed class Bundle
    {
        internal Bundle(
            FeatureDefinitionPower masterPower, IEnumerable<FeatureDefinitionPower> subPowers, bool terminateAll)
        {
            // MasterPower = masterPower;
            SubPowers = [..subPowers];
            TerminateAll = terminateAll;

            var subSpells = SubPowers.Select(RegisterPower).ToArray();

            Repertoire = new RulesetSpellRepertoire();
            Repertoire.KnownSpells.AddRange(subSpells);

            var masterSpell = RegisterPower(masterPower);

            masterSpell.SubspellsList.AddRange(subSpells);
        }

        /**
         * If set to true will terminate all powers in this bundle when 1 is terminated, so only one power
         * from this bundle can be in effect
         */
        internal bool TerminateAll { get; }

        internal List<FeatureDefinitionPower> SubPowers { get; }

        // internal FeatureDefinitionPower MasterPower { get; }

        //May be needed to hold powers for some native widgets
        // internal FeatureDefinitionFeatureSet PowerSet { get; }

        private RulesetSpellRepertoire Repertoire { get; }
    }
}
