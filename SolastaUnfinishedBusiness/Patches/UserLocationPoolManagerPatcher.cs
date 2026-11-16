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
public static class UserLocationPoolManagerPatcher
{
    //PATCH: allows the last X campaign files to be backed up in the mod folder
    [HarmonyPatch(typeof(UserLocationPoolManager), nameof(UserLocationPoolManager.SaveUserLocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class SaveUserLocation_Patch
    {
        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var deleteMethod = typeof(File).GetMethod("Delete");
            var backupAndDeleteMethod = new Action<string, UserContent>(DungeonMakerContext.BackupAndDelete).Method;

            //PATCH: a location saved on its own records the mod version too, as a campaign does (issue #42;
            //see UserCampaignPoolManagerPatcher and DungeonMakerStampContext)
            var toJsonMethod = DungeonMakerStampContext.ToJsonMethod;
            var stampedJsonMethod = new Func<object, string>(DungeonMakerStampContext.StampedJson).Method;

            var code = instructions.ReplaceCalls(deleteMethod, "UserLocationPoolManager.SaveUserLocation",
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, backupAndDeleteMethod));

            // without the game's ToJson there is nothing to replace, and the file is written as the game does
            return toJsonMethod == null
                ? code
                : code.ReplaceCalls(toJsonMethod, "UserLocationPoolManager.SaveUserLocation.ToJson",
                    new CodeInstruction(OpCodes.Call, stampedJsonMethod));
        }
    }
}
