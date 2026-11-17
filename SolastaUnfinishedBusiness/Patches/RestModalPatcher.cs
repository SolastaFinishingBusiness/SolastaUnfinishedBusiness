using System;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class RestModalPatcher
{
    // a rest the game allows waits for every other player's approval first
    private static bool MustWaitForApproval(RestType restType, Action proceed)
    {
        var failure = string.Empty;

        if (ServiceRepository.GetService<IGameRestingService>()?.CanExecuteLocationRest(restType, ref failure, true) !=
            true)
        {
            return false;
        }

        return MultiplayerApprovalContext.MustWaitForApproval(
            restType == RestType.ShortRest
                ? MultiplayerApprovalContext.ApprovalAction.ShortRest
                : MultiplayerApprovalContext.ApprovalAction.LongRest,
            string.Empty, null, MultiplayerApprovalContext.RestSeconds(restType), proceed);
    }

    //PATCH: asks every player to approve a short rest (MULTIPLAYER)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnStartShortRestCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartShortRestCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestModal __instance)
        {
            GameRestingManagerPatcher.InRestScreen = true;

            return !MustWaitForApproval(RestType.ShortRest, __instance.OnStartShortRestCb);
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }

    //PATCH: asks every player to approve a long rest (MULTIPLAYER)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnStartLongRestCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnStartLongRestCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(RestModal __instance)
        {
            GameRestingManagerPatcher.InRestScreen = true;

            // a campfire set to rest at once pressing Start itself: the game's own Start, with no rest checks, no wait
            // and no approval, as the story puts the party to rest there
            if (CampfireRestContext.IsForcedStart)
            {
                return true;
            }

            // the campfire's gather is over and the rest checks passed (CampfireRestContext): the game's own Start
            if (CampfireRestContext.IsReplayingStart)
            {
                return true;
            }

            //PATCH: when the party already rested today, waits until it can rest again, then rests (at a campfire, the
            //whole party fast travels to it before the wait)
            if (LongRestWaitContext.TryWaitThenRest(__instance))
            {
                return false;
            }

            //PATCH: at a campfire, a party out of the long rest range fast travels there first, then rests; in
            //multiplayer the gather and the rest are approved together (CampfireRestContext)
            if (!CampfireRestContext.GathersAtCampfire())
            {
                return !MustWaitForApproval(RestType.LongRest, __instance.OnStartLongRestCb);
            }

            var failure = string.Empty;

            // every other rest rule must pass before the party travels (the gather only fixes the distance); the
            // button is only enabled when they do, so this is a rule that stopped passing since, shown as the game
            // shows it
            if (ServiceRepository.GetService<IGameRestingService>()
                    ?.CanExecuteLocationRest(RestType.LongRest, ref failure, true) != true)
            {
                Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);

                return false;
            }

            // a dead, dying or unconscious leader can't lead the travel: said before anyone is asked to approve it
            if (!CampfireRestContext.LeaderCanTravel())
            {
                return false;
            }

            if (!MultiplayerApprovalContext.MustWaitForApproval(
                    MultiplayerApprovalContext.ApprovalAction.CampfireLongRest, string.Empty, null,
                    MultiplayerApprovalContext.RestSeconds(RestType.LongRest),
                    () => CampfireRestContext.StartGather(__instance)))
            {
                CampfireRestContext.StartGather(__instance);
            }

            return false;
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }

    //PATCH: a rest screen that isn't a campfire's own forgets the campfire, and any rest screen but the one reopened
    //after a campfire's gather ends that gather (CampfireRestContext)
    [HarmonyPatch(typeof(RestModal), nameof(RestModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            CampfireRestContext.OnRestScreenShown();
        }
    }

    //PATCH: the rest screen's start buttons are enabled for every player, not only the host (MULTIPLAYER)
    [HarmonyPatch(typeof(RestBeforePanel), nameof(RestBeforePanel.SetRestType))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SetRestType_Patch
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            GameRestingManagerPatcher.InRestScreen = true;
        }

        [UsedImplicitly]
        public static void Finalizer()
        {
            GameRestingManagerPatcher.InRestScreen = false;
        }
    }
}
