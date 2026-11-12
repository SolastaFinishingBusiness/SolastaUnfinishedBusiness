using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using TA.AI;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationActionManagerPatcher
{
    //PATCH: forced movement through areas (issue #32, ForcedMovementContext). A push worked out inside ApplyMotionForm
    // is queued here right after. Its cells are tied to these parameters, so they stay with this push however long
    // it waits and whatever other pushes are worked out for the same creature meanwhile.
    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ExecuteAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteAction_Patch
    {
        [UsedImplicitly]
        public static void Prefix(CharacterActionParams actionParams)
        {
            ForcedMovementContext.BindToAction(actionParams);

            //PATCH: Eldritch Blast's pushes and pulls: note the push a queued move of the cast makes (issue #37)
            EldritchBlastFallContext.PushQueued(actionParams);
        }
    }

    //PATCH: Eldritch Blast's pushes and pulls (issue #37, EldritchBlastFallContext, a setting): a creature whose
    // fall was put off until the cast's last move on it is let go here, once it is at rest and nothing more is to
    // come. UpdateImpl is where the game advances every character's actions, so this follows the same steps on
    // every computer. Returns at once when no creature is held.
    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.UpdateImpl))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UpdateImpl_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationActionManager __instance)
        {
            EldritchBlastFallContext.AfterActionUpdate(__instance);
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactToSpendSpellSlot))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactToSpendSpellSlot_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: replace `SpendSpellSlot` reaction with custom one
            __instance.AddInterruptRequest(new ReactionRequestSpendSpellSlotExtended(reactionParams));

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactForOpportunityAttack))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactForOpportunityAttack_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: replace `OpportunityAttack` reaction with warcaster one

            //replace only for player characters
            if (reactionParams.ActingCharacter.Side != Side.Ally)
            {
                return true;
            }

            __instance.AddInterruptRequest(new ReactionRequestWarcaster(reactionParams));

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactForReadiedAction))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactForReadiedAction_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: mark this attack as not AoO, so Sentinel movement stop won't trigger
            reactionParams.AttackMode?.AddAttackTagAsNeeded(AttacksOfOpportunity.NotAoOTag);
            //PATCH: mark as a reaction, so Attack After Magic Effect won't check for attack validity, since it was already checked prior to triggering the reaction
            reactionParams.IsReactionEffect = true;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactToSpendPower))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactToSpendPower_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, CharacterActionParams reactionParams)
        {
            //PATCH: replace `SpendPower` reaction for bundled powers or customized one for other powers
            if (reactionParams.RulesetEffect is not RulesetEffectPower powerEffect)
            {
                return true;
            }

            if (powerEffect.PowerDefinition.IsBundlePower())
            {
                __instance.AddInterruptRequest(new ReactionRequestSpendBundlePower(reactionParams)
                {
                    Resource = powerEffect.PowerDefinition.GetFirstSubFeatureOfType<ICustomReactionResource>()
                });
            }
            else
            {
                __instance.AddInterruptRequest(new ReactionRequestSpendPowerCustom(reactionParams));
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.ReactToUsePower))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReactToUsePower_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GameLocationActionManager __instance,
            CharacterActionParams reactionParams,
            string reactionName = "",
            GameLocationCharacter attacker = null)
        {
            //PATCH: replace `UsePower` reaction for customized one that allows better descriptions
            if (reactionParams.RulesetEffect is not RulesetEffectPower)
            {
                return true;
            }

            __instance.AddInterruptRequest(string.IsNullOrEmpty(reactionName)
                ? attacker == null
                    ? new ReactionRequestUsePowerCustom(reactionParams, (string)null)
                    : new ReactionRequestUsePowerCustom(reactionParams, attacker)
                : attacker == null
                    ? new ReactionRequestUsePowerCustom(reactionParams, reactionName)
                    : new ReactionRequestUsePowerCustom(reactionParams, reactionName, attacker));

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.CharacterDamageReceivedAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CharacterDamageReceivedAsync_Patch
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(
            [NotNull] IEnumerator values,
            RulesetCharacter rulesetTarget,
            bool wasConscious,
            bool stillConscious,
            bool massiveDamage)
        {
            //PATCH: support for `DoNotTerminateWhileUnconscious`
            yield return RestrictEffectToNotTerminateWhileUnconscious.TerminateAllSpellsAndEffects(
                values, rulesetTarget, wasConscious, stillConscious, massiveDamage);
        }
    }

    [HarmonyPatch(typeof(GameLocationActionManager),
        nameof(GameLocationActionManager.ExecuteReactionRequestGroupAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteReactionRequestGroupAsync_Patch
    {
        public const string ReactionTimestamp = "ReactionTimestamp";

        [UsedImplicitly]
        public static IEnumerator Postfix(
            [NotNull] IEnumerator values,
            ReactionRequestGroup reactionRequestGroup)
        {
            //PATCH: ensure whoever reacts first will get the reaction handled first by game
            if (!Global.IsMultiplayer)
            {
                reactionRequestGroup.Requests.Sort((a, b) =>
                {
                    a.Character.UsedSpecialFeatures.TryGetValue(ReactionTimestamp, out var aTimestamp);
                    b.Character.UsedSpecialFeatures.TryGetValue(ReactionTimestamp, out var bTimestamp);

                    return aTimestamp <= bTimestamp ? -1 : 1;
                });
            }

            while (values.MoveNext())
            {
                yield return values.Current;
            }
        }
    }

    //PATCH: supports for unique request groups, except if an opportunity attack, otherwise battle crashes...
    // ...under advanced reaction scenarios when someone reacts in the middle of another reaction
    // i.e.: an attack before hit offers a reaction which forces an enemy to roll a save,
    // which an ally could change from success to failure, and only after that pop up a maneuver usage
    [HarmonyPatch(typeof(GameLocationActionManager), nameof(GameLocationActionManager.AddInterruptRequest))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AddInterruptRequest_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationActionManager __instance, ReactionRequest reactionRequest)
        {
            AddInterruptRequest(__instance, reactionRequest);

            return false;
        }

        // vanilla code except for BEGIN/END patch block
        private static void AddInterruptRequest(GameLocationActionManager __instance, ReactionRequest reactionRequest)
        {
            reactionRequest.AssignGuid(__instance.currentReactionGuid++);

            if (__instance.pendingReactionRequestGroups.Count > 0 &&
                __instance.pendingReactionRequestGroups.Peek().ReactionDefinitionName == reactionRequest.DefinitionName)
            {
                var isSameCharacter = __instance.pendingReactionRequestGroups.Peek().Requests
                    .Any(request => request.Character == reactionRequest.Character);

                if (!isSameCharacter)
                {
                    //BEGIN PATCH
                    // add a new unique request group to avoid reactions grouping and, enforce the desired sequence
                    // if not an opportunity attack, ready attack, cast reaction, or ready cast
                    if (reactionRequest.ReactionParams.ActionDefinition.Id is not (
                        ActionDefinitions.Id.AttackOpportunity or
                        ActionDefinitions.Id.AttackReadied or
                        ActionDefinitions.Id.CastReaction or
                        ActionDefinitions.Id.CastReadied))
                    {
                        __instance.pendingReactionRequestGroups.Push(
                            new ReactionRequestGroup(reactionRequest.DefinitionName));
                    }
                    //END PATCH

                    __instance.pendingReactionRequestGroups.Peek().Requests.Add(reactionRequest);
                }
            }
            else
            {
                __instance.pendingReactionRequestGroups.Push(new ReactionRequestGroup(reactionRequest.DefinitionName));
                __instance.pendingReactionRequestGroups.Peek().Requests.Add(reactionRequest);
            }

            if (!reactionRequest.Automated)
            {
                __instance.ReactionTriggered?.Invoke(reactionRequest);
            }
            else
            {
                __instance.unstoppableCoroutines.Add(
                    __instance.DelayedProcessAutomatedReactionRequest(reactionRequest));
            }
        }
    }
}
