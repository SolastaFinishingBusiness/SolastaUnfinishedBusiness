using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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

    //PATCH: know which gadget the game is updating. GameGadget.Update is where the game starts a gadget's
    //triggered flows and runs their steps, so while it lasts the gadget says which flow is running
    //(DungeonMakerCommandsContext.RunningFlow). That is what lets a flow switched on by another flow act for the
    //character who set the first one off, whatever functor did the switching. Nothing of the game is changed: the
    //prefix and the finalizer only note the gadget and put the earlier note back, however the update ends.
    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.Update))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_Update
    {
        [UsedImplicitly]
        public static void Prefix(GameGadget __instance, out GameGadget __state)
        {
            __state = DungeonMakerCommandsContext.BeginGadgetUpdate(__instance);
        }

        [UsedImplicitly]
        public static void Finalizer(GameGadget __state)
        {
            DungeonMakerCommandsContext.EndGadgetUpdate(__state);
        }
    }

    //PATCH: know which flow the game is running when it runs one in a single go (a flow that is not asynchronous,
    //every flow during the fast travel simulation, the "location entered" flows forced after a load). The other
    //way of running a flow, as a coroutine, is read from the gadget (see the patch above).
    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.ExecuteFunctorSequenceSync))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_ExecuteFunctorSequenceSync
    {
        [UsedImplicitly]
        public static void Prefix(GameGadget __instance, int listenerIndex, out GameGadgetListener __state)
        {
            __state = DungeonMakerCommandsContext.BeginSyncFlow(__instance, listenerIndex);
        }

        [UsedImplicitly]
        public static void Finalizer(GameGadgetListener __state)
        {
            DungeonMakerCommandsContext.EndSyncFlow(__state);
        }
    }

    //PATCH: a gadget's condition changed, or an event was raised on it: if that switched one of its flows on while
    //another flow was running, the flow inherits who set that one off. These two are the only ways the game
    //passes an activation from one flow to another (the only callers of GameGadget.SetCondition and
    //RaiseGadgetEvent are seven functors, and each calls them inside its own step), so every such functor is
    //covered here without a patch of its own. The state is "triggered" before the call.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListener_SwitchedOn
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameGadgetListenerConditionToggle),
                nameof(GameGadgetListenerConditionToggle.ConditionStatusChanged));
            yield return AccessTools.Method(typeof(GameGadgetListenerGadgetEvent),
                nameof(GameGadgetListenerGadgetEvent.GadgetEventRaised));
        }

        [UsedImplicitly]
        public static void Prefix(GameGadgetListener __instance, out bool __state)
        {
            __state = __instance.IsTriggered;
        }

        [UsedImplicitly]
        public static void Postfix(GameGadgetListener __instance, bool __state)
        {
            DungeonMakerCommandsContext.NoteFlowSwitchedOn(__instance, __state, __instance.IsTriggered);
        }
    }

    //PATCH: the game starts the flow of a trigger it noted: from here on the flow acts for the character who
    //caused that trigger (an area's or a pressure plate's, an interaction's), or who set off the flow that
    //switched this one on (a condition's or a gadget event's). Each listener type overrides AcknowledgeTrigger
    //itself, so each override is patched.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListener_AcknowledgeTrigger
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameGadgetListenerVolumetricTrigger),
                nameof(GameGadgetListenerVolumetricTrigger.AcknowledgeTrigger));
            yield return AccessTools.Method(typeof(GameGadgetListenerPressurePlate),
                nameof(GameGadgetListenerPressurePlate.AcknowledgeTrigger));
            yield return AccessTools.Method(typeof(GameGadgetListenerCharacterInteraction),
                nameof(GameGadgetListenerCharacterInteraction.AcknowledgeTrigger));
            yield return AccessTools.Method(typeof(GameGadgetListenerConditionToggle),
                nameof(GameGadgetListenerConditionToggle.AcknowledgeTrigger));
            yield return AccessTools.Method(typeof(GameGadgetListenerGadgetEvent),
                nameof(GameGadgetListenerGadgetEvent.AcknowledgeTrigger));
        }

        [UsedImplicitly]
        public static void Prefix(GameGadgetListener __instance)
        {
            DungeonMakerCommandsContext.NoteTriggerAcknowledged(__instance);
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

    //PATCH: the same for pressure plates
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

    //PATCH: a clock skip is one wait for an object's timers, as a long rest is (issue #42's Clock Skip
    //specification): its "every N hours" (GameGadgetListenerHourUpdate) and "after a time"
    //(GameGadgetListenerElapsedTime) listeners don't check themselves while a skip runs, or while a skip saved
    //with the game waits to go on, and check once when it ends (DungeonMakerClockContext.HoldTimers, CheckTimers).
    //The checks themselves are held, not the clock's ticks, because the game also checks from other places: its
    //ordinary per-frame clock updates between the skip's batches, and each listener's PostLoad when a save is
    //loaded (Codex's review of cd9aa39a, from the game's IL). The ticks still run, so the elapsed-time listener
    //keeps adding up the seconds (only while its conditions match, as the game does), and the hour listener's
    //check compares the total hours with its last activation: the one check at the end gives a due timer one
    //activation and restarts an hour timer's interval from the arrival hour.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListener_CheckTimer
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameGadgetListenerHourUpdate),
                nameof(GameGadgetListenerHourUpdate.CheckHoursElapsed));
            yield return AccessTools.Method(typeof(GameGadgetListenerElapsedTime),
                nameof(GameGadgetListenerElapsedTime.CheckSecondsElapsed));
        }

        [UsedImplicitly]
        public static bool Prefix()
        {
            return !DungeonMakerClockContext.HoldTimers;
        }
    }
}
