using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

// The hooks of the Dungeon Maker campaign commands (issue #42). What each one is for is explained in
// DungeonMakerCommandsContext; every patch here only passes what the game is doing on to it.
[UsedImplicitly]
public static class DungeonMakerCommandsPatcher
{
    //PATCH: work on the campaign's variable commands right after the game updated its gadgets, which is where the
    //game itself starts and continues gadget flows
    [HarmonyPatch(typeof(GameLocation), nameof(GameLocation.UpdateGadgets))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocation_UpdateGadgets
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            DungeonMakerCommandsContext.Tick();
        }
    }

    //PATCH: know which gadget flow is writing a variable while a Variable Operation runs. Execute only builds the
    //coroutine, so the returned enumerator is wrapped and each of its steps is bracketed.
    [HarmonyPatch(typeof(FunctorVariableOperation), nameof(FunctorVariableOperation.Execute))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FunctorVariableOperation_Execute
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, FunctorParametersDescription functorParameters)
        {
            try
            {
                while (true)
                {
                    var token = DungeonMakerCommandsContext.BeginVariableOperation(functorParameters);
                    bool more;

                    try
                    {
                        more = values.MoveNext();
                    }
                    finally
                    {
                        DungeonMakerCommandsContext.EndVariableOperation(token);
                    }

                    if (!more)
                    {
                        yield break;
                    }

                    yield return values.Current;
                }
            }
            finally
            {
                (values as IDisposable)?.Dispose();
            }
        }
    }

    //PATCH: know when the game's Teleport functor is moving characters: an area several of them land in was
    //activated by a group arriving together, not by one character walking in
    [HarmonyPatch(typeof(FunctorTeleport), nameof(FunctorTeleport.Execute))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FunctorTeleport_Execute
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values)
        {
            try
            {
                while (true)
                {
                    bool more;

                    DungeonMakerCommandsContext.BeginGroupMove();

                    try
                    {
                        more = values.MoveNext();
                    }
                    finally
                    {
                        DungeonMakerCommandsContext.EndGroupMove();
                    }

                    if (!more)
                    {
                        yield break;
                    }

                    yield return values.Current;
                }
            }
            finally
            {
                (values as IDisposable)?.Dispose();
            }
        }
    }

    //PATCH: see which characters the game places, and where, while a party teleport's functor runs: that is how
    //the command knows that every traveller arrived
    [HarmonyPatch(typeof(GameLocationPositioningManager), nameof(GameLocationPositioningManager.TeleportCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationPositioningManager_TeleportCharacter
    {
        [UsedImplicitly]
        public static void Postfix(GameLocationCharacter character, TA.int3 newPosition)
        {
            DungeonMakerCommandsContext.NoteCharacterTeleported(character, newPosition);
        }
    }

    //PATCH: a character used a gadget: who it was is noted for the flow this starts, and if the flow will ask for
    //a one-character teleport, its place in the queue is kept from this moment, as for areas and plates
    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.Interact))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_Interact
    {
        [UsedImplicitly]
        public static void Postfix(GameGadget __instance, int listenerIndex, GameLocationCharacter actingCharacter)
        {
            DungeonMakerCommandsContext.NoteInteracted(__instance, listenerIndex, actingCharacter);
        }
    }

    //PATCH: the game starts the flow of an interaction it accepted: that flow acts for the character who used
    //the gadget then
    [HarmonyPatch(typeof(GameGadgetListenerCharacterInteraction),
        nameof(GameGadgetListenerCharacterInteraction.AcknowledgeTrigger))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerCharacterInteraction_AcknowledgeTrigger
    {
        [UsedImplicitly]
        public static void Prefix(GameGadgetListenerCharacterInteraction __instance)
        {
            DungeonMakerCommandsContext.NoteTriggerAcknowledged(__instance);
        }
    }

    //PATCH: remember whose move made an Area Activator fire (the game passes the character that moved)
    [HarmonyPatch(typeof(GameGadgetListenerVolumetricTrigger),
        nameof(GameGadgetListenerVolumetricTrigger.OccupationChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerVolumetricTrigger_OccupationChanged
    {
        [UsedImplicitly]
        public static void Prefix(GameGadgetListenerVolumetricTrigger __instance, out bool __state)
        {
            __state = __instance.occupationTriggered;
        }

        [UsedImplicitly]
        public static void Postfix(
            GameGadgetListenerVolumetricTrigger __instance, GameLocationCharacter character, bool __state)
        {
            DungeonMakerCommandsContext.NoteOccupationChanged(
                __instance, character, __state, __instance.occupationTriggered);
        }
    }

    //PATCH: the game starts the flow of the trigger it noted: that flow acts for the character who caused it
    [HarmonyPatch(typeof(GameGadgetListenerVolumetricTrigger),
        nameof(GameGadgetListenerVolumetricTrigger.AcknowledgeTrigger))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerVolumetricTrigger_AcknowledgeTrigger
    {
        [UsedImplicitly]
        public static void Prefix(GameGadgetListenerVolumetricTrigger __instance)
        {
            DungeonMakerCommandsContext.NoteTriggerAcknowledged(__instance);
        }
    }

    //PATCH: the same two hooks for pressure plates
    [HarmonyPatch(typeof(GameGadgetListenerPressurePlate), nameof(GameGadgetListenerPressurePlate.OccupationChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerPressurePlate_OccupationChanged
    {
        [UsedImplicitly]
        public static void Prefix(GameGadgetListenerPressurePlate __instance, out bool __state)
        {
            __state = __instance.occupationTriggered;
        }

        [UsedImplicitly]
        public static void Postfix(
            GameGadgetListenerPressurePlate __instance, GameLocationCharacter character, bool __state)
        {
            DungeonMakerCommandsContext.NoteOccupationChanged(
                __instance, character, __state, __instance.occupationTriggered);
        }
    }

    [HarmonyPatch(typeof(GameGadgetListenerPressurePlate), nameof(GameGadgetListenerPressurePlate.AcknowledgeTrigger))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerPressurePlate_AcknowledgeTrigger
    {
        [UsedImplicitly]
        public static void Prefix(GameGadgetListenerPressurePlate __instance)
        {
            DungeonMakerCommandsContext.NoteTriggerAcknowledged(__instance);
        }
    }

    //PATCH: the location is going away: end what is waiting. A prefix, so it runs when the unload is asked for.
    [HarmonyPatch(typeof(GameLocationManager), nameof(GameLocationManager.UnloadLocationAsync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameLocationManager_UnloadLocationAsync
    {
        [UsedImplicitly]
        public static void Prefix()
        {
            DungeonMakerCommandsContext.OnLocationUnloading();
        }
    }

    //PATCH: keep the campaign's clock variables up to date. The state is the clock in minutes before the update.
    [HarmonyPatch(typeof(GameTime), nameof(GameTime.UpdateTime))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameTime_UpdateTime
    {
        [UsedImplicitly]
        public static void Prefix(GameTime __instance, out int __state)
        {
            __state = __instance.TimeInfo?.ComputeMinutes() ?? 0;
        }

        [UsedImplicitly]
        public static void Postfix(GameTime __instance, int __state)
        {
            DungeonMakerClockContext.OnTimeUpdated(__instance, __state);
        }
    }

    //PATCH: an object's "every N hours" timer (GameGadgetListenerHourUpdate) is not checked during a clock skip's
    //inner steps, so a skip is one wait for it, as a long rest is (issue #42's Clock Skip specification). The game
    //raises the hour tick once per clock update, and the timer then compares the total hours with its last
    //activation; a skip's last step, which lets the tick through, gives a due timer its one activation and
    //restarts its interval from the arrival hour. Without this, a skip of several days set its flow off once per
    //gadget update between the steps.
    [HarmonyPatch(typeof(GameGadgetListenerHourUpdate), nameof(GameGadgetListenerHourUpdate.RealTimeHourTicked))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerHourUpdate_RealTimeHourTicked
    {
        [UsedImplicitly]
        public static bool Prefix()
        {
            return !DungeonMakerClockContext.HoldTimers;
        }
    }

    //PATCH: the same for an object's "after a time" timer (GameGadgetListenerElapsedTime). It adds the seconds of
    //each clock update to its total and goes off when the total reaches its duration. During a skip's inner steps
    //the seconds are still added, as the game would (only while its conditions match), but the check is left to
    //the last step, so the skip gives it one activation, as a single clock update of the same length would.
    [HarmonyPatch(typeof(GameGadgetListenerElapsedTime), nameof(GameGadgetListenerElapsedTime.RealTimeSecondTicked))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListenerElapsedTime_RealTimeSecondTicked
    {
        [UsedImplicitly]
        public static bool Prefix(GameGadgetListenerElapsedTime __instance, int seconds)
        {
            if (!DungeonMakerClockContext.HoldTimers)
            {
                return true;
            }

            if (__instance.IsMatchingConditions())
            {
                __instance.lapsedDuration += seconds;
            }

            return false;
        }
    }
}
