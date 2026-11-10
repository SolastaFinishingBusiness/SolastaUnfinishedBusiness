using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using NAudio.CoreAudioApi;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Behaviors.Specific;
using SolastaUnfinishedBusiness.CustomUI;
using SolastaUnfinishedBusiness.Interfaces;
using SolastaUnfinishedBusiness.Models;
using SolastaUnfinishedBusiness.Races;
using SolastaUnfinishedBusiness.Spells;
using SolastaUnfinishedBusiness.Subclasses;
using SolastaUnfinishedBusiness.Validators;
using TA;
using UnityEngine;
using static ActionDefinitions;
using static LocationDefinitions;
using static RuleDefinitions;
using static SenseMode;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class GameLocationCharacterPatcher
{
    //PATCH: forced movement through areas (issue #29, ForcedMovementContext). FinishMoveTo is where a move changes the
    // creature's cell. A push does it once, from its start straight to its landing cell, so areas in between never
    // acted on the creature. Just before that change, a pushed creature is stepped through the cells its push goes
    // through, for area effects only. Every other move (walking, teleports) returns at once. The game's method is
    // skipped only if an area's effect moved the creature elsewhere meanwhile (see BeforeMoveCommitted).
    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.FinishMoveTo))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FinishMoveTo_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __instance, int3 destination)
        {
            return ForcedMovementContext.BeforeMoveCommitted(__instance, destination);
        }
    }

    //PATCH: supports IForceLightingState
    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.LightingState), MethodType.Getter)]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class LightingState_Getter_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GameLocationCharacter __instance,
            ref LocationDefinitions.LightingState __result)
        {
            var modifiers = __instance.RulesetActor.GetSubFeaturesByType<IForceLightingState>();

            if (modifiers.Count == 0)
            {
                return true;
            }

            __result = modifiers[0].GetLightingState(__instance, __result);

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.SignalJumpFinished))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SignalJumpFinished_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            GameLocationCharacter __instance,
            int3 start, int3 finish, bool landingFailed)
        {
            if (Main.Settings.ModifyJumpRulesForArmorAndEncumberance
                && landingFailed
                )
            {
                var damageForm = new DamageForm
                {
                    DamageType = DamageTypeBludgeoning,
                    DiceNumber = 1,
                    DieType = DieType.D4
                };
                var rolls = new List<int>();
                var totalDamage = __instance.RulesetCharacter.RollDamage(damageForm, 0, false, 0, 0, 1, false, false, false, rolls);
                var currentHitPoints = __instance.RulesetCharacter.CurrentHitPoints;
                
                __instance.RulesetCharacter.SustainDamage(totalDamage, damageForm.DamageType, false, __instance.Guid,
                    new RollInfo(damageForm.DieType, rolls, 0), out _);
            }
        }
    }


    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.CanMoveInSituation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanMoveInSituation_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance, ref bool __result,
            RulesetCharacter.MotionRange motionRange)
        {
            if (__result) { return; }

            //PATCH: allow standing on air if grappled
            if (motionRange == RulesetCharacter.MotionRange.AboveGround)
            {
                __result = __instance.IsGrappled();
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.CheckMotionValidity))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckMotionValidity_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __instance)
        {
            //PATCH: always allow prone when grappled
            return !__instance.Prone || !__instance.IsGrappled();
        }
    }

    //PATCH: supports `UseOfficialLightingObscurementAndVisionRules`
    //let ADV/DIS be handled elsewhere in `GLBM.CanAttack` if alternate lighting and obscurement rules in place
    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.ComputeLightingModifierForIlluminable))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ComputeLightingModifierForIlluminable_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            GameLocationCharacter __instance,
            IIlluminable target,
            LocationDefinitions.LightingState targetLightingState,
            Vector3 gravityCenter,
            Vector3 targetGravityCenter,
            ActionModifier actionModifier)
        {
            if (!Main.Settings.UseOfficialLightingObscurementAndVisionRules)
            {
                ComputeLightingModifierForLightingState(
                    __instance,
                    (gravityCenter - targetGravityCenter).magnitude,
                    targetLightingState,
                    actionModifier,
                    target.TargetSource);
            }

            return false;
        }

        private static void ComputeLightingModifierForLightingState(
            GameLocationCharacter __instance,
            float distance,
            LocationDefinitions.LightingState lightingState,
            ActionModifier actionModifier,
            object source = null)
        {
            var isValidForLightingState = false;
            var isDarkvisionOffRange = false;
            var isWithinRange = false;

            // BEGIN PATCH
            var senseModesToPrevent = new List<SenseMode.Type>();

            if (source is RulesetCharacter rulesetCharacter)
            {
                foreach (var modifier in rulesetCharacter.GetSubFeaturesByType<IPreventEnemySenseMode>())
                {
                    senseModesToPrevent.AddRange(modifier.PreventedSenseModes(__instance, rulesetCharacter));
                }
            }
            // END PATCH

            foreach (var senseMode in __instance.RulesetCharacter.SenseModes
                         .Where(x => !senseModesToPrevent.Contains(x.SenseType)))
            {
                if (distance > (double)senseMode.SenseRange)
                {
                    if (senseMode.SenseType == SenseMode.Type.Darkvision)
                    {
                        isDarkvisionOffRange = true;
                    }
                }
                else
                {
                    isWithinRange = true;

                    if (!ValidForLighting(senseMode.SenseType, lightingState))
                    {
                        continue;
                    }

                    isValidForLightingState = true;
                    break;
                }
            }

            if (isValidForLightingState || !isWithinRange)
            {
                return;
            }

            var additionalDetails = isDarkvisionOffRange && lightingState == LocationDefinitions.LightingState.Unlit
                ? "Tooltip/&OutOfDarkvisionRangeFormat"
                : string.Empty;

            actionModifier.AttackAdvantageTrends.Add(new TrendInfo(-1, FeatureSourceType.Lighting,
                lightingState.ToString(), source, additionalDetails));
            actionModifier.AbilityCheckAdvantageTrends.Add(new TrendInfo(-1, FeatureSourceType.Lighting,
                lightingState.ToString(), source, additionalDetails));
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.StartBattleTurn))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartBattleTurn_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter __instance)
        {
            //PATCH: acts as a callback for the character's before combat turn started event
            CharacterBattleListenersPatch.OnCharacterBeforeTurnStarted(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance)
        {
            //PATCH: acts as a callback for the character's combat turn started event
            CharacterBattleListenersPatch.OnCharacterTurnStarted(__instance);
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.StartBattleTurnOtherContender))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartBattleTurnOtherContender_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __instance)
        {
            var rulesetCharacter = __instance.RulesetCharacter;

            if (__instance.GetActionTypeStatus(ActionType.Reaction) != ActionStatus.Available)
            {
                rulesetCharacter.EnumerateFeaturesToBrowse<IActionPerformanceProvider>(
                    __instance.featuresCache, rulesetCharacter.FeaturesOrigin);

                // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
                foreach (var feature in __instance.featuresCache)
                {
                    var actionAffinity = feature as FeatureDefinitionActionAffinity;

                    if (!actionAffinity ||
                        !actionAffinity.RechargeReactionsAtEveryTurn)
                    {
                        continue;
                    }

                    //---- START ----
                    //PATCH: support for feature usage limits
                    if (feature.GetFirstSubFeatureOfType<FeatureUseLimiter>()?.CanBeUsed(__instance, feature) == false)
                    {
                        continue;
                    }

                    __instance.IncrementSpecialFeatureUses(actionAffinity.Name);

                    //---- END ----
                    __instance.RefundActionUse(ActionType.Reaction);

                    var actionRefunded = __instance.ActionRefunded;
                    actionRefunded?.Invoke(__instance, ActionType.Reaction);
                }
            }

            var db = DatabaseRepository.GetDatabase<FeatureDefinition>();

            foreach (var key in __instance.usedSpecialFeatures.Keys)
            {
                if (db.TryGetElement(key, out var result)
                    && result is FeatureDefinitionAdditionalDamage { LimitedUsage: FeatureLimitedUsage.OncePerTurn })
                {
                    __instance.restoredFeatures.Add(result);
                }
            }

            foreach (var restoredFeature in __instance.restoredFeatures)
            {
                __instance.usedSpecialFeatures.Remove(restoredFeature.Name);
            }

            __instance.restoredFeatures.Clear();
            foreach (var proxy in rulesetCharacter.ControlledEffectProxies)
            {
                GameLocationCharacter.GetFromActor(proxy)?.StartBattleTurnOtherContender();
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.EndBattleTurn))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EndBattleTurn_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter __instance)
        {
            //PATCH: acts as a callback for the character's combat turn ended event
            CharacterBattleListenersPatch.OnCharacterTurnEnded(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance, int roundNumber)
        {
            //PATCH: compares this character with the host's at the end of its turn (MULTIPLAYER)
            MultiplayerContext.Guarded("checking a turn end",
                () => MultiplayerDesyncContext.OnTurnEnded(__instance, roundNumber, false));
        }
    }

    //PATCH: a legendary turn ends here instead of EndBattleTurn; compared the same way (MULTIPLAYER)
    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.EndLegendaryTurn))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EndLegendaryTurn_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance)
        {
            MultiplayerContext.Guarded("checking a legendary turn end",
                () => MultiplayerDesyncContext.OnTurnEnded(__instance, Gui.Battle?.CurrentRound ?? 0, true));
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.StartBattle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class StartBattle_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance, bool surprise)
        {
            //PATCH: acts as a callback for the character's combat started event
            //while there already is callback for this event it doesn't have character or surprise flag arguments
            CharacterBattleListenersPatch.OnCharacterBattleStarted(__instance, surprise);

            // Characters sharing a cell at battle start (MULTIPLAYER) are left to the game's own unstack at initiative
            // (BattleState_Initiative.Begin), which reads only the grid and so gives the same cells on every computer.
            // The mod used to move them to a "safe" cell here instead, which also asked each computer's own visibility
            // service which cells enemies could see, so computers could disagree (desync issue 04; removed on the
            // user's choice, 2026-11-09, PR #28).
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.EndBattle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class EndBattle_Patch
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance)
        {
            //PATCH: acts as a callback for the character's combat ended event
            //while there already is callback for this event it doesn't have character argument
            CharacterBattleListenersPatch.OnCharacterBattleEnded(__instance);
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.CanUseAtLeastOnPower))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CanUseAtLeastOnPower_Patch
    {
        [UsedImplicitly]
        public static void Postfix(
            GameLocationCharacter __instance,
            ActionType actionType,
            ref bool __result,
            bool accountDelegatedPowers)
        {
            var rulesetCharacter = __instance.RulesetCharacter;

            if (rulesetCharacter == null)
            {
                return;
            }

            var battleInProgress = Gui.Battle != null;

            //PATCH: force show power use button during exploration if it has at least one usable power
            //This makes it so that if a character only has powers that take longer than an action to activate the "Use Power" button is available.
            if (__result || battleInProgress)
            {
                return;
            }

            if (actionType == ActionType.Main &&
                rulesetCharacter.UsablePowers.Any(rulesetUsablePower =>
                    CanUsePower(rulesetCharacter, rulesetUsablePower, accountDelegatedPowers)))

            {
                __result = true;
            }
        }

        private static bool CanUsePower(RulesetCharacter character, RulesetUsablePower usablePower,
            bool accountDelegatedPowers)
        {
            var power = usablePower.PowerDefinition;

            return (accountDelegatedPowers || !power.DelegatedToAction)
                   && character.CanUsePower(power, false);
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.GetActionStatus))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetActionStatus_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var isWearingShieldMethod = typeof(RulesetCharacter).GetMethod("IsWearingShield");
            var trueMethod = new Func<RulesetActor, bool>(True).Method;

            return instructions.ReplaceCalls(isWearingShieldMethod,
                "GameLocationCharacter.GetActionStatus",
                new CodeInstruction(OpCodes.Call, trueMethod));

            //PATCH: Support for Pugilist Fighting Style
            // Removes check that makes `ShoveBonus` action unavailable if character has no shield
            static bool True(RulesetActor actor)
            {
                return true;
            }
        }

        [UsedImplicitly]
        public static void Postfix(
            GameLocationCharacter __instance,
            ref ActionStatus __result,
            Id actionId,
            ActionScope scope,
            ActionStatus actionTypeStatus,
            // RulesetAttackMode optionalAttackMode,
            bool ignoreMovePoints)
        {
            var rulesetCharacter = __instance.RulesetCharacter;
            var flurryOfBlowActions = new[]
            {
                Id.FlurryOfBlows, Id.FlurryOfBlowsSwiftSteps, Id.FlurryOfBlowsUnendingStrikes
            };

            //PATCH: support for `IReplaceAttackWithCantrip` - allows `CastMain` action if character used attack
            ReplaceAttackWithCantrip.AllowCastDuringMainAttack(__instance, actionId, scope, ref __result);

            //PATCH: support for custom invocation action ids
            CustomActionIdContext.ProcessCustomActionIds(
                __instance, ref __result, actionId, scope, actionTypeStatus, ignoreMovePoints);

            ActionSwitching.CheckSpellcastingAvailability(__instance, actionId, scope, ref __result);

            //PATCH: support grapple
            GrappleContext.ValidateActionAvailability(__instance, ref __result, actionId);

            //PATCH: support blinded don't allow AoO
            if (Main.Settings.BlindedConditionDontAllowAttackOfOpportunity &&
                actionId == Id.AttackOpportunity &&
                rulesetCharacter.HasConditionOfTypeOrSubType(ConditionBlinded) &&
                rulesetCharacter.SenseModes
                    .Select(x => x.SenseType)
                    .Intersect([SenseMode.Type.Blindsight, SenseMode.Type.Tremorsense, SenseMode.Type.Truesight])
                    .ToList().Count == 0)
            {
                __result = ActionStatus.CannotPerform;
            }

            //PATCH: support bonus / main forbidden actions
            foreach (var bonusOrMain in new[] { ActionType.Bonus, ActionType.Main })
            {
                if (__result == ActionStatus.Available &&
                    __instance.ActionPerformancesByType.TryGetValue(
                        bonusOrMain, out var actionPerformanceFilters) &&
                    actionPerformanceFilters.Any(x => x.ForbiddenActions.Any(y => y == actionId)))
                {
                    __result = ActionStatus.CannotPerform;
                }
            }

            //PATCH: support `EnableMonkFocus2024`
            if (Main.Settings.EnableMonkFocus2024 &&
                __result == ActionStatus.CannotPerform &&
                flurryOfBlowActions.Contains(actionId) &&
                (rulesetCharacter.RemainingKiPoints > 0 ||
                 rulesetCharacter.HasConditionOfType(WayOfShadow.ConditionCloakOfShadowsName)) &&
                __instance.GetActionTypeStatus(ActionType.Bonus) == ActionStatus.Available)
            {
                __result = ActionStatus.Available;
            }

            //PATCH: support Swift Quiver spell interaction with Flurry of Blows
            //TODO: this sounds fishy. rethink how to do it
            if (__result == ActionStatus.Available &&
                flurryOfBlowActions.Contains(actionId) &&
                __instance.UsedSpecialFeatures.ContainsKey(SpellBuilders.SwiftQuiverAttackTag))
            {
                __result = ActionStatus.CannotPerform;
            }

            var traditionFreedomLevel =
                rulesetCharacter.GetSubclassLevel(DatabaseHelper.CharacterClassDefinitions.Monk, "TraditionFreedom");

            //BUGFIX: Hide other Flurry of Blows actions on Way of Freedom Monk as it levels up
            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (actionId)
            {
                case Id.FlurryOfBlows when
                    traditionFreedomLevel >= 3:
                case Id.FlurryOfBlowsSwiftSteps when
                    traditionFreedomLevel >= 11:
                    __result = ActionStatus.Unavailable;
                    break;
            }

            //BUGFIX: if character can use only 1 of Main or Bonus - auto fail status if another type is used
            //Fixes Slow in various cases where we add extra attacks or other actions that manually consume main or bonus action
            var either = rulesetCharacter.GetSubFeaturesByType<IActionPerformanceProvider>()
                .Any(provider => provider.EitherMainOrBonus);

            if (!either)
            {
                return;
            }

            var action = ServiceRepository.GetService<IGameLocationActionService>().AllActionDefinitions[actionId];
            var actionType = action.actionType;
            var mainRanks = __instance.currentActionRankByType[ActionType.Main];
            var bonusRanks = __instance.currentActionRankByType[ActionType.Bonus];

            // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
            switch (actionType)
            {
                case ActionType.Bonus when mainRanks > 0:
                case ActionType.Main when bonusRanks > 0:
                    __result = ActionStatus.Unavailable;
                    break;
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.RefreshActionPerformances))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefreshActionPerformances_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            return instructions
                //PATCH: Support for `IValidateDefinitionApplication`
                .ReplaceEnumerateFeaturesToBrowse<IActionPerformanceProvider>(
                    "GameLocationCharacter.RefreshActionPerformances.ValidateActionPerformanceProviders",
                    ValidateFeatureApplication.EnumerateActionPerformanceProviders)
                //PATCH: Support for action switching
                .ReplaceEnumerateFeaturesToBrowse<IAdditionalActionsProvider>(
                    "GameLocationCharacter.RefreshActionPerformances.ValidateAdditionalActionProviders",
                    ValidateFeatureApplication.EnumerateAdditionalActionProviders);
        }

        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter __instance)
        {
            //PATCH: support for action switching
            ActionSwitching.ResortPerformances(__instance);
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.SpendActionType))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SpendActionType_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter __instance, ActionType actionType)
        {
            //PATCH: support for action switching
            ActionSwitching.SpendActionType(__instance, actionType);
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.RefundActionUse))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RefundActionUse_Patch
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter __instance, ActionType actionType)
        {
            //PATCH: support for action switching
            ActionSwitching.RefundActionUse(__instance, actionType);

            //PATCH: fix reaction counted as not available even after refund
            if (actionType == ActionType.Reaction)
            {
                __instance.ReactionEngaged = false;
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.HandleActionExecution))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class HandleActionExecution_Patch
    {
        private static int _mainAttacks, _bonusAttacks, _mainRank, _bonusRank;

        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter __instance)
        {
            _mainRank = __instance.currentActionRankByType[ActionType.Main];
            _bonusRank = __instance.currentActionRankByType[ActionType.Bonus];
            _mainAttacks = __instance.UsedMainAttacks;
            _bonusAttacks = __instance.UsedBonusAttacks;
        }

        [UsedImplicitly]
        public static void Postfix(
            GameLocationCharacter __instance,
            CharacterActionParams actionParams,
            ActionScope scope)
        {
            //PATCH: support for `IReplaceAttackWithCantrip`
            ReplaceAttackWithCantrip.AllowAttacksAfterCantrip(__instance, actionParams, scope);

            //PATCH: support for action switching
            ActionSwitching.CheckIfActionSwitched(
                __instance, actionParams, scope, _mainRank, _mainAttacks, _bonusRank, _bonusAttacks);

            //PATCH: only call refresh once after above methods are called
            //BUGFIX: vanilla doesn't refresh attack modes on free attacks
            if (scope == ActionScope.Battle &&
                actionParams.ActionDefinition.Id
                    is Id.AttackFree
                    or Id.AttackMain
                    or Id.AttackOff)
            {
                __instance.RulesetCharacter.RefreshAttackModes();
            }
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.GetActionAvailableIterations))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GetActionAvailableIterations_Patch
    {
        [NotNull]
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            //PATCH: Support for ExtraAttacksOnActionPanel
            //replaces calls to FindExtraActionAttackModes to custom method which supports forced attack modes for offhand attacks
            var findAttacks = typeof(GameLocationCharacter).GetMethod("FindActionAttackMode");
            var method = new Func<
                GameLocationCharacter,
                Id,
                bool,
                bool,
                bool,
                ReadyActionType,
                RulesetAttackMode,
                RulesetAttackMode
            >(ExtraAttacksOnActionPanel.FindExtraActionAttackModesFromForcedAttack).Method;

            return instructions.ReplaceCalls(findAttacks,
                "GameLocationCharacter.GetActionAvailableIterations",
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Call, method));
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.IsActionOnGoing))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class IsActionOnGoing_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __instance, ref bool __result,
            Id actionId)
        {
            if (!CustomActionIdContext.IsToggleId(actionId))
            {
                return true;
            }

            __result = __instance.RulesetCharacter.IsToggleEnabled(actionId);

            return false;
        }
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.CheckConcentration))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckConcentration_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __instance, int damage)
        {
            //BUGFIX: don't roll if zero damage
            if (damage <= 0)
            {
                return false;
            }

            //PATCH: supports `IPreventRemoveConcentrationOnDamage`
            if (__instance.RulesetCharacter is not { } rulesetCharacter)
            {
                return true;
            }

            var hero = rulesetCharacter.GetOriginalHero();
            var concentratedSpell = hero?.ConcentratedSpell;

            if (concentratedSpell == null)
            {
                return true;
            }

            var shouldKeepConcentration = hero.GetSubFeaturesByType<IPreventRemoveConcentrationOnDamage>()
                .Any(x =>
                    x.SpellsThatShouldNotRollConcentrationCheckFromDamage(hero)
                        .Contains(concentratedSpell.SpellDefinition));

            return !shouldKeepConcentration;
        }

#if false
        [UsedImplicitly]
        public static void Postfix(
            GameLocationCharacter __instance,
            int damage,
            string damageType,
            bool stillConscious)
        {
            //PATCH: support for EffectWithConcentrationCheck
            ForceConcentrationCheck.ProcessConcentratedEffects(__instance, damage, damageType, stillConscious);
        }
#endif
    }

    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.GenerateCharacterDescription))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GenerateCharacterDescription_Patch
    {
        [UsedImplicitly]
        public static void Postfix(EntityDescription entityDescription)
        {
            Tooltips.AddDistanceToTooltip(entityDescription);
        }
    }

    //PATCH: supports RaceLightSensitivityApplyOutdoorsOnly
    //PATCH: changes only what differs, so a check that changes nothing uses no object ID (object-ID desyncs)
    // The game's version, and the mod's copy of it, removed the character's light conditions and effects and made new ones
    // on every call, even when nothing had changed. Each new condition takes a new object ID, and IDs are never given back,
    // so one computer calling this more often than another shifted all its later IDs. RefreshAll calls this through
    // GameLocationCharacter.CharacterRefreshed, so every one-sided refresh of such a hero used an ID.
    // Now it works out what the character should have, compares that with what it has, and touches only the difference.
    // Computers that see the same lighting changes make the same changes, however many times each one checks. This covers
    // every light affinity (the game's and the mod's), not only the races the mod's setting is about.
    [HarmonyPatch(typeof(GameLocationCharacter), nameof(GameLocationCharacter.CheckLightingAffinityEffects))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CheckLightingAffinityEffects_Patch
    {
        // Characters whose conditions and effects are being changed right now. Adding or removing a condition runs
        // handlers that could call this again and see a half-finished change; such a call does nothing. The RefreshAll at
        // the end is outside this on purpose: the check it causes finds nothing left to do.
        private static readonly HashSet<GameLocationCharacter> Updating = [];

        // Characters being refreshed by the RefreshAll below. If a handler or a forced category takes a wanted condition
        // away again, the check that refresh causes adds it again, and refreshing again would never end. No current light
        // condition does this (Codex's review); the old code would have looped too. Such a nested check still corrects
        // the condition but doesn't start another refresh.
        private static readonly HashSet<GameLocationCharacter> Refreshing = [];

        [UsedImplicitly]
        public static bool Prefix(GameLocationCharacter __instance)
        {
            if (!Updating.Add(__instance))
            {
                return false;
            }

            bool conditionsChanged;

            try
            {
                conditionsChanged = Update(__instance);
            }
            finally
            {
                Updating.Remove(__instance);
            }

            // as before, only a change of conditions refreshes the character, not a change of effects alone
            if (!conditionsChanged || !Refreshing.Add(__instance))
            {
                return false;
            }

            try
            {
                __instance.RulesetCharacter.RefreshAll();
            }
            finally
            {
                Refreshing.Remove(__instance);
            }

            return false;
        }

        private static bool Update(GameLocationCharacter __instance)
        {
            var rulesetCharacter = __instance.RulesetCharacter;
            var wantedConditions = new List<ConditionDefinition>();
            var wantedEffects = new List<EnvironmentEffectDefinition>();

            // read fully before changing anything: GetLightAffinityFeatures returns a list the game reuses for other
            // feature queries, which the handlers of a condition change could run
            //BEGIN PATCH
            // With "outdoors only" on, the Light Sensitivity of Dark Kobolds, Dark Elves and Gray Dwarves follows the
            // daylight rule in RacialSunlightSensitivity (exterior sector during the game's bright hours, 08:00-18:00),
            // which gives the condition in a category of its own. Their light affinity features are left out here, so
            // this personal-light check neither adds the condition nor keeps an old copy: one left in the light category
            // from a check made with the setting off is surplus and is removed below. With the setting off, the features
            // count as usual: the condition follows the hero's own illumination (a torch counts, an outdoor night
            // doesn't), as in the original mod. This replaced the earlier "outdoors only" test here, which kept the
            // personal-light rule and only dropped the condition in interior sectors.
            var daylightRuleOn = RacialSunlightSensitivity.Enabled;
            //END PATCH

            foreach (var lightingEffectAndConditionList in rulesetCharacter.GetLightAffinityFeatures()
                         .Where(featureDefinition =>
                             !daylightRuleOn || !RacialSunlightSensitivity.IsRacialFeature(featureDefinition))
                         .Select(featureDefinition =>
                             (featureDefinition as ILightingAffinityProvider)?.LightingEffectAndConditionList)
                         .Where(lightingEffectAndConditionList => lightingEffectAndConditionList != null))
            {
                foreach (var effectAndCondition in lightingEffectAndConditionList.Where(effectAndCondition =>
                             effectAndCondition.lightingState == __instance.lightingState))
                {
                    if (effectAndCondition.condition)
                    {
                        wantedConditions.Add(effectAndCondition.condition);
                    }

                    if (effectAndCondition.effect != null)
                    {
                        wantedEffects.Add(effectAndCondition.effect);
                    }

                    break;
                }
            }

            // start with everything the character has as surplus, then take off what's still wanted
            var surplusConditions = rulesetCharacter.ConditionsByCategory.TryGetValue(
                AttributeDefinitions.TagLightSensitivity, out var liveConditions)
                ? new List<RulesetCondition>(liveConditions)
                : [];
            var conditionsToAdd = new List<ConditionDefinition>();
            var satisfiedConditions = new List<ConditionDefinition>();

            foreach (var wanted in wantedConditions)
            {
                var index = surplusConditions.FindIndex(x => x.ConditionDefinition == wanted);

                if (index >= 0)
                {
                    surplusConditions.RemoveAt(index);
                    satisfiedConditions.Add(wanted);
                }
                // AddConditionOfCategory drops a second copy of a condition that doesn't allow several, so two features
                // giving the same one mean one on the character. Asking for two would never match and would keep making
                // a new one.
                else if (wanted.AllowMultipleInstances || !satisfiedConditions.Contains(wanted))
                {
                    conditionsToAdd.Add(wanted);
                    satisfiedConditions.Add(wanted);
                }
            }

            // an effect that has already ended counts as gone and is replaced, as the old code's rebuild did
            var surplusEffects = new List<RulesetEffect>(__instance.affectingLightEffects);
            var effectsToAdd = new List<EnvironmentEffectDefinition>();

            foreach (var wanted in wantedEffects)
            {
                var index = surplusEffects.FindIndex(x =>
                    x is RulesetEffectEnvironment { Terminated: false } effect &&
                    effect.EnvironmentEffectDefinition == wanted);

                if (index >= 0)
                {
                    surplusEffects.RemoveAt(index);
                }
                else
                {
                    effectsToAdd.Add(wanted);
                }
            }

            if (surplusConditions.Count == 0 && conditionsToAdd.Count == 0 &&
                surplusEffects.Count == 0 && effectsToAdd.Count == 0)
            {
                ObjectIdDiagnosticsContext.LightingUnchanged();

                return false;
            }

            ObjectIdDiagnosticsContext.LightingChanged(
                rulesetCharacter,
                __instance.lightingState,
                surplusConditions.Select(x => x.ConditionDefinition?.Name),
                conditionsToAdd.Select(x => x.Name),
                surplusEffects.Count,
                effectsToAdd.Count);

            foreach (var condition in surplusConditions)
            {
                rulesetCharacter.RemoveConditionOfCategory(
                    AttributeDefinitions.TagLightSensitivity, condition, false, true, false);
            }

            foreach (var effect in surplusEffects)
            {
                effect.Terminate(false);
                __instance.affectingLightEffects.Remove(effect);
            }

            foreach (var condition in conditionsToAdd)
            {
                rulesetCharacter.AddConditionOfCategory(
                    AttributeDefinitions.TagLightSensitivity,
                    RulesetCondition.CreateActiveCondition(
                        rulesetCharacter.Guid,
                        condition,
                        DurationType.Irrelevant,
                        0,
                        TurnOccurenceType.StartOfTurn,
                        __instance.Guid,
                        rulesetCharacter.CurrentFaction.Name),
                    false);
            }

            foreach (var effect in effectsToAdd)
            {
                __instance.affectingLightEffects.Add(ServiceRepository.GetService<IRulesetImplementationService>()
                    .InstantiateEffectEnvironment(
                        rulesetCharacter, effect, -1, 0, false, new BoxInt(), new int3(), string.Empty, false));
            }

            return surplusConditions.Count > 0 || conditionsToAdd.Count > 0;
        }
    }
}
