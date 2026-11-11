using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class CharacterActionPushedPatcher
{
    //PATCH: Eldritch Blast's pushes and pulls: a creature moved over a drop falls only after the cast's last move on
    // it (issue #37, EldritchBlastFallContext, a setting).
    //
    // After a push has landed, the game's push action waits for the creature's "back on its feet" animation event,
    // unless the creature is falling (or taking damage, or can't be shoved). That event is only sent when the push
    // lands on a cell the creature can stay on; over a drop the game counts on the fall having started, which sets
    // Falling. With the fall put off, the push would wait for an event that never comes until the game gives up after
    // 10 seconds of real time. So the one read of Falling in the push action goes through the mod's test, which is
    // also true for a creature whose fall was put off and that can't stay where it is. For every other creature it
    // returns Falling itself, so nothing changes.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class ExecuteImpl_Patch
    {
        // the coroutine's state machine; AccessTools.EnumeratorMoveNext isn't in the Harmony the build uses
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(CharacterActionPushed).GetNestedTypes(BindingFlags.NonPublic)
                    .First(x => x.Name.StartsWith($"<{nameof(CharacterActionPushed.ExecuteImpl)}>")),
                "MoveNext");
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var fallingMethod = typeof(GameLocationCharacter).GetProperty(nameof(GameLocationCharacter.Falling))!
                .GetGetMethod();
            var myFallingMethod =
                new Func<GameLocationCharacter, bool>(EldritchBlastFallContext.IsFallingOrLeftInAir).Method;

            return instructions.ReplaceCalls(fallingMethod,
                "CharacterActionPushed.ExecuteImpl",
                new CodeInstruction(OpCodes.Call, myFallingMethod));
        }
    }
}
