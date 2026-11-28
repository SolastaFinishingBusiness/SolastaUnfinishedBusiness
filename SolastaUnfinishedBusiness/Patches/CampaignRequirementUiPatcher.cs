using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CampaignRequirementUiPatcher
{
    //PATCH: the "requires the mod" toggle on the campaign editor
    [HarmonyPatch(typeof(UserCampaignEditorScreen), nameof(UserCampaignEditorScreen.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnBeginShow_Patch
    {
        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance)
        {
            CampaignRequirementUi.OnEditorShown(__instance);
        }
    }

    [HarmonyPatch(typeof(UserCampaignEditorScreen), nameof(UserCampaignEditorScreen.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class Refresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance)
        {
            CampaignRequirementUi.Refresh(__instance);
        }
    }

    //PATCH: drops the requirement question when another message takes over the message window
    [HarmonyPatch(typeof(MessageModal), nameof(MessageModal.Show))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class MessageModal_Show_Patch
    {
        [UsedImplicitly]
        public static void Prefix(MessageModal __instance)
        {
            CampaignRequirementUi.OnMessageShown(__instance);
        }
    }

    //PATCH: asks the author once whether a campaign with command-like variables requires the mod
    [HarmonyPatch(typeof(UserCampaignEditorScreen), nameof(UserCampaignEditorScreen.OnSaveCb))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class OnSaveCb_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(UserCampaignEditorScreen __instance)
        {
            return CampaignRequirementUi.BeforeSave(__instance);
        }
    }
}
