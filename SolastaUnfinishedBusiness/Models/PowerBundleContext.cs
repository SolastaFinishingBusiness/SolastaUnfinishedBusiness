using System.Collections;
using System.Collections.Generic;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.Interfaces;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

internal static class PowerBundleContext
{
    internal const string UseCustomRestPowerFunctorName = "UseCustomRestPower";

    internal static void Load()
    {
        ServiceRepository.GetService<IFunctorService>()
            .RegisterFunctor(UseCustomRestPowerFunctorName, new FunctorUseCustomRestPower());
        RestCommandContext.Register(nameof(UseCustomRestPowerTravelling), UseCustomRestPowerTravelling);
        RestCommandContext.Register(nameof(UseCustomRestPowerEverywhere), UseCustomRestPowerEverywhere);
    }

    // the action a self power from the rest screen runs as: used by the actor on itself, without animations
    private static CharacterActionParams BuildRestPowerAction(
        GameLocationCharacter actor, RulesetUsablePower usablePower)
    {
        return new CharacterActionParams(actor, ActionDefinitions.Id.PowerNoCost)
        {
            ActionModifiers = { new ActionModifier() },
            RulesetEffect = ServiceRepository.GetService<IRulesetImplementationService>()
                .InstantiateEffectPower(actor.RulesetCharacter, usablePower, true),
            UsablePower = usablePower,
            TargetCharacters = { actor },
            SkipAnimationsAndVFX = true
        };
    }

    // rest powers run by command finished, per hero and power: counted on every computer alike, so the clicker can
    // wait for the one it started
    private static readonly Dictionary<(ulong, string), int> CommandPowersFinished = new();

    // GameDestroyed (GameManagerPatcher): the counts belong to the game that's gone
    internal static void ForgetGame()
    {
        CommandPowersFinished.Clear();
    }

    // the character a self rest power is used by: its retarget (IRetargetCustomRestPower, the Innovation Weapon's Steel
    // Defender), or else the hero on the map
    private static GameLocationCharacter RestPowerActor(FeatureDefinitionPower power, RulesetCharacterHero ruleChar)
    {
        return power.GetFirstSubFeatureOfType<IRetargetCustomRestPower>()?.GetTarget(ruleChar) ??
               GameLocationCharacter.GetFromActor(ruleChar);
    }

    // The game's networked action sends the power as an index into the acting character's own powers, so it can only
    // carry a power that character has. Two kinds of rest power aren't: one another character uses (the Steel
    // Defender's), and one the hero doesn't own at all, which PowerProvider makes up for the moment (Identify after a
    // rest); both would reach the other computers as no power.
    private static bool NeedsCommand(
        FeatureDefinitionPower power, RulesetCharacterHero ruleChar, RulesetUsablePower usablePower,
        GameLocationCharacter actor)
    {
        return actor.RulesetCharacter != ruleChar ||
               !ruleChar.UsablePowers.Contains(usablePower) ||
               power.GetFirstSubFeatureOfType<IRetargetCustomRestPower>()?.GetTarget(ruleChar) != null;
    }

    // runs on every computer at the same tick (RestCommandContext): a rest power the networked action can't carry
    // (NeedsCommand). Every computer starts the same action itself, on the character the
    // clicker chose (by ID), and only if that is still the power's user here.
    private static bool UseCustomRestPowerEverywhere(RulesetCharacterHero ruleChar, string argument)
    {
        var parts = argument.Split('|');

        if (parts.Length != 2 || !ulong.TryParse(parts[1], out var actorGuid) ||
            !TryGetCustomRestPower(parts[0], out var power) ||
            RestPowerActor(power, ruleChar) is not { } actor ||
            actor.Guid != actorGuid)
        {
            Main.Error($"[RestCommand] {argument} for {ruleChar.Name}: not its target any more, power not used");

            return false;
        }

        var key = (ruleChar.Guid, power.Name);

        ServiceRepository.GetService<IGameLocationActionService>()
            .ExecuteAction(BuildRestPowerAction(actor, PowerProvider.Get(power, ruleChar)), _ =>
            {
                CommandPowersFinished.TryGetValue(key, out var count);
                CommandPowersFinished[key] = count + 1;
            }, false);

        return true;
    }

    private static bool TryGetCustomRestPower(string powerName, out FeatureDefinitionPower power)
    {
        power = PowerBundle.GetPower(powerName);

        return power || DatabaseHelper.TryGetDefinition(powerName, out power);
    }

    // runs on every computer at the same tick (RestCommandContext): a self power used from the rest screen while the
    // hero isn't on a map (travelling). Run on the clicker's computer only, only that game would spend the use.
    private static bool UseCustomRestPowerTravelling(RulesetCharacterHero ruleChar, string powerName)
    {
        if (!TryGetCustomRestPower(powerName, out var power))
        {
            return false;
        }

        var usablePower = PowerProvider.Get(power, ruleChar);
        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var formsParams = new RulesetImplementationDefinitions.ApplyFormsParams();

        formsParams.FillSourceAndTarget(ruleChar, ruleChar);
        formsParams.FillFromActiveEffect(implementationService
            .InstantiateEffectPower(ruleChar, usablePower, false));
        formsParams.effectSourceType = EffectSourceType.Power;

        ruleChar.UsePower(usablePower);
        ruleChar.LogCharacterUsedPower(power, $"Feedback/&{power.Name}UsedWhileTravellingFormat");

        return true;
    }

    private sealed class FunctorUseCustomRestPower : Functor
    {
        private bool _powerUsed;

        public override IEnumerator Execute(
            [NotNull] FunctorParametersDescription functorParameters,
            FunctorExecutionContext context)
        {
            var functor = this;
            var powerName = functorParameters.StringParameter;
            if (!TryGetCustomRestPower(powerName, out var power))
            {
                yield break;
            }

            var ruleChar = functorParameters.RestingHero;
            var usablePower = PowerProvider.Get(power, ruleChar);

            if (power.EffectDescription.TargetType == TargetType.Self)
            {
                var fromActor = RestPowerActor(power, ruleChar);

                if (fromActor != null && NeedsCommand(power, ruleChar, usablePower, fromActor))
                {
                    // like the action path below, the rest screen carries on only once the power has been used
                    var key = (ruleChar.Guid, power.Name);
                    var finishedBefore = CommandPowersFinished.TryGetValue(key, out var count) ? count : 0;
                    var outcome = RestCommandContext.Outcome.Failed;

                    yield return RestCommandContext.Send(ruleChar, nameof(UseCustomRestPowerEverywhere),
                        $"{power.Name}|{fromActor.Guid}", x => outcome = x);

                    // bounded, like the command itself, so an action that never reports back can't hold the screen
                    var deadline = UnityEngine.Time.realtimeSinceStartup + 30f;

                    while (outcome == RestCommandContext.Outcome.Ran &&
                           UnityEngine.Time.realtimeSinceStartup < deadline &&
                           (CommandPowersFinished.TryGetValue(key, out count) ? count : 0) == finishedBefore)
                    {
                        yield return null;
                    }

                    yield break;
                }

                if (fromActor != null)
                {
                    functor._powerUsed = false;

                    // through the command service, as the game's own FunctorUsePower does, so the power is used on
                    // every computer; IGameLocationActionService.ExecuteAction (used before) runs it on this one only
                    ServiceRepository.GetService<ICommandService>()
                        .ExecuteAction(BuildRestPowerAction(fromActor, usablePower), functor.ActionExecuted, false);

                    while (!functor._powerUsed)
                    {
                        yield return null;
                    }
                }
                else
                {
                    yield return RestCommandContext.Send(
                        ruleChar, nameof(UseCustomRestPowerTravelling), power.Name, null);
                }
            }

            Trace.LogWarning("Unable to assign targets to power");
        }

        private void ActionExecuted(CharacterAction action)
        {
            _powerUsed = true;
        }
    }
}
