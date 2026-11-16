using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
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

    //PATCH: a flow of a gadget became triggered: it gets the record of whoever is behind that (the flow that
    //switched it on, the dialogue step, the character who set its timer going or who damaged the gadget). Every
    //kind of flow reports its trigger to the gadget through this one method (the gadget gives it to each of its
    //listeners as their handler), so one patch covers conditions, gadget events, variables, quest steps, timers and
    //the rest, where a patch per kind would need some twenty methods and miss the next kind.
    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.ListenerStatusChanged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_ListenerStatusChanged
    {
        [UsedImplicitly]
        public static void Postfix(GameGadgetListener listener)
        {
            DungeonMakerCommandsContext.NoteListenerStatusChanged(listener);
        }
    }

    //PATCH: the game starts the flow of a trigger it noted: from here on the flow acts for whoever is behind that
    //trigger. Each kind of flow overrides AcknowledgeTrigger itself (the base method is empty), so every override
    //in the game is patched; they are looked up, so a kind is not left out by a list going stale.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadgetListener_AcknowledgeTrigger
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(GameGadgetListener).Assembly))
            {
                if (!type.IsSubclassOf(typeof(GameGadgetListener)))
                {
                    continue;
                }

                var method = AccessTools.DeclaredMethod(type, nameof(GameGadgetListener.AcknowledgeTrigger),
                    Type.EmptyTypes);

                if (method != null && !method.IsAbstract)
                {
                    yield return method;
                }
            }
        }

        [UsedImplicitly]
        public static void Prefix(GameGadgetListener __instance)
        {
            DungeonMakerCommandsContext.NoteFlowStarted(__instance);
        }
    }

    //PATCH: a condition of a gadget changed: if that completes, or breaks, the conditions one of the gadget's
    //timers waits for, the timer is set going, or stopped, by whoever's flow did it. The state is the condition's
    //value before the call (null when the gadget did not have it yet). Nothing is changed.
    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.SetCondition))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_SetCondition
    {
        [UsedImplicitly]
        public static void Prefix(GameGadget __instance, int conditionIndex, out bool? __state)
        {
            __state = __instance.currentConditionStates != null &&
                      __instance.currentConditionStates.TryGetValue(conditionIndex, out var before)
                ? before
                : null;
        }

        [UsedImplicitly]
        public static void Postfix(GameGadget __instance, int conditionIndex, bool state, bool? __state)
        {
            DungeonMakerCommandsContext.NoteConditionSet(__instance, conditionIndex, __state, state);
        }
    }

    //PATCH: a flow resets its gadget's timers (the "reset time lapse" step): whoever's flow that is sets them going
    [HarmonyPatch(typeof(FunctorResetTimeLapse), nameof(FunctorResetTimeLapse.Execute))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FunctorResetTimeLapse_Execute
    {
        [UsedImplicitly]
        public static void Prefix(FunctorParametersDescription functorParameters)
        {
            DungeonMakerCommandsContext.NoteTimersReset(functorParameters);
        }
    }

    //PATCH: know who did something to the character bound to a gadget (a button or lever that can be attacked)
    //while the game tells the gadget about it. The game hands these four methods the character, but passes only
    //the amount, the kind or the tag on to the gadget's flows. The state goes back when the method ends.
    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.OnBoundCharacterDamaged))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_OnBoundCharacterDamaged
    {
        [UsedImplicitly]
        public static void Prefix(ulong sourceGuid, out (bool, GameLocationCharacter, string) __state)
        {
            __state = DungeonMakerCommandsContext.BeginCauser(sourceGuid, "damaged by");
        }

        [UsedImplicitly]
        public static void Finalizer((bool, GameLocationCharacter, string) __state)
        {
            DungeonMakerCommandsContext.EndCauser(__state);
        }
    }

    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.OnBoundCharacterAlterationInflicted))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_OnBoundCharacterAlterationInflicted
    {
        [UsedImplicitly]
        public static void Prefix(RulesetActor source, out (bool, GameLocationCharacter, string) __state)
        {
            __state = DungeonMakerCommandsContext.BeginCauser(source?.Guid ?? 0, "a spell or effect of");
        }

        [UsedImplicitly]
        public static void Finalizer((bool, GameLocationCharacter, string) __state)
        {
            DungeonMakerCommandsContext.EndCauser(__state);
        }
    }

    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.OnBoundCharacterTagRevealed))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_OnBoundCharacterTagRevealed
    {
        [UsedImplicitly]
        public static void Prefix(GameLocationCharacter revealedBy, out (bool, GameLocationCharacter, string) __state)
        {
            __state = DungeonMakerCommandsContext.BeginCauser(revealedBy, "revealed by");
        }

        [UsedImplicitly]
        public static void Finalizer((bool, GameLocationCharacter, string) __state)
        {
            DungeonMakerCommandsContext.EndCauser(__state);
        }
    }

    [HarmonyPatch(typeof(GameGadget), nameof(GameGadget.OnBoundCharacterCharacterWasStolenItems))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameGadget_OnBoundCharacterCharacterWasStolenItems
    {
        [UsedImplicitly]
        public static void Prefix(RulesetCharacter thief, out (bool, GameLocationCharacter, string) __state)
        {
            __state = DungeonMakerCommandsContext.BeginCauser(thief?.Guid ?? 0, "stolen from by");
        }

        [UsedImplicitly]
        public static void Finalizer((bool, GameLocationCharacter, string) __state)
        {
            DungeonMakerCommandsContext.EndCauser(__state);
        }
    }

    //PATCH: a gadget flow's "start dialogue" step built its dialogue (or observation): the dialogue belongs to
    //whoever set that flow off. Both of the game's start-dialogue functors build it through these methods.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NarrativeDirectionManager_PrepareSequence
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(NarrativeDirectionManager),
                nameof(NarrativeDirectionManager.PrepareDialogSequence));
            yield return AccessTools.Method(typeof(NarrativeDirectionManager),
                nameof(NarrativeDirectionManager.PrepareObservationSequence));
        }

        [UsedImplicitly]
        public static void Postfix(NarrativeSequence __result)
        {
            DungeonMakerCommandsContext.NoteSequencePrepared(__result);
        }
    }

    //PATCH: know when the game carries out the actions of a dialogue step, and for which dialogue: what they set
    //off (a variable, a quest step, a condition) acts for whoever the dialogue belongs to. This is the only place
    //a dialogue runs functors (NarrativeState.End calls it).
    [HarmonyPatch(typeof(NarrativeState), nameof(NarrativeState.ExecuteFunctors))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class NarrativeState_ExecuteFunctors
    {
        [UsedImplicitly]
        public static void Prefix(NarrativeState __instance, out bool __state)
        {
            DungeonMakerCommandsContext.BeginNarrativeActions(__instance);
            __state = true;
        }

        // the state says the prefix ran, so each end has its beginning
        [UsedImplicitly]
        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                DungeonMakerCommandsContext.EndNarrativeActions();
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

    //PATCH: keep who is behind the waiting commands and chains in the save. The game writes the campaign's
    //variables here; right before, the mod puts its own variable among them, and right after it takes it out
    //again, so the variable exists in the save only (DungeonMakerCommandsContext, "SAVES"). The state says the mod
    //put it in. Reading a save is not touched here: OnLocationReady reads the variable once the location is ready.
    [HarmonyPatch(typeof(GameVariableManager), nameof(GameVariableManager.SerializeElements))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GameVariableManager_SerializeElements
    {
        [UsedImplicitly]
        public static void Prefix(IElementsSerializer serializer, out bool __state)
        {
            __state = serializer is { Mode: Serializer.SerializationMode.Write } &&
                      DungeonMakerCommandsContext.BeforeVariablesSaved();
        }

        [UsedImplicitly]
        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                DungeonMakerCommandsContext.AfterVariablesSaved();
            }
        }
    }

    //PATCH: Dungeon Maker editor: hints above the text fields where a variable's name, or the gadget name a
    //command variable is given, is typed (DungeonMakerNameHints says what and where). Each of these three methods
    //is where the game ties one of those fields to what it edits; the postfix only puts a listener on the field.
    [HarmonyPatch(typeof(UserContentEditionModal), nameof(UserContentEditionModal.Show))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserContentEditionModal_Show
    {
        [UsedImplicitly]
        public static void Postfix(UserContentEditionModal __instance)
        {
            DungeonMakerNameHints.ForContentModal(__instance);
        }
    }

    [HarmonyPatch(typeof(GadgetParameterLine), nameof(GadgetParameterLine.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GadgetParameterLine_Bind
    {
        [UsedImplicitly]
        public static void Postfix(GadgetParameterLine __instance)
        {
            DungeonMakerNameHints.ForGadgetLine(__instance);
        }
    }

    [HarmonyPatch(typeof(UserParameterItem), nameof(UserParameterItem.CommondBind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserParameterItem_CommondBind
    {
        [UsedImplicitly]
        public static void Postfix(UserParameterItem __instance)
        {
            DungeonMakerNameHints.ForParameterItem(__instance);
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
