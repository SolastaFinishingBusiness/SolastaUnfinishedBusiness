using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection.Emit;
using HarmonyLib;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[HarmonyPatch(typeof(GameSerializationManager), nameof(GameSerializationManager.SaveGame))]
internal static class SaveStampPatcher
{
    // Replace only the gzip writer in SaveGame, after persistent serialization finishes.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var constructor = AccessTools.Constructor(typeof(GZipStream), [typeof(Stream), typeof(CompressionMode)]);
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Newobj && Equals(instruction.operand, constructor))
            {
                var manager = new CodeInstruction(OpCodes.Ldarg_0);
                manager.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                yield return manager;
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SaveStampContext), nameof(SaveStampContext.Open));
            }

            yield return instruction;
        }
    }
}
