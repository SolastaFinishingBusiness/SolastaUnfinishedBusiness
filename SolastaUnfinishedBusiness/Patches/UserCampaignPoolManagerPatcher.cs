using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class UserCampaignPoolManagerPatcher
{
    //PATCH: allows the last X campaign files to be backed up in the mod folder
    [HarmonyPatch(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.SaveUserCampaign))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveUserCampaign_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var deleteMethod = typeof(File).GetMethod("Delete");
            var backupAndDeleteMethod = new Action<string, UserContent>(DungeonMakerContext.BackupAndDelete).Method;

            //PATCH: the campaign file records the mod version it was saved with (issue #42). The text the game
            //makes of the campaign gets one more field at its start (DungeonMakerStampContext.StampedJson), where
            //the game makes it, so the file and the copy sent to cloud storage are the same.
            var toJsonMethod = DungeonMakerStampContext.ToJsonMethod;
            var stampedJsonMethod = new Func<object, string>(DungeonMakerStampContext.StampedJson).Method;

            var code = instructions.ReplaceCalls(deleteMethod, "UserCampaignPoolManager.SaveUserCampaign",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, backupAndDeleteMethod));

            // without the game's ToJson there is nothing to replace, and the file is written as the game does
            return toJsonMethod == null
                ? code
                : code.ReplaceCalls(toJsonMethod, "UserCampaignPoolManager.SaveUserCampaign.ToJson",
                    new CodeInstruction(OpCodes.Call, stampedJsonMethod));
        }

        [UsedImplicitly]
        public static void Postfix(UserCampaignPoolManager __instance, UserCampaign userCampaign)
        {
            // the file the game wrote (or tried to): the method's own file name for this title
            var path = userCampaign?.Title == null ? null : __instance.BuildUserCampaignFilename(userCampaign.Title);

            DungeonMakerStampContext.NoteCampaignSaved(userCampaign, path);
        }
    }

    //PATCH: note the mod version a campaign file was saved with while the game reads it (issue #42). The game
    //reads the file's whole text once (TextReader.ReadToEnd); that call is replaced by one that returns the same
    //text and notes the mod's field at its start, and the postfix ties the note to the campaign that was read.
    [HarmonyPatch(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.ReadCampaignFromDisk))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ReadCampaignFromDisk_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var readToEndMethod = typeof(TextReader).GetMethod("ReadToEnd");
            var readNotedMethod = new Func<TextReader, string>(DungeonMakerStampContext.ReadNoted).Method;

            return instructions.ReplaceCalls(readToEndMethod, "UserCampaignPoolManager.ReadCampaignFromDisk",
                new CodeInstruction(OpCodes.Call, readNotedMethod));
        }

        [UsedImplicitly]
        public static void Postfix(UserCampaign __result)
        {
            DungeonMakerStampContext.NoteCampaignRead(__result);
        }
    }
}
