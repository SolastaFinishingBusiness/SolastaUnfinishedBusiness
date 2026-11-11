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
    // it, and the cast's pushes don't play the falling pose (issue #37, EldritchBlastFallContext, a setting).
    //
    // After a push has landed, the game's push action waits for the creature's "back on its feet" animation event,
    // unless the creature is falling (or taking damage, or can't be shoved). That event is only sent when the push
    // lands on a cell the creature can stay on; over a drop the game counts on the fall having started, which sets
    // Falling. With the fall put off, the push would wait for an event that never comes until the game gives up after
    // 10 seconds of real time. So the one read of Falling in the push action goes through the mod's test, which is
    // also true for a creature whose fall was put off and that can't stay where it is, and for every push of the
    // cast, which never entered the pose that event belongs to (see AnimateMove_Patch below). For every other
    // creature, and with the setting off, it returns Falling itself, so nothing changes.
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

    //PATCH: Eldritch Blast's pushes and pulls with the fall setting on: the creature travels without the falling pose
    // (issue #37; user, 2026-11-11). The game switches that pose on at the start of every forced move, in the
    // coroutine that animates a move, and off again when the move ends on ground the creature can stand on, which is
    // the "getting up" seen after each push and pull. The switching on goes through the mod's test, which leaves it
    // out for a push of a held creature's cast and passes everything else on. The switching off at the end is left
    // as it is: without the pose there is nothing to leave. A real fall sets the pose in its own coroutine
    // (AnimateFall), which is untouched. With the setting off no creature is held and the game's call is made as is.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class AnimateMove_Patch
    {
        [UsedImplicitly]
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(WorldLocationCharacter).GetNestedTypes(BindingFlags.NonPublic)
                    .First(x => x.Name.StartsWith($"<{nameof(WorldLocationCharacter.AnimateMove)}>")),
                "MoveNext");
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var poseMethod = typeof(GraphicsCharacter).GetProperty(nameof(GraphicsCharacter.IsFalling))!
                .GetSetMethod();
            var myPoseMethod =
                new Action<GraphicsCharacter, bool>(EldritchBlastFallContext.SetFallingPose).Method;

            // the first of the two calls is the one at the start of a forced move
            return instructions.ReplaceCall(poseMethod,
                1, "WorldLocationCharacter.AnimateMove",
                new CodeInstruction(OpCodes.Call, myPoseMethod));
        }
    }
}
