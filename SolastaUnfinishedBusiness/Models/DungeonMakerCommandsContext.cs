using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SolastaUnfinishedBusiness.Models;

// Dungeon Maker campaign commands (issue #42, adapted from BlackguardRogue's Dungeon Maker tools).
//
// A campaign author asks for something the Dungeon Maker has no gadget link for by writing a campaign variable:
// start an NPC's or object's dialogue, have a character roll a Roll Activator's skill check, teleport one character
// or the party to a Node, switch torches. Variables that don't exist are ignored, so a campaign that doesn't define
// them, and a player without the mod, are unaffected.
//
// COMMAND VARIABLES (String; names are case-sensitive; the mod sets them back to "" when the command ends)
//   Dialog_Trigger       the name of the NPC or object whose dialogue should start: the gadget's own name, the
//                        NPC's internal unique name, or the name or title the author gave the NPC in the Dungeon
//                        Maker. Never a name as a player's language shows it: in multiplayer every computer has to
//                        find the same gadget, whatever language it runs in.
//   Roll_Trigger         the name of a Roll Activator gadget; the acting character rolls its check.
//   Teleport_Node        the name of a gadget (a Node, usually): the acting character is teleported near it.
//   Teleport_PartyNode   the name of a gadget (a Node, usually): the whole party, with the guests travelling with it, is
//                        teleported there in formation. It is done when the game has placed every one of them.
//   Writing a new value while the earlier one is still waiting replaces it; writing "" cancels it.
//   Teleport_Node is the exception: every activation that writes it is a request of its own, and they are
//   carried out one after the other in the order they were made (see "ONE-CHARACTER TELEPORT").
//
// STATUS (optional String variables named <command>_Status, e.g. Roll_Trigger_Status)
//   Pending (waiting for something temporary), Running (handed to the game), Succeeded, Failed, Cancelled.
//   A failure is also written to the mod's log with its reason, once.
//
// WHO ACTS. Roll_Trigger and Teleport_Node need one particular character, and it is never guessed:
//   - when an Area Activator or pressure plate fires, it is the character whose move made it fire (not whoever
//     else stands in it, and not a character who moved elsewhere afterwards). When several characters arrive at
//     once (the party entering the location, a party teleport), nobody "walked in": it then only works if exactly
//     one party member is in the area;
//   - when a character interacts with something, it is that character;
//   - when one of those gadgets activates another gadget (its Dungeon Maker "Activate" operation, e.g. an Area
//     Activator activating a Variable Operation gadget that writes the variable), the activated gadget's flow acts
//     for the same character, through any number of gadgets (NoteConditionToggled, issue #42: the original author's
//     test campaign is built this way). If the start of such a chain isn't traced to one character, the one
//     eligible character the game passed along the chain acts, if there is exactly one;
//   - anything else (a timer, a variable or condition activator, a quest step, a variable set from a dialogue, a
//     save loaded while the command was waiting, or a chain they start) has no acting character, so the command
//     fails and says so: a line in the log with what wrote the variable, the chain, what started it and the
//     characters passed along (LogResolution), and the game's alert at the top of the screen (AlertFailure).
//   For Roll_Trigger the character must still be a conscious party member in the location when the command runs.
//   Dialog_Trigger uses the acting character when there is one, otherwise the first conscious party member.
//   Teleport_PartyNode needs nobody.
//
// ONE-CHARACTER TELEPORT (Teleport_Node), as specified in issue #42:
//   - It moves the character that caused the activation and never another. Only a party member or a guest
//     travelling with the party can cause it: an activation by an enemy or another NPC fails, and nobody moves,
//     even with a party member standing in the same area. The character is moved in the state it is in
//     (unconscious, dying or dead too), as long as it is still in the location.
//   - Every activation makes its own request, even for the same Node; a new one does not replace one that is
//     waiting. They are carried out in the order the game reported the activations, one per gadget update, each
//     ended before the next character is placed. The order is that of the activations, not of the variable
//     writes: an Area Activator or pressure plate whose flow will write Teleport_Node takes its place in the queue
//     the moment it is set off (ExpectSoloTeleport), so a flow that reaches its Variable Operation late, behind a
//     Wait for instance, is not overtaken by one set off after it. A gadget a character uses does the same when
//     the game accepts the interaction (NoteInteracted).
//   - The destination can be any uniquely named gadget. The native Teleport functor's placement search
//     chooses a nearby cell and the marker supplies facing. Arrival is checked against the chosen cell.
//
// TORCHES (Int: 1 = lit, 2 = out, 0 or missing = no opinion; strongest first)
//   Torch_<gadget name>  one torch;   Torches_Lit  every torch in the location;
//   Torches_Auto         (Int or Bool) on outdoor maps, lit from 18:00 to 08:00.
//
// DISCOVERY
//   Dialog_ListGadgets   Int. 1 lists the interactive gadgets of the location in the log, 2 lists every gadget
//                        object with its conditions (to find Node and torch names); the mod sets it back to 0.
//
// HOW IT RUNS, and why (the first version polled these variables from the game clock's per-frame update):
//   - A request is taken when the variable is written, through the game's own "variable value changed" event,
//     the one a Variable Activator gadget listens to. The gadget flow that wrote it is known at that moment
//     (see BeginVariableOperation), which is what gives the acting character.
//   - Requests are worked on right after the game updates its gadgets (GameLocation.UpdateGadgets), the place
//     where the game itself starts and continues gadget flows. So nothing here runs while gadgets are not being
//     updated, and nothing depends on the clock ticking (it does not tick during a battle).
//   - A dialogue or a roll is started with GameGadget.Interact, which only marks the gadget's flow as triggered;
//     the game then runs the flow from its own gadget update, as after a click.
//   - A party teleport runs the game's own Teleport functor through the functor service as a coroutine, continued
//     once per gadget update, the way a gadget runs its functors. It is never driven to its end in one go.
//   - Each command has its own state: one that waits or fails does not hold up or switch off the others.
//   - What can pass is waited for (a dialogue playing, conditions not met yet), for an hour of game time at
//     most. What can't work is ended at once with one log line saying why.
//   - Everything is dropped when the location unloads; waiting commands end as Cancelled.
//
// MULTIPLAYER: nothing here sends anything. Every computer sees the same variable write from the same gadget flow
// and answers it the same way, like a gadget flow that triggers another gadget. Not tested on two computers.
internal static class DungeonMakerCommandsContext
{
    internal const string DialogVariable = "Dialog_Trigger";
    internal const string RollVariable = "Roll_Trigger";
    internal const string TeleportNodeVariable = "Teleport_Node";
    internal const string TeleportPartyVariable = "Teleport_PartyNode";
    internal const string ListVariable = "Dialog_ListGadgets";
    internal const string TorchesVariable = "Torches_Lit";
    internal const string TorchesAutoVariable = "Torches_Auto";
    internal const string TorchVariablePrefix = "Torch_";
    internal const string StatusSuffix = "_Status";

    internal const string StatusPending = "Pending";
    internal const string StatusRunning = "Running";
    internal const string StatusSucceeded = "Succeeded";
    internal const string StatusFailed = "Failed";
    internal const string StatusCancelled = "Cancelled";

    // the game's functor names (FunctorManager's registrations)
    private const string TeleportFunctor = "Teleport";

    // what makes an interaction "a roll" or "a dialogue": the functors UserGadget.ApplyParameters fills for a
    // Dungeon Maker Roll Activator and for an NPC's or object's dialogue
    private static readonly string[] RollFunctors =
        ["SetGadgetConditionByAbilityCheck", "SetGadgetConditionBySavingThrow"];

    private static readonly string[] DialogFunctors = ["StartDialogSequence", "StartGameplayDialogSequence"];

    // a gadget is treated as a torch when its name contains this ...
    private const string TorchNameFilter = "torch";

    // ... and it has a condition with one of these names (first match wins). A torch's conditions are
    // [Initialized, Effect_Activated, Param_ManualExtinguish, Param_DefaultAsLit, RemoteActivated, WieldsFlamable]:
    // Param_DefaultAsLit is the creator's "Lit" checkbox (the starting state only), Effect_Activated is the live
    // state of the flame and light, so that is the one switched.
    private static readonly string[] LitConditionNames = ["Effect_Activated", "Lit", "IsLit", "Lighted"];

    // automatic torches (outdoor maps only): lit from this hour until that hour
    private const int AutoLitFromHour = 18;
    private const int AutoOutFromHour = 8;

    // How long a request may wait for something temporary, and how long a started interaction may stay
    // untouched by the game, in seconds of GAME time: the game clock is part of the simulation, a computer's own
    // clock or frame count is not.
    private const long MaxWaitSeconds = 3600;

    // A name that fits nothing may belong to something the same flow is about to create (an NPC that spawns):
    // look again every few gadget updates, and call the name wrong after a few tries and a few game seconds, or
    // after many tries when the clock stands still.
    private const int LookupInterval = 8;
    private const int MissingMinLookups = 3;
    private const int MissingMaxLookups = 40;
    private const long MissingGraceSeconds = 5;

    // torches whose game object is not built yet are tried again on the next gadget updates, this often at most
    private const int TorchRetries = 30;

    private static readonly Dictionary<string, Kind> Commands = new()
    {
        { DialogVariable, Kind.Dialog },
        { RollVariable, Kind.Roll },
        { TeleportNodeVariable, Kind.TeleportOne },
        { TeleportPartyVariable, Kind.TeleportParty }
    };

    // requests not yet handed to the game, one per command variable
    private static readonly Dictionary<string, Request> Waiting = [];

    // requests handed to the game and watched until they end
    private static readonly List<Request> Started = [];

    // Teleport_Node requests, oldest first: one per activation, none replaced by a later one. An entry can be a
    // place kept for a request that is still to come (Request.Expected).
    private static readonly List<Request> SoloTeleports = [];

    // a place kept in that queue for a flow that then never starts is given up after this many gadget updates
    private const int ExpectedPatienceTicks = 600;

    // the functor a gadget's Variable Operation is (FunctorManager's registration)
    private const string VariableOperationFunctor = "VariableOperation";

    // the serial of the newest request of each command variable: only that one may write the status variable
    private static readonly Dictionary<string, int> NewestSerial = [];

    // Per flow of an Area Activator, a pressure plate or an interaction, who set it off (see NoteOccupationChanged
    // and NoteInteracted): the activation whose flow has not started yet, and the one whose flow the game is
    // running (or ran last).
    private static ConditionalWeakTable<GameGadgetListener, Activation> _causes = new();
    private static ConditionalWeakTable<GameGadgetListener, Activation> _runs = new();

    // while the game's Teleport functor moves characters (see BeginGroupMove): who it moved, and the
    // activations those moves caused
    private static readonly HashSet<ulong> GroupMovers = [];
    private static readonly List<Activation> GroupActivations = [];
    private static int _groupDepth;

    private static readonly List<Torch> Torches = [];

    private static int _serial;

    // > 0 while this class writes a variable itself, so its own write is not taken as a request
    private static int _writing;

    // the party teleport whose Teleport functor is being stepped right now, if any (see NoteCharacterTeleported)
    private static Request _placing;

    // the Variable Operation functor that is running right now, if any (see BeginVariableOperation)
    private static FunctorParametersDescription _operation;

    // the Activate (or Enable) operation that is running right now, if any (see BeginGadgetActivation)
    private static FunctorParametersDescription _activating;

    private static IGameVariableService _subscribedTo;
    private static GameLocation _torchLocation;
    private static bool _listRequested;
    private static bool _torchesDirty;
    private static bool _torchControlSeen;
    private static int _torchRetriesLeft;
    private static string _lastTorchReport = string.Empty;
    private static string _lastTypeReport = string.Empty;


    private static GameLocation CurrentLocation => ServiceRepository.GetService<IGameLocationService>()?.GameLocation;

    #region Hooks

    // GameLocationManager.ReadyLocation: a location has finished loading (entered, or loaded from a save).
    internal static void OnLocationReady()
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        // the variable service belongs to the game being played: move the subscription when it is another one
        if (!ReferenceEquals(_subscribedTo, variables))
        {
            if (_subscribedTo != null)
            {
                _subscribedTo.VariableValueChanged -= OnVariableChanged;
            }

            _subscribedTo = variables;

            if (variables != null)
            {
                variables.VariableValueChanged += OnVariableChanged;
            }
        }

        // Nothing may be left over from another location or another save (the unload hook already dropped those;
        // this covers a location that went away without it). Requests made while this location was loading stay.
        var location = CurrentLocation;

        foreach (var stale in Waiting.Values.Where(x => !ReferenceEquals(x.Location, location)).ToArray())
        {
            Waiting.Remove(stale.Variable);
        }

        Started.RemoveAll(x => !ReferenceEquals(x.Location, location));
        SoloTeleports.RemoveAll(x => !ReferenceEquals(x.Location, location));
        Torches.Clear();
        _torchLocation = null;
        _lastTorchReport = string.Empty;

        if (variables == null)
        {
            return;
        }

        // A command variable can already hold a value here without an event having told us: a save made while it
        // was waiting, or a write made outside a location. Take it now. No gadget flow is behind it, so it has no
        // acting character.
        foreach (var command in Commands)
        {
            if (!Waiting.ContainsKey(command.Key) &&
                SoloTeleports.All(x => x.Expected || x.Variable != command.Key) &&
                variables.TryFindVariable(command.Key, out var variable) && variable is
                {
                    Type: GameVariableDefinitions.Type.String
                } && !string.IsNullOrEmpty(variable.StringValue))
            {
                Capture(command.Value, command.Key, variable.StringValue, null);
            }
        }

        // A dialogue, roll or teleport that was running when the game was saved can't be picked up again: its
        // variable was already cleared when it started, and nothing in the save says how far it got. Its status
        // would read Running for the rest of the campaign, so it is closed as Cancelled. (Leaving a location
        // closes them the same way, in DropRequests; this is the load.) A request of this location that is
        // running now is in Started.
        foreach (var command in Commands.Keys)
        {
            if (StringValueOf(variables, command + StatusSuffix) == StatusRunning &&
                Started.All(x => x.Variable != command))
            {
                WriteStatus(command, StatusCancelled);
                Main.Info($"DM command {command} cancelled: it was running when the game was saved.");
            }
        }

        _listRequested = IntValueOf(variables, ListVariable) != 0;
        _torchesDirty = true;
        _torchRetriesLeft = TorchRetries;
        DungeonMakerClockContext.OnLocationReady();
    }

    // GameLocationManager.UnloadLocationAsync: the location is going away, with its gadgets and characters.
    internal static void OnLocationUnloading()
    {
        DropRequests();
        Torches.Clear();
        _torchLocation = null;
        _operation = null;
        _activating = null;
        _causes = new ConditionalWeakTable<GameGadgetListener, Activation>();
        _runs = new ConditionalWeakTable<GameGadgetListener, Activation>();
        GroupMovers.Clear();
        GroupActivations.Clear();
        _groupDepth = 0;
        _listRequested = false;
        _torchesDirty = false;
        DungeonMakerClockContext.OnLocationUnloading();
    }

    // FunctorVariableOperation.Execute, around each step of it: the game is running a gadget's Variable Operation.
    // The variable's "value changed" event fires inside it, and the functor's parameters say which gadget flow is
    // writing, which is how a request learns its acting character. The returned token goes back to
    // EndVariableOperation.
    internal static (FunctorParametersDescription, int) BeginVariableOperation(FunctorParametersDescription parameters)
    {
        var token = (_operation, _serial);

        _operation = parameters;

        return token;
    }

    internal static void EndVariableOperation((FunctorParametersDescription, int) token)
    {
        var operation = _operation;
        var (previous, serialBefore) = token;

        _operation = previous;

        // The game raises "value changed" only when the value really changes. A second activation that writes the
        // same Node name into Teleport_Node while the first request has not started yet (the variable still holds
        // that name) would go unnoticed, and it is a request of its own. So when this operation wrote
        // Teleport_Node and no request was taken while it ran, it is taken here.
        if (operation?.StringParameter != TeleportNodeVariable || _serial != serialBefore ||
            CurrentLocation == null)
        {
            return;
        }

        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null || !variables.TryFindVariable(TeleportNodeVariable, out var variable) || variable is not
            {
                Type: GameVariableDefinitions.Type.String
            })
        {
            return;
        }

        if (!string.IsNullOrEmpty(variable.StringValue))
        {
            Capture(Kind.TeleportOne, TeleportNodeVariable, variable.StringValue, operation);

            return;
        }

        // The same goes for a cancel. Once the request at the head of the queue starts, it empties the variable
        // (ClearVariable), and writing "" over "" raises no event, so "writing an empty text cancels all that are
        // waiting" did nothing while requests stood behind it. An operation on Teleport_Node that leaves it
        // empty is that cancel. (Writing "" when the event did fire has already cancelled them; this finds none.)
        CancelQueuedTeleports();
    }

    // "" written to Teleport_Node: the requests made so far end as Cancelled. A place kept for one still to come
    // is not a request yet, and stays.
    private static void CancelQueuedTeleports()
    {
        foreach (var waiting in SoloTeleports.Where(x => !x.Expected).ToArray())
        {
            Finish(waiting, StatusCancelled, "the variable was cleared");
        }
    }

    // GameGadgetListenerVolumetricTrigger.OccupationChanged and GameGadgetListenerPressurePlate.OccupationChanged
    // run for every character move. The game passes the character that moved, and the flow becomes triggered when
    // that move switches the area's state. So when "triggered" goes from false to true inside this call, the
    // character passed in is the one who caused the activation, whoever else stands in the area. A move that
    // leaves the state as it was records nothing, so it can't replace the character who did activate it.
    internal static void NoteOccupationChanged(
        GameGadgetListener listener, GameLocationCharacter mover, bool triggeredBefore, bool triggeredAfter)
    {
        if (_groupDepth > 0 && mover != null)
        {
            GroupMovers.Add(mover.Guid);
        }

        if (triggeredAfter && !triggeredBefore)
        {
            var gadgetName = GadgetNameOf(listener);
            var activation = new Activation
            {
                MoverKnown = mover != null,
                SoloTeleportEligible = CanTriggerSoloTeleport(mover),
                MoverGuid = mover?.Guid ?? 0,
                MoverName = mover?.Name,
                // the party is put down together when a location is entered: no one character "walked in"
                Group = ServiceRepository.GetService<IGameLocationService>() is not { LocationIsReady: true },
                Origin = $"{DescribeTrigger(listener)} '{gadgetName}'" +
                         (mover != null ? $" ({mover.Name} moved into it)" : " (no character moved)"),
                Chain = gadgetName
            };

            if (_groupDepth > 0)
            {
                GroupActivations.Add(activation);
            }

            ForgetExpected(listener);
            _causes.Remove(listener);
            _causes.Add(listener, activation);
            ExpectSoloTeleport(listener, activation);
        }
        else if (!triggeredAfter)
        {
            // the trigger was withdrawn before its flow ran (the character left again)
            ForgetExpected(listener);
            _causes.Remove(listener);
        }
    }

    // An Area Activator or pressure plate was just set off, and its flow has a Variable Operation on Teleport_Node:
    // keep its place in the queue of one-character teleports now. The game runs gadget flows in the order of its
    // gadget list, and a flow can wait before it writes, so the writes do not arrive in the order the activations
    // happened; the places kept here do. The place is filled when the write comes (Capture), and given up if the
    // flow ends without writing or never starts (StepExpected).
    private static void ExpectSoloTeleport(GameGadgetListener listener, Activation activation)
    {
        activation.Expected = KeepPlace(listener);
    }

    // GameGadget.Interact, after it ran: a character used a gadget. If the game accepted it (the flow is
    // triggered), this is an activation like an area's or a plate's, and it is recorded the same way: who caused
    // it, whether that character may cause a one-character teleport, and, if the flow has a Variable Operation on
    // Teleport_Node, its place in the queue, so that a flow which waits before it writes is not overtaken by one
    // set off after it. NoteTriggerAcknowledged then carries the record over to the flow when the game starts it.
    //
    // The character has to be noted here, for the whole activation. The game writes the interacting character
    // into every functor's parameters at each accepted Interact, so when a second character uses the gadget
    // before the flow has started, the parameters name the second one from then on, for every write the flow
    // makes. The game runs the flow once for both presses; it is the first character's, so a second press while
    // the first is still waiting changes nothing here.
    internal static void NoteInteracted(GameGadget gadget, int listenerIndex, GameLocationCharacter actor)
    {
        var listeners = gadget?.activeListeners;

        if (listeners == null || listenerIndex < 0 || listenerIndex >= listeners.Count ||
            listeners[listenerIndex] is not GameGadgetListenerCharacterInteraction { IsTriggered: true } listener ||
            _causes.TryGetValue(listener, out _))
        {
            return;
        }

        var activation = new Activation
        {
            MoverKnown = actor != null,
            SoloTeleportEligible = CanTriggerSoloTeleport(actor),
            MoverGuid = actor?.Guid ?? 0,
            MoverName = actor?.Name,
            Origin = $"'{gadget.UniqueNameId}'" + (actor != null ? $" (used by {actor.Name})" : " (used)"),
            Chain = gadget.UniqueNameId
        };

        _causes.Add(listener, activation);
        ExpectSoloTeleport(listener, activation);
    }

    // FunctorUserGadgetOperation.Execute, around each step of it: a gadget's flow is running a Dungeon Maker
    // "Activate" (or "Enable") operation on other gadgets. The game sets each target's RemoteActivated (or
    // RemoteEnabled) condition inside it, passing on this flow's ActingCharacters
    // (GameGadget.SetCondition(index, state, functorParameters.ActingCharacters)). The returned token goes back to
    // EndGadgetActivation.
    internal static FunctorParametersDescription BeginGadgetActivation(FunctorParametersDescription parameters)
    {
        var token = _activating;

        _activating = parameters;

        return token;
    }

    internal static void EndGadgetActivation(FunctorParametersDescription token)
    {
        _activating = token;
    }

    // GameGadgetListenerConditionToggle.ConditionStatusChanged: a gadget's condition changed, and this flow of it
    // may have been switched on by it. That is how a Dungeon Maker gadget is "activated" by another one: the
    // activating flow's Activate operation sets the RemoteActivated condition, and the target's flow waiting on that
    // condition is triggered. The game copies the activating flow's ActingCharacters to the target's flow, but for
    // an Area Activator those are everyone standing in it, not the one who walked in, so the activation recorded for
    // the activating flow (who walked in, or who used the gadget) is carried over instead, and the chain grows by
    // one gadget. A chain can go on for any number of gadgets this way.
    //
    // When the activating flow has no recorded activation (it was started by a timer, a variable, a quest, or a
    // trigger kept in a save), the target gets a record that says so (Untraced), so a command its flow writes can
    // say what started the chain. A change made by anything else than an Activate operation (a dialogue, a torch,
    // a condition functor) carries nothing: ResolveActor then reports it as an untraced activation.
    internal static void NoteConditionToggled(GameGadgetListener listener, bool triggeredBefore, bool triggeredAfter)
    {
        if (triggeredBefore || !triggeredAfter || CurrentLocation == null)
        {
            return;
        }

        var targetName = GadgetNameOf(listener);
        var source = ListenerOf(_activating);
        Activation carried;

        if (_activating == null)
        {
            // Switched on by something that is not an Activate operation. Nothing is recorded; if this flow then
            // writes a command, ResolveActor reports it as untraced (logging every such change here would log
            // every torch the game lights).
            ForgetExpected(listener);
            _causes.Remove(listener);

            return;
        }

        if (source != null && _runs.TryGetValue(source, out var from))
        {
            carried = new Activation
            {
                MoverKnown = from.MoverKnown,
                SoloTeleportEligible = from.SoloTeleportEligible,
                MoverGuid = from.MoverGuid,
                MoverName = from.MoverName,
                Group = from.Group,
                Untraced = from.Untraced,
                Origin = from.Origin,
                OriginTerm = from.OriginTerm,
                Chain = $"{from.Chain} > {targetName}"
            };
        }
        else
        {
            var sourceName = source != null ? GadgetNameOf(source) : "an unknown gadget";

            carried = new Activation
            {
                Untraced = true,
                Origin = source != null ? $"{DescribeTrigger(source)} '{sourceName}'" : sourceName,
                OriginTerm = TriggerTerm(source),
                Chain = $"{sourceName} > {targetName}"
            };
        }

        ForgetExpected(listener);
        _causes.Remove(listener);
        _causes.Add(listener, carried);
        ExpectSoloTeleport(listener, carried);
    }

    // the unique name of the gadget a flow belongs to, for the log
    private static string GadgetNameOf(GameGadgetListener listener)
    {
        return listener?.SourceGadget ? listener.SourceGadget.GameGadget?.UniqueNameId ?? listener.SourceGadget.name
            : "?";
    }

    // What kind of trigger a gadget flow has, in words, for the log; and the translation term naming it to the
    // player, for the alert when a command can't tell who acts (Message/&DungeonMakerTrigger<Kind>).
    private static string DescribeTrigger(GameGadgetListener listener)
    {
        return listener switch
        {
            GameGadgetListenerVolumetricTrigger => "the Area Activator",
            GameGadgetListenerPressurePlate => "the pressure plate",
            GameGadgetListenerCharacterInteraction => "the interaction with",
            GameGadgetListenerConditionToggle => "the activation of",
            GameGadgetListenerElapsedTime => "the \"after a time\" timer of",
            GameGadgetListenerHourUpdate => "the \"every N hours\" timer of",
            GameGadgetListenerVariableChanged => "the Variable Activator",
            GameGadgetListenerQuestUpdated => "the quest step listened to by",
            GameGadgetListenerLocationReady or GameGadgetListenerLocationEntered
                or GameGadgetListenerLocationFinalized => "the location loading, through",
            GameGadgetListenerBattleEvent => "a battle event, through",
            GameGadgetListenerConditionChanged => "a character condition change, through",
            null => "an unknown trigger of",
            _ => $"a {listener.GetType().Name.Replace("GameGadgetListener", string.Empty)} trigger of"
        };
    }

    private static string TriggerTerm(GameGadgetListener listener)
    {
        return listener switch
        {
            GameGadgetListenerElapsedTime or GameGadgetListenerHourUpdate => "Message/&DungeonMakerTriggerTimer",
            GameGadgetListenerVariableChanged => "Message/&DungeonMakerTriggerVariable",
            GameGadgetListenerQuestUpdated => "Message/&DungeonMakerTriggerQuest",
            GameGadgetListenerLocationReady or GameGadgetListenerLocationEntered
                or GameGadgetListenerLocationFinalized => "Message/&DungeonMakerTriggerLocation",
            _ => "Message/&DungeonMakerTriggerOther"
        };
    }

    // Adds a place to the queue of one-character teleports for this flow, if the flow has a Variable Operation on
    // Teleport_Node; returns it, or null when the flow has none.
    private static Request KeepPlace(GameGadgetListener listener)
    {
        if (CurrentLocation == null || listener.FunctorParams == null ||
            !listener.FunctorParams.Any(x =>
                x is FunctorParametersDescription { Type: VariableOperationFunctor } operation &&
                operation.StringParameter == TeleportNodeVariable))
        {
            return null;
        }

        var kept = new Request
        {
            Kind = Kind.TeleportOne,
            Variable = TeleportNodeVariable,
            Expected = true,
            Listener = listener,
            Location = CurrentLocation,
            IssuedAt = GameSeconds()
        };

        SoloTeleports.Add(kept);

        return kept;
    }

    // the place kept for this listener's activation that has not started yet, if any, is given up
    private static void ForgetExpected(GameGadgetListener listener)
    {
        if (_causes.TryGetValue(listener, out var activation) && activation.Expected is { Expected: true } kept)
        {
            SoloTeleports.Remove(kept);
            activation.Expected = null;
        }
    }

    // AcknowledgeTrigger of those listeners: the game is about to run the flow for the trigger it noted. From
    // here until the next acknowledge, the flow's functors act for the character that caused this trigger. A flow
    // started without a move behind it (a trigger kept in a save) gets no character, rather than inheriting the
    // one of an older activation.
    internal static void NoteTriggerAcknowledged(GameGadgetListener listener)
    {
        _runs.Remove(listener);

        if (!_causes.TryGetValue(listener, out var activation))
        {
            return;
        }

        _causes.Remove(listener);
        _runs.Add(listener, activation);
    }

    // FunctorTeleport.Execute, around each step of it: the game's Teleport functor is moving characters. If it
    // moves more than one, the areas and plates they land in were activated by a group arriving together.
    internal static void BeginGroupMove()
    {
        _groupDepth++;
    }

    // GameLocationPositioningManager.TeleportCharacter, after it ran: the game has put a character on a cell.
    // While a party teleport's functor is being stepped, this is the functor placing one of the travellers: it
    // works out a free cell near the Node for each of them and calls TeleportCharacter with it, and makes no call
    // for a character it finds no cell for (it only writes a log line). So the calls seen here are exactly the
    // placements the game made, with the cells it chose.
    internal static void NoteCharacterTeleported(GameLocationCharacter character, TA.int3 position)
    {
        if (_placing != null && character != null)
        {
            _placing.Placed[character.Guid] = position;
        }
    }

    internal static void EndGroupMove()
    {
        if (--_groupDepth > 0)
        {
            return;
        }

        _groupDepth = 0;

        if (GroupMovers.Count > 1)
        {
            foreach (var activation in GroupActivations)
            {
                activation.Group = true;
            }
        }

        GroupMovers.Clear();
        GroupActivations.Clear();
    }

    // GameLocation.UpdateGadgets, after the game updated its gadgets: work on the requests.
    internal static void Tick()
    {
        if (ServiceRepository.GetService<IGameLocationService>() is not { LocationIsReady: true })
        {
            return;
        }

        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null)
        {
            return;
        }

        if (_listRequested)
        {
            _listRequested = false;
            Guarded("gadget list", () => ListGadgets(variables));
        }

        if (Waiting.Count > 0)
        {
            foreach (var request in Waiting.Values.ToArray())
            {
                GuardedStep(request, StepWaiting);
            }
        }

        for (var i = Started.Count - 1; i >= 0; i--)
        {
            GuardedStep(Started[i], StepStarted);
        }

        // One-character teleports: the oldest request only, so each one ends before the next character is placed.
        // Places kept for requests that turn out not to come are dropped on the way to it.
        while (SoloTeleports.Count > 0 && SoloTeleports[0].Expected && !StepExpected(SoloTeleports[0]))
        {
            SoloTeleports.RemoveAt(0);
        }

        if (SoloTeleports.Count > 0 && !SoloTeleports[0].Expected)
        {
            GuardedStep(SoloTeleports[0], StepSoloTeleport);
        }

        if (_torchesDirty)
        {
            _torchesDirty = false;
            Guarded("torches", () => ApplyTorches(variables));
        }

        Guarded("clock skip", DungeonMakerClockContext.Tick);
    }

    // the in-game hour changed (from the clock's update): automatic torches may have to switch
    internal static void NoteHourChanged()
    {
        if (_torchControlSeen)
        {
            _torchesDirty = true;
        }
    }

    // the game's "variable value changed" event: any variable, written by anything
    private static void OnVariableChanged(GameVariable variable)
    {
        if (_writing > 0 || variable?.Name == null || CurrentLocation == null)
        {
            return;
        }

        var name = variable.Name;

        if (Commands.TryGetValue(name, out var kind))
        {
            if (variable.Type != GameVariableDefinitions.Type.String)
            {
                ReportOnce(ref _lastTypeReport, $"DM command '{name}' ignored: the variable must be a String.");

                return;
            }

            Capture(kind, name, variable.StringValue, _operation);
        }
        else if (name == ListVariable)
        {
            _listRequested = true;
        }
        else if (name == TorchesVariable || name == TorchesAutoVariable ||
                 name.StartsWith(TorchVariablePrefix, StringComparison.Ordinal))
        {
            _torchControlSeen = true;
            _torchesDirty = true;
        }
        else
        {
            DungeonMakerClockContext.OnVariableChanged(name);
        }
    }

    #endregion

    #region Requests

    // Takes a write of a command variable: "" cancels what was waiting, anything else replaces it. For
    // Teleport_Node nothing is replaced: the request joins the queue.
    private static void Capture(Kind kind, string variableName, string value, FunctorParametersDescription operation)
    {
        var queued = kind == Kind.TeleportOne;
        var replaced = !queued && Waiting.Remove(variableName);

        if (string.IsNullOrEmpty(value))
        {
            if (queued)
            {
                CancelQueuedTeleports();
            }
            else if (replaced)
            {
                WriteStatus(variableName, StatusCancelled);
                Main.Info($"DM command {variableName}: cancelled.");
            }

            return;
        }

        // a one-character teleport whose activation kept a place in the queue takes that place
        var kept = queued ? TakeExpected(operation) : null;
        var request = kept ?? new Request { Kind = kind, Variable = variableName, Location = CurrentLocation };

        request.Expected = false;
        request.Ticks = 0;
        request.Target = value;
        request.Serial = ++_serial;
        request.IssuedAt = GameSeconds();

        ResolveActor(operation, request);

        if (kind is Kind.Roll or Kind.TeleportOne or Kind.Dialog)
        {
            LogResolution(operation, request);
        }

        NewestSerial[variableName] = request.Serial;

        if (kept != null)
        {
            // already in the queue, where its activation put it
        }
        else if (queued)
        {
            SoloTeleports.Add(request);
        }
        else
        {
            Waiting[variableName] = request;
        }

        WriteStatus(variableName, StatusPending);
    }

    // the listener (the flow) of the gadget whose Variable Operation this is
    private static GameGadgetListener ListenerOf(FunctorParametersDescription operation)
    {
        var listeners = operation?.SourceGadget ? operation.SourceGadget.GameGadget?.activeListeners : null;
        var index = operation?.ListenerIndex ?? -1;

        return listeners != null && index >= 0 && index < listeners.Count ? listeners[index] : null;
    }

    // The place kept in the queue for the flow that is writing now, if its activation kept one and it has not
    // been used. A second write by the same flow finds none and joins the end of the queue.
    private static Request TakeExpected(FunctorParametersDescription operation)
    {
        var listener = ListenerOf(operation);

        if (listener == null || !_runs.TryGetValue(listener, out var activation) ||
            activation.Expected is not { Expected: true } kept || !SoloTeleports.Contains(kept))
        {
            return null;
        }

        activation.Expected = null;

        return kept;
    }

    // A place kept at the head of the queue: is its request still to come? Yes while the flow is running (the
    // gadget's coroutine is on this flow), and while it is triggered and the game will still start it. A flow that
    // has ended without writing, or that the game won't start, gives its place up, so the requests behind it are
    // not held back.
    //
    // The game starts a triggered flow at a gadget update (GameGadget.Update) only when the gadget's scope is valid
    // (CheckIfScopeIsValid), no other flow of the gadget is running (activeFunctorSequence), and the flow's
    // conditions match (IsMatchingConditions; OccupationChanged sets the trigger even when they don't). The first
    // two are temporary: the gadget may be running another flow of its own (a dialogue, a Wait) or be out of scope
    // for a while, and its flow then starts later and writes in its turn. Those updates are not counted against
    // the place's patience, which only measures updates in which nothing kept the flow from starting; the game
    // clock alone bounds the wait. Conditions that don't match keep it from starting until something changes
    // them, so the place is given up at once rather than holding every teleport behind it.
    private static bool StepExpected(Request kept)
    {
        var listener = kept.Listener;
        var gadget = listener?.SourceGadget ? listener.SourceGadget.GameGadget : null;

        if (gadget == null || !ReferenceEquals(kept.Location, CurrentLocation))
        {
            return false;
        }

        var withinTime = GameSeconds() - kept.IssuedAt <= MaxWaitSeconds;

        if (gadget.activeFunctorSequence != null && gadget.currentListenerIndex == listener.Index)
        {
            // running: it may be waiting on purpose before it writes
            return withinTime;
        }

        if (!listener.IsTriggered)
        {
            // ended without writing, or its trigger was taken back
            return false;
        }

        if (gadget.activeFunctorSequence != null || !gadget.HasValidScope)
        {
            // another flow of this gadget runs first, or the gadget isn't updated now: still to come
            return withinTime;
        }

        // nothing keeps it from starting at the next update but its conditions
        return listener.IsMatchingConditions() && ++kept.Ticks <= ExpectedPatienceTicks;
    }

    // Works out which character a write acts for, from the gadget flow that made it. It never picks among
    // several: a request that can't name exactly one character keeps the reason instead.
    private static void ResolveActor(FunctorParametersDescription operation, Request request)
    {
        if (operation == null)
        {
            request.ActorProblem = "no object's Variable Operation set the variable (a quest, a dialogue, a cheat, " +
                                   "or a save loaded while it was waiting), so no character is tied to it";
            request.ProblemTerm = "Message/&DungeonMakerTriggerNoGadget";

            return;
        }

        var gadget = operation.SourceGadget ? operation.SourceGadget.GameGadget : null;
        var listeners = gadget?.activeListeners;
        var index = operation.ListenerIndex;
        var listener = listeners != null && index >= 0 && index < listeners.Count ? listeners[index] : null;
        var gadgetName = gadget?.UniqueNameId;

        // The characters the game handed the flow (it reuses these lists, so they only mean something for the
        // listener types below, which fill them for each activation). A roll or a dialogue is for a party member;
        // a solo teleport needs the original actor identified before checking eligibility.
        var anyCharacter = request.Kind == Kind.TeleportOne;
        var heroes = (operation.ActingCharacters ?? [])
            .Where(x => x != null && (anyCharacter || IsPartyMember(x)))
            .Select(x => x.Guid)
            .Distinct()
            .ToList();
        // Keep all occupants when checking ambiguity. Filtering first could select a bystander.
        var singleActorEligible = !anyCharacter || (heroes.Count == 1 &&
            operation.ActingCharacters.Any(x => x != null && x.Guid == heroes[0] && CanTriggerSoloTeleport(x)));
        var who = anyCharacter ? "character" : "party member";

        switch (listener)
        {
            // Something a character used, and the use was seen (NoteInteracted): the character noted then, for
            // every write this flow makes. The functor's own list may name a later user of the same gadget.
            case GameGadgetListenerCharacterInteraction
                when _runs.TryGetValue(listener, out var used) && used.MoverKnown:
                if (anyCharacter && !used.SoloTeleportEligible)
                {
                    request.ActorProblem = "the triggering character was outside the party and guest rosters";
                }
                else
                {
                    // for a roll or a dialogue, being a conscious party member is checked when the command runs
                    request.ActorGuid = used.MoverGuid;
                    request.ActorKnown = true;
                    request.ActorName = used.MoverName;
                }

                break;

            // an interaction that was not seen being made: the game hands the flow exactly the interacting
            // character (GameGadget.Interact)
            case GameGadgetListenerCharacterInteraction when heroes.Count == 1 && singleActorEligible:
                request.ActorGuid = heroes[0];
                request.ActorKnown = true;
                break;
            case GameGadgetListenerCharacterInteraction:
                request.ActorProblem = $"the interaction with '{gadgetName}' has no single {who} acting";
                break;
            case GameGadgetListenerVolumetricTrigger or GameGadgetListenerPressurePlate:
                if (!_runs.TryGetValue(listener, out var activation))
                {
                    request.ActorProblem = $"the activation of '{gadgetName}' was not seen happening (it was " +
                                           "kept in a save, or its conditions changed without anyone moving)";
                }
                else if (activation.Group)
                {
                    // arriving in the location or a party teleport: fine if only one of them is in it
                    if (heroes.Count == 1 && singleActorEligible)
                    {
                        request.ActorGuid = heroes[0];
                        request.ActorKnown = true;
                    }
                    else
                    {
                        request.ActorProblem = $"'{gadgetName}' was activated by characters being placed " +
                                               $"together ({heroes.Count} {who}s in it), so no single one caused it";
                    }
                }
                else if (anyCharacter && activation.MoverKnown && !activation.SoloTeleportEligible)
                {
                    request.ActorProblem = "the triggering character was outside the party and guest rosters";
                }
                else if (activation.MoverKnown)
                {
                    // whether it is a conscious party member is checked when the command runs
                    request.ActorGuid = activation.MoverGuid;
                    request.ActorKnown = true;
                    request.ActorName = activation.MoverName;
                }
                else
                {
                    request.ActorProblem = $"'{gadgetName}' was activated without a character moving";
                }

                break;

            // A gadget activated by another gadget's Activate operation (an Area Activator whose flow activates a
            // Variable Operation gadget, and so on). NoteConditionToggled carried the first gadget's activation
            // down the chain, so the flow acts for whoever set the first one off.
            case GameGadgetListenerConditionToggle:
                _runs.TryGetValue(listener, out var chained);
                request.Chain = chained?.Chain;
                request.Origin = chained?.Origin;

                if (chained is { Untraced: false, Group: false, MoverKnown: true })
                {
                    if (anyCharacter && !chained.SoloTeleportEligible)
                    {
                        request.ActorProblem = "the triggering character was outside the party and guest rosters";
                    }
                    else
                    {
                        // whether it is a conscious party member is checked when the command runs
                        request.ActorGuid = chained.MoverGuid;
                        request.ActorKnown = true;
                        request.ActorName = chained.MoverName;
                    }
                }
                else if (heroes.Count == 1 && singleActorEligible)
                {
                    // The start of the chain wasn't traced to one character (the party arriving together, a flow
                    // kept in a save, a change the mod doesn't follow), but the game passed exactly one eligible
                    // character along the chain: that one acts, by the same rule as an area the party arrives in.
                    // A timer, a variable or a quest passes nobody, so a chain they start never gets here.
                    request.ActorGuid = heroes[0];
                    request.ActorKnown = true;
                    request.ActorNote = "the start of the chain was not traced to one character; the one eligible " +
                                        "character the game passed along the chain acts";
                }
                else if (chained == null)
                {
                    request.ActorProblem = $"'{gadgetName}' was switched on by a condition change the mod does not " +
                                           "trace (not another gadget's Activate operation), so no character is " +
                                           "tied to it";
                    request.ProblemTerm = "Message/&DungeonMakerTriggerOther";
                }
                else if (chained.Untraced)
                {
                    request.ActorProblem = $"the chain {chained.Chain} was started by {chained.Origin}, which " +
                                           $"passes no character ({heroes.Count} {who}s passed along)";
                    request.ProblemTerm = chained.OriginTerm;
                }
                else
                {
                    request.ActorProblem = $"the chain {chained.Chain} was started by characters being placed " +
                                           $"together ({heroes.Count} {who}s in it), so no single one caused it";
                }

                break;
            default:
                request.ActorProblem = listener == null
                    ? "the object that set the variable could not be identified"
                    : $"'{gadgetName}' set it from {DescribeTrigger(listener)} '{gadgetName}', which has no " +
                      "acting character";
                request.ProblemTerm = TriggerTerm(listener);
                break;
        }
    }

    // One detailed line per write of a command that acts for a character: what wrote it, how the flow was set
    // off, what the game passed along, and who acts or why nobody does. This is the record to read when a
    // command didn't do what the campaign meant.
    private static void LogResolution(FunctorParametersDescription operation, Request request)
    {
        var gadget = operation?.SourceGadget ? operation.SourceGadget.GameGadget : null;
        var listener = ListenerOf(operation);
        var passed = operation?.ActingCharacters?.Where(x => x != null).Select(x => x.Name).ToList() ?? [];
        if (request.Chain == null && listener != null && _runs.TryGetValue(listener, out var run))
        {
            request.Chain = run.Chain;
            request.Origin = run.Origin;
        }

        var outcome = request.ActorKnown
            ? $"acts for {request.ActorName ?? FindCharacter(request.ActorGuid)?.Name ?? "?"}" +
              (request.ActorNote != null ? $" ({request.ActorNote})" : string.Empty)
            : $"no acting character: {request.ActorProblem}";

        Main.Info($"DM command {request.Variable} = '{request.Target}': written by " +
                  (gadget == null
                      ? "no gadget's Variable Operation"
                      : $"'{gadget.UniqueNameId}' (flow {operation.ListenerIndex}, {DescribeTrigger(listener)})") +
                  (request.Chain != null ? $"; chain {request.Chain}" : string.Empty) +
                  (request.Origin != null ? $"; started by {request.Origin}" : string.Empty) +
                  $"; characters passed: [{string.Join(", ", passed)}]; {outcome}.");
    }

    private static bool CanTriggerSoloTeleport(GameLocationCharacter character)
    {
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();

        return character != null && (characters?.PartyCharacters?.Contains(character) == true ||
                                     characters?.GuestCharacters?.Contains(character) == true);
    }

    private static bool IsPartyMember(GameLocationCharacter character)
    {
        return character != null &&
               ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters?.Contains(character) ==
               true;
    }

    // the acting character, only if it is still a conscious member of the party in this location
    private static GameLocationCharacter ValidActor(Request request, out string problem)
    {
        problem = request.ActorProblem;

        if (!request.ActorKnown)
        {
            return null;
        }

        var party = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters;
        var actor = party?.FirstOrDefault(x => x != null && x.Guid == request.ActorGuid);

        if (actor == null)
        {
            problem = request.ActorName == null
                ? "the character that caused it is not a party member in this location"
                : $"{request.ActorName}, who caused it, is not a party member in this location";

            return null;
        }

        if (actor.RulesetCharacter is not { IsDeadOrDyingOrUnconscious: false })
        {
            problem = $"{actor.Name} is unconscious or dead";

            return null;
        }

        return actor;
    }

    private static GameLocationCharacter FirstConsciousHero()
    {
        return ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters?
            .FirstOrDefault(x => x?.RulesetCharacter is RulesetCharacterHero { IsDeadOrDyingOrUnconscious: false });
    }

    private static void StepWaiting(Request request)
    {
        // still the request of its variable? An earlier step of this update may have led to it being replaced.
        if (!Waiting.TryGetValue(request.Variable, out var current) || !ReferenceEquals(current, request))
        {
            return;
        }

        // a request belongs to the location it was made in
        if (!ReferenceEquals(request.Location, CurrentLocation))
        {
            Finish(request, StatusCancelled, "the location changed");

            return;
        }

        // temporary: a dialogue or cutscene is playing or about to
        var narrative = ServiceRepository.GetService<INarrativeDirectionService>();

        if (narrative != null && (narrative.IsSequenceInProgress || narrative.HasPendingSequence))
        {
            Wait(request, "a dialogue is in progress");

            return;
        }

        switch (request.Kind)
        {
            case Kind.Dialog:
            case Kind.Roll:
                StepInteraction(request);
                break;
            default:
                StepPartyTeleport(request);
                break;
        }
    }

    #endregion

    #region Dialogue and roll

    private static void StepInteraction(Request request)
    {
        var isRoll = request.Kind == Kind.Roll;
        var what = isRoll ? "skill check" : "dialogue";
        var actor = ValidActor(request, out var problem);

        if (actor == null)
        {
            if (isRoll)
            {
                Finish(request, StatusFailed, $"a roll needs one particular character, but {problem}");

                return;
            }

            // a dialogue belongs to the party, so anyone conscious can open it
            actor = FirstConsciousHero();

            if (actor == null)
            {
                Wait(request, "no conscious party member");

                return;
            }
        }

        var gadget = request.Gadget;

        if (gadget == null)
        {
            if (!LookupDue(request))
            {
                return;
            }

            var matches = FindInteractionTargets(request.Target);

            switch (matches.Count)
            {
                case 0:
                    WaitMissing(request,
                        $"no NPC or object named '{request.Target}' in this location (set {ListVariable} to 1 to list them)");

                    return;
                case > 1:
                    Finish(request, StatusFailed,
                        $"'{request.Target}' fits {matches.Count} gadgets ({string.Join(", ", matches.Select(x => x.UniqueNameId))}); use a gadget's own name");

                    return;
            }

            gadget = request.Gadget = matches[0];
        }

        var wanted = isRoll ? RollFunctors : DialogFunctors;
        var flows = new List<int>();

        for (var i = 0; i < gadget.activeListeners.Count; i++)
        {
            if (gadget.activeListeners[i] is GameGadgetListenerCharacterInteraction interaction &&
                interaction.FunctorParams.Any(x => wanted.Contains(x.Type)))
            {
                flows.Add(i);
            }
        }

        if (flows.Count == 0)
        {
            Finish(request, StatusFailed,
                $"gadget '{gadget.UniqueNameId}' has no interaction that starts a {what}");

            return;
        }

        // the gadget is in the middle of another flow: the game would not start a new one until it ends, and
        // Interact would overwrite the acting character of the flow that is running
        if (gadget.activeFunctorSequence != null)
        {
            Wait(request, $"gadget '{gadget.UniqueNameId}' is busy");

            return;
        }

        var ready = flows.Where(x => gadget.WouldInteract(x, actor)).ToList();

        switch (ready.Count)
        {
            case 0:
                Wait(request, $"the {what} of '{gadget.UniqueNameId}' is not available to {actor.Name} yet");

                return;
            case > 1:
                Finish(request, StatusFailed,
                    $"gadget '{gadget.UniqueNameId}' has {ready.Count} {what} interactions available at once (flows {string.Join(", ", ready)}), so which one is meant can't be told");

                return;
        }

        // hand it to the game: clear the variable first so the same value can be written again later
        request.Flow = ready[0];
        MarkStarted(request);
        Main.Info(
            $"DM command {request.Variable}: starting the {what} of '{gadget.UniqueNameId}' (flow {request.Flow}) for {actor.Name}.");

        // Interact marks the flow as triggered and the gadget as dirty; the game's gadget update runs it
        gadget.Interact(request.Flow, actor);

        if (!gadget.activeListeners[request.Flow].IsTriggered)
        {
            Finish(request, StatusFailed, $"the game refused the interaction with '{gadget.UniqueNameId}'");
        }
    }

    // Gadgets the author's name can mean: a gadget's own name or its NPC's internal name first; if neither fits,
    // the name or title the author gave the NPC. More than one result means the name is ambiguous.
    private static List<GameGadget> FindInteractionTargets(string name)
    {
        var gadgets = GadgetsById();
        var byId = gadgets.Values
            .Where(x => Same(x.UniqueNameId, name) || Same(x.BoundCharacterUniqueName, name))
            .ToList();

        if (byId.Count > 0)
        {
            return byId;
        }

        return gadgets
            .Where(x => x.Value.activeListeners.Any(l => l is GameGadgetListenerCharacterInteraction) &&
                        NpcNames.Of(x.Key)?.Matches(name) == true)
            .Select(x => x.Value)
            .ToList();
    }

    #endregion

    #region Teleport

    // Looks the destination up the way a Teleporter gadget finds it (by the gadget object's name), or by the
    // gadget's unique name. Returns null when the request has to wait or has been ended.
    private static WorldGadget FindDestination(Request request)
    {
        if (!LookupDue(request))
        {
            return null;
        }

        var nodes = WorldGadgets()
            .Where(x => Same(x.name, request.Target) || Same(x.GameGadget?.UniqueNameId, request.Target))
            .ToList();

        switch (nodes.Count)
        {
            case 1:
                return nodes[0];
            case 0:
                WaitMissing(request,
                    $"no gadget named '{request.Target}' in this location (set {ListVariable} to 2 to list them)");

                return null;
            default:
                Finish(request, StatusFailed,
                    $"{nodes.Count} gadgets are named '{request.Target}', so the destination can't be told");

                return null;
        }
    }

    // A character of this location by its id, in whatever state: the party, guests, everyone else alive, the dead.
    private static GameLocationCharacter FindCharacter(ulong guid)
    {
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characters == null)
        {
            return null;
        }

        return new[]
            {
                characters.PartyCharacters, characters.GuestCharacters, characters.ValidCharacters,
                characters.DeadCharacters
            }
            .Where(x => x != null)
            .SelectMany(x => x)
            .FirstOrDefault(x => x != null && x.Guid == guid);
    }

    // Teleport_Node: the head of the queue. See "ONE-CHARACTER TELEPORT" at the top of this file.
    private static void StepSoloTeleport(Request request)
    {
        if (!ReferenceEquals(request.Location, CurrentLocation))
        {
            Finish(request, StatusCancelled, "the location changed");

            return;
        }

        // temporary: a dialogue or cutscene is playing or about to
        var narrative = ServiceRepository.GetService<INarrativeDirectionService>();

        if (narrative != null && (narrative.IsSequenceInProgress || narrative.HasPendingSequence))
        {
            Wait(request, "a dialogue is in progress");

            return;
        }

        if (!request.ActorKnown)
        {
            Finish(request, StatusFailed,
                $"teleporting one character needs to know which, but {request.ActorProblem} (use {TeleportPartyVariable} for the whole party)");

            return;
        }

        // the character that caused it, whatever its state: it only has to be here still
        var character = FindCharacter(request.ActorGuid);

        if (character == null)
        {
            Finish(request, StatusFailed,
                $"{request.ActorName ?? "the character that caused it"} has left the location or been removed");

            return;
        }

        var positioning = ServiceRepository.GetService<IGameLocationPositioningService>();

        if (positioning == null)
        {
            Finish(request, StatusFailed, "the game's positioning service is not available");

            return;
        }

        var node = FindDestination(request);

        if (node == null)
        {
            return;
        }

        var marker = positioning.GetGridPositionFromWorldPosition(node.transform);
        var orientation = positioning.GetGridOrientationFromWorldOrientation(node.transform);
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (characters?.PartyFormation == null ||
            !positioning.TryComputeFirstFormationPlacementPositions(
                character, marker, orientation, characters.PartyFormation.FormationPositions,
                CellHelpers.PlacementMode.Station, out var destination, 5, true, false))
        {
            Finish(request, StatusFailed, $"no placement found near '{node.name}' for {character.Name}");

            return;
        }

        // from here the request is no longer waiting: its variable is cleared so the same name can be written again
        SoloTeleports.Remove(request);
        ClearVariable(request);
        WriteStatus(request, StatusRunning);
        Main.Info(
            $"DM command {request.Variable}: teleporting {character.Name} to '{node.name}' at {destination}.");

        // Use the native Teleport functor's placement search and facing before moving this actor.
        ServiceRepository.GetService<IGameLocationActionService>()?
            .StopCharacterActions(character, CharacterAction.InterruptionType.Kill, true);
        positioning.TeleportCharacter(character, destination, orientation);

        // arrival is checked once, here, before anything that follows from standing on that cell
        var arrived = character.LocationPosition;

        if (!arrived.Equals(destination))
        {
            Finish(request, StatusFailed, $"{character.Name} was placed at {arrived}, not at {destination}");

            return;
        }

        Finish(request, StatusSucceeded, null);

        // now the cell's consequences: with no floor under it the character falls, as after any move
        character.CheckCharacterFooting(false);
    }

    // Teleport_PartyNode, as specified in issue #42: the party and the guests travelling with it, placed in
    // formation around the Node by the game's Teleport functor. It needs no acting character.
    private static void StepPartyTeleport(Request request)
    {
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
        var party = (characters?.PartyCharacters ?? [])
            .Concat(characters?.GuestCharacters ?? [])
            .Where(x => x != null)
            .Distinct()
            .ToList();

        if (party.Count == 0)
        {
            Wait(request, "no party in this location yet");

            return;
        }

        var node = FindDestination(request);

        if (node == null)
        {
            return;
        }

        // the parameters a Teleporter gadget gives the game's Teleport functor
        var parameters = new FunctorParametersDescription
        {
            Type = TeleportFunctor,
            // the game's own look-up for "the party and its guests"; the functor moves summoned creatures to
            // their owners itself
            CharacterLookUpMethod = FunctorDefinitions.CharacterLookUpMethod.AllPartyMembersWithGuests,
            ActingCharacters = [..party],
            SourceGadget = node,
            playerPlacementMarkers = [node.transform]
        };

        var functors = ServiceRepository.GetService<IFunctorService>();

        if (functors == null)
        {
            Finish(request, StatusFailed, "the game's functor service is not available");

            return;
        }

        // Everyone meant to travel is remembered, to check afterwards that the game placed each of them. That
        // includes summoned creatures: the functor takes them out of its own list and has them moved, as a group,
        // to the first character it placed (TeleportSummonsToCharacter), which uses the same TeleportCharacter.
        request.Members = party.Select(x => x.Guid).ToList();

        MarkStarted(request);
        Main.Info(
            $"DM command {request.Variable}: teleporting the party ({party.Count} with guests) to '{node.name}'.");

        // The request itself is the "source" the functor service tracks while the functor runs. The coroutine gets
        // its first step later in this same gadget update, when Tick goes over the started requests.
        request.Routine = TA.Coroutine.StartCoroutine(functors.ExecuteFunctorAsync(TeleportFunctor, parameters,
            request));
    }

    #endregion

    #region Started requests

    private static void StepStarted(Request request)
    {
        if (request.Routine != null)
        {
            // one step per gadget update, as the game does for a gadget's functors; the placements the functor
            // makes during the step are noted for this request
            _placing = request;

            try
            {
                request.Routine.Run();
            }
            finally
            {
                _placing = null;
            }

            if (request.Routine.LastException != null)
            {
                var exception = request.Routine.LastException;

                ServiceRepository.GetService<IFunctorService>()?.EmergencyReleaseSource(request);
                Main.Error(exception);
                Finish(request, StatusFailed, $"the teleport threw {exception.GetType().Name}");
            }
            else if (request.Routine.IsFinished)
            {
                // Done only when the game placed every member meant to travel, and each stands on the cell the
                // game gave it. A member the functor found no free cell for was never placed.
                var missing = new List<string>();

                foreach (var guid in request.Members ?? [])
                {
                    var member = FindCharacter(guid);

                    if (member == null)
                    {
                        missing.Add("a character that is gone");
                    }
                    else if (!request.Placed.TryGetValue(guid, out var cell))
                    {
                        missing.Add($"{member.Name} (no free cell found)");
                    }
                    else if (!member.LocationPosition.Equals(cell))
                    {
                        missing.Add($"{member.Name} (placed at {cell}, now at {member.LocationPosition})");
                    }
                }

                if (missing.Count > 0)
                {
                    Finish(request, StatusFailed,
                        $"{missing.Count} of {request.Members.Count} did not arrive: {string.Join(", ", missing)}");
                }
                else
                {
                    Finish(request, StatusSucceeded, null);
                }
            }

            return;
        }

        // a dialogue or roll: over when the game has taken the trigger and the gadget's flow has ended
        var gadget = request.Gadget;
        var listener = gadget != null && request.Flow < gadget.activeListeners.Count
            ? gadget.activeListeners[request.Flow]
            : null;

        if (listener == null)
        {
            Finish(request, StatusFailed, "its gadget is gone");

            return;
        }

        // the gadget takes the trigger when it starts the flow, and keeps a coroutine while the flow runs (a
        // dialogue runs for as long as the player reads it)
        var waitingToStart = listener.IsTriggered;
        var flowRunning = gadget.activeFunctorSequence != null && gadget.currentListenerIndex == request.Flow;

        if (!waitingToStart && !flowRunning)
        {
            Finish(request, StatusSucceeded, null);
        }
        else if (waitingToStart && !flowRunning && GameSeconds() - request.StartedAt > MaxWaitSeconds)
        {
            // triggered, yet the game never ran it: its conditions stopped matching after it was started
            Finish(request, StatusFailed, $"the game never ran the interaction of '{gadget.UniqueNameId}'");
        }
    }

    // the request leaves the waiting list; its variable is cleared so the same value can be written again
    private static void MarkStarted(Request request)
    {
        Waiting.Remove(request.Variable);
        Started.Add(request);
        request.StartedAt = GameSeconds();
        ClearVariable(request);
        WriteStatus(request, StatusRunning);
    }

    // the game clock in seconds (TimeInfo counts minutes from year 0, which fits an int; seconds would not)
    private static long GameSeconds()
    {
        var info = Gui.GameCampaign?.GameTime?.TimeInfo;

        return info == null ? 0 : ((long)info.ComputeMinutes() * 60) + info.Second;
    }

    // a name is looked up on the request's first gadget update, then every LookupInterval updates
    private static bool LookupDue(Request request)
    {
        if (request.Ticks++ % LookupInterval != 0)
        {
            return false;
        }

        request.Lookups++;

        return true;
    }

    // the name fits nothing: allow for it appearing in the same moment, then end the request
    private static void WaitMissing(Request request, string reason)
    {
        if ((request.Lookups >= MissingMinLookups && GameSeconds() - request.IssuedAt >= MissingGraceSeconds) ||
            request.Lookups >= MissingMaxLookups)
        {
            Finish(request, StatusFailed, reason);
        }
    }

    // not possible yet for a reason that can pass: keep the request, say why once, give up after MaxWaitSeconds
    private static void Wait(Request request, string reason)
    {
        if (GameSeconds() - request.IssuedAt > MaxWaitSeconds)
        {
            Finish(request, StatusFailed, $"gave up after an hour of game time: {reason}");

            return;
        }

        if (request.LastWait == reason)
        {
            return;
        }

        request.LastWait = reason;
        Main.Info($"DM command {request.Variable} '{request.Target}' is waiting: {reason}.");
    }

    private static void Finish(Request request, string status, string reason)
    {
        var wasWaiting = Waiting.TryGetValue(request.Variable, out var waiting) && ReferenceEquals(waiting, request);

        if (wasWaiting)
        {
            Waiting.Remove(request.Variable);
            ClearVariable(request);
        }

        Started.Remove(request);

        if (SoloTeleports.Remove(request))
        {
            // it was still waiting in the queue
            ClearVariable(request);
        }

        WriteStatus(request, status);

        if (reason != null)
        {
            Main.Info($"DM command {request.Variable} '{request.Target}' {status.ToLowerInvariant()}: {reason}.");
        }

        if (status == StatusFailed)
        {
            AlertFailure(request);
        }
    }

    // Tells the player in game that a command failed, with the game's own alert (the small message at the top of
    // the screen); the reason in full is in the log. A roll or a one-character teleport that can't tell who acts
    // says what started it, as the way it was set off is the part the campaign's author can change.
    private static void AlertFailure(Request request)
    {
        try
        {
            var text = request.Kind is Kind.Roll or Kind.TeleportOne && !request.ActorKnown &&
                       request.ProblemTerm != null
                ? Gui.Format("Message/&DungeonMakerCommandNoActor", request.Variable, Gui.Localize(request.ProblemTerm))
                : Gui.Format("Message/&DungeonMakerCommandFailed", request.Variable, request.Target);

            ShowFailureAlert(text);
        }
        catch (Exception e)
        {
            // a failed alert must not turn a failed command into an exception in the gadget update
            Main.Error(e);
        }
    }

    // the game's alert, in the failure colour; nothing when no screen can show it (loading)
    internal static void ShowFailureAlert(string text)
    {
        Gui.GuiService?.ShowAlert(text, Gui.ColorFailure, 6f);
    }

    // The location is going away: waiting requests end as Cancelled, and a teleport still running is released
    // from the functor service's list of busy sources.
    private static void DropRequests()
    {
        foreach (var request in Waiting.Values.Concat(SoloTeleports.Where(x => !x.Expected)).ToArray())
        {
            Finish(request, StatusCancelled, "the location was left");
        }

        // what is left are places kept for requests that never came
        SoloTeleports.Clear();

        // A started request has already cleared its variable; without this its status would say Running for as
        // long as the campaign is played.
        foreach (var request in Started.ToArray())
        {
            if (request.Routine is { IsFinished: false })
            {
                ServiceRepository.GetService<IFunctorService>()?.EmergencyReleaseSource(request);
            }

            WriteStatus(request, StatusCancelled);
            Main.Info($"DM command {request.Variable} '{request.Target}' cancelled: the location was left.");
        }

        Waiting.Clear();
        Started.Clear();
        NewestSerial.Clear();
    }

    private static void GuardedStep(Request request, Action<Request> step)
    {
        try
        {
            step(request);
        }
        catch (Exception e)
        {
            // one request's failure ends that request only
            Main.Error(e);
            Finish(request, StatusFailed, $"it threw {e.GetType().Name} (details in the log)");
        }
    }

    private static void Guarded(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Main.Error($"Dungeon Maker commands ({what}): {e}");
        }
    }

    #endregion

    #region Variables

    private static bool Same(string a, string b)
    {
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void ReportOnce(ref string last, string message)
    {
        if (message == last)
        {
            return;
        }

        last = message;
        Main.Info(message);
    }

    internal static int IntValueOf(IGameVariableService variables, string name)
    {
        return variables.TryFindVariable(name, out var variable) && variable is
        {
            Type: GameVariableDefinitions.Type.Int
        }
            ? variable.IntValue
            : 0;
    }

    internal static bool IsOn(IGameVariableService variables, string name)
    {
        if (!variables.TryFindVariable(name, out var variable) || variable == null)
        {
            return false;
        }

        return variable.Type switch
        {
            GameVariableDefinitions.Type.Bool => variable.BoolValue,
            GameVariableDefinitions.Type.Int => variable.IntValue != 0,
            _ => false
        };
    }

    // Writes made by the mod itself. The game's listeners still see them (a Variable Activator on a status
    // variable works), only this class does not take them as requests.
    internal static void WriteInt(string name, int value)
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null || !variables.TryFindVariable(name, out var variable) || variable is not
            {
                Type: GameVariableDefinitions.Type.Int
            } || variable.IntValue == value)
        {
            return;
        }

        _writing++;

        try
        {
            variable.IntValue = value;
        }
        finally
        {
            _writing--;
        }
    }

    internal static void WriteString(string name, string value)
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null || !variables.TryFindVariable(name, out var variable) || variable is not
            {
                Type: GameVariableDefinitions.Type.String
            } || variable.StringValue == value)
        {
            return;
        }

        _writing++;

        try
        {
            variable.StringValue = value;
        }
        finally
        {
            _writing--;
        }
    }

    internal static string StringValueOf(IGameVariableService variables, string name)
    {
        return variables.TryFindVariable(name, out var variable) && variable is
        {
            Type: GameVariableDefinitions.Type.String
        }
            ? variable.StringValue
            : null;
    }

    // Makes sure a campaign variable of the mod's own exists, so that its value is saved with the game. It is a
    // campaign variable like the author's: one scoped to the location would be dropped on leaving it.
    internal static void EnsureInt(string name)
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null || variables.IsVariableRegistered(name))
        {
            return;
        }

        _writing++;

        try
        {
            variables.RegisterVariable(name, GameVariableDefinitions.Scope.Campaign, 0);
        }
        finally
        {
            _writing--;
        }
    }

    // the optional <command>_Status variable
    internal static void WriteStatus(string commandVariable, string status)
    {
        WriteString(commandVariable + StatusSuffix, status);
    }

    // A request that a newer one has replaced no longer speaks for the variable. One-character teleports are not
    // replaced, so each of them reports as it goes; the status variable shows the latest thing that happened.
    private static void WriteStatus(Request request, string status)
    {
        if (request.Kind == Kind.TeleportOne ||
            (NewestSerial.TryGetValue(request.Variable, out var newest) && newest == request.Serial))
        {
            WriteStatus(request.Variable, status);
        }
    }

    // sets the command variable back to "", unless a newer write already put something else there
    private static void ClearVariable(Request request)
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables != null && variables.TryFindVariable(request.Variable, out var variable) &&
            variable?.StringValue == request.Target)
        {
            WriteString(request.Variable, string.Empty);
        }
    }

    #endregion

    #region Gadgets

    private static Dictionary<ulong, GameGadget> GadgetsById()
    {
        return ServiceRepository.GetService<IGameLocationGadgetService>() is GameLocationGadgetManager manager
            ? manager.gadgetsByGuid
            : [];
    }

    // every gadget object of the loaded location
    // Has every object's timer in the location check itself, and nothing else: the "every N hours" timers get an
    // hour tick (what GameTime.UpdateTime raises once per update, RealTimeHourTicked) and the "after a time" timers
    // a tick of 0 seconds, i.e. a check of the seconds already added up. For a clock skip that a battle cut short
    // while these checks were held back (DungeonMakerClockContext). Each timer checks its own conditions as on a
    // tick from the game; the gadget runs a triggered flow at its next update.
    internal static void CheckTimers(int hour)
    {
        foreach (var gadget in WorldGadgets().Select(x => x.GameGadget).Where(x => x != null))
        {
            foreach (var listener in gadget.activeListeners.ToArray())
            {
                switch (listener)
                {
                    case GameGadgetListenerHourUpdate hours:
                        Guarded("an hour timer", () => hours.RealTimeHourTicked(hour, 1));
                        break;
                    case GameGadgetListenerElapsedTime elapsed:
                        Guarded("an elapsed time timer", () => elapsed.RealTimeSecondTicked(0));
                        break;
                }
            }
        }
    }

    private static IEnumerable<WorldGadget> WorldGadgets()
    {
        var sectors = (ServiceRepository.GetService<IGameLocationService>() as GameLocationManager)?.WorldLocation?
            .WorldSectors;

        return sectors == null
            ? []
            : sectors.Where(x => x != null).SelectMany(x => x.WorldGadgets).Where(x => x != null);
    }

    private static string[] ConditionChoices(WorldGadget world)
    {
        try
        {
            return world.ConditionChoices() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    // Dialog_ListGadgets: writes the location's gadgets to the log for the author
    private static void ListGadgets(IGameVariableService variables)
    {
        var mode = IntValueOf(variables, ListVariable);

        if (mode == 0)
        {
            return;
        }

        WriteInt(ListVariable, 0);

        var showAll = mode == 2;
        var gadgets = GadgetsById();
        var interactive = gadgets
            .Where(x => x.Value.activeListeners.Any(l => l is GameGadgetListenerCharacterInteraction))
            .ToList();

        Main.Info(
            $"DM gadgets in this location: {gadgets.Count} ({interactive.Count} interactive){(showAll ? ", listing all" : "")}");

        foreach (var entry in showAll ? gadgets.ToList() : interactive)
        {
            var gadget = entry.Value;
            var npc = NpcNames.Of(entry.Key);
            var npcText = npc == null
                ? "npc=(no spawned character bound)"
                : $"npcDefinition='{npc.Definition}' npcTitle='{npc.Title}'";
            var kinds = gadget.activeListeners
                .Where(l => l is GameGadgetListenerCharacterInteraction)
                .Select(l => l.FunctorParams.Any(x => DialogFunctors.Contains(x.Type)) ? "dialogue"
                    : l.FunctorParams.Any(x => RollFunctors.Contains(x.Type)) ? "roll"
                    : "other");

            Main.Info(
                $"DM gadget: name='{gadget.UniqueNameId}' internalNpc='{gadget.BoundCharacterUniqueName}' {npcText} interactions=[{string.Join(", ", kinds)}]");
        }

        if (!showAll)
        {
            return;
        }

        var worlds = WorldGadgets().OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase).ToList();

        Main.Info($"DM gadget objects in this location: {worlds.Count}");

        foreach (var world in worlds)
        {
            Main.Info(
                $"DM gadget object: name='{world.name}' conditions=[{string.Join(", ", ConditionChoices(world))}]");
        }
    }

    #endregion

    #region Torches

    // The torch list is built once per location, the first time a torch variable is seen there. Torches are then
    // looked at again only when a torch variable is written or the hour changes, not every few frames.
    private static void ApplyTorches(IGameVariableService variables)
    {
        var location = CurrentLocation;

        if (!ReferenceEquals(_torchLocation, location))
        {
            _torchLocation = location;
            Torches.Clear();

            foreach (var world in WorldGadgets())
            {
                if (world.name == null ||
                    world.name.IndexOf(TorchNameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var choices = ConditionChoices(world);

                Torches.Add(new Torch
                {
                    World = world,
                    Variable = TorchVariablePrefix + world.name,
                    LitIndex = Array.FindIndex(choices, c => LitConditionNames.Any(n => Same(n, c)))
                });
            }

            // has the campaign opted in at all? (a later write of a torch variable opts in too)
            _torchControlSeen = variables.TryFindVariable(TorchesVariable, out _) ||
                                variables.TryFindVariable(TorchesAutoVariable, out _) ||
                                Torches.Any(x => variables.TryFindVariable(x.Variable, out _));
        }

        if (!_torchControlSeen)
        {
            return;
        }

        var manual = IntValueOf(variables, TorchesVariable);
        var auto = AutoTorchState(variables, location);

        if (Torches.Count == 0 && (manual != 0 || auto != 0))
        {
            var report = $"DM torches: a torch variable is in use but no gadget with '{TorchNameFilter}' in its name is in this location.";

            // once per location, as the log line: the alert names the problem, the log the details
            if (report != _lastTorchReport)
            {
                ShowFailureAlert(Gui.Localize("Message/&DungeonMakerTorchesNone"));
            }

            ReportOnce(ref _lastTorchReport, report);
        }

        var notBuilt = false;

        foreach (var torch in Torches)
        {
            if (!torch.World)
            {
                continue;
            }

            var own = IntValueOf(variables, torch.Variable);

            notBuilt |= !ApplyTorch(torch, own is 1 or 2 ? own : manual is 1 or 2 ? manual : auto);
        }

        // gadgets are built over the first updates of a location: come back for those that were not ready
        if (notBuilt && _torchRetriesLeft-- > 0)
        {
            _torchesDirty = true;
        }
    }

    // 0 = no opinion, 1 = lit, 2 = out: what the clock says for the automatic layer on this map right now.
    // Outdoor is the game's own flag on the map's environment; only Dungeon Maker maps have one.
    private static int AutoTorchState(IGameVariableService variables, GameLocation location)
    {
        if (!IsOn(variables, TorchesAutoVariable))
        {
            return 0;
        }

        var hour = Gui.GameCampaign?.GameTime?.TimeInfo?.Hour;
        var environment = location?.UserLocation?.Environment;

        if (hour == null || environment == null || !environment.Outdoor)
        {
            return 0;
        }

        return hour >= AutoLitFromHour || hour < AutoOutFromHour ? 1 : 2;
    }

    // Brings one torch to the wanted state, once per change: a torch a player toggles afterwards stays as the
    // player left it until the layer above it changes again. Returns false when the torch's game object does not
    // exist yet.
    private static bool ApplyTorch(Torch torch, int desired)
    {
        if (desired is not (1 or 2))
        {
            // no opinion any more: forget it so that a later state is applied again
            torch.Applied = 0;

            return true;
        }

        var gadget = torch.World.GameGadget;

        if (gadget == null)
        {
            return false;
        }

        if (torch.Applied == desired)
        {
            return true;
        }

        torch.Applied = desired;

        if (torch.LitIndex < 0)
        {
            if (!torch.Reported)
            {
                torch.Reported = true;
                ShowFailureAlert(Gui.Format("Message/&DungeonMakerTorchNoCondition", torch.World.name));
                Main.Info(
                    $"DM torches: '{torch.World.name}' has no lit condition; its conditions are [{string.Join(", ", ConditionChoices(torch.World))}].");
            }

            return true;
        }

        var lit = desired == 1;

        if (gadget.CurrentConditionStates != null &&
            gadget.CurrentConditionStates.TryGetValue(torch.LitIndex, out var current) && current == lit)
        {
            return true;
        }

        // what the game's own "set gadget condition" functor does
        gadget.SetCondition(torch.LitIndex, lit, []);
        Main.Info($"DM torches: '{torch.World.name}' -> {(lit ? "lit" : "out")}.");

        return true;
    }

    #endregion

    private enum Kind
    {
        Dialog,
        Roll,
        TeleportOne,
        TeleportParty
    }

    private sealed class Request
    {
        internal ulong ActorGuid;
        internal bool ActorKnown;
        internal string ActorName;
        internal string ActorProblem;

        // why a fallback was used to find the acting character, for the log
        internal string ActorNote;

        // For a command that needs its acting character and can't have one: the translation term naming, to the
        // player, the kind of trigger that started it (a timer, a Variable Activator...), for the alert.
        internal string ProblemTerm;

        // the gadgets the activation went through, and how it started (from the Activation), for the log
        internal string Chain;
        internal string Origin;
        internal int Flow;
        internal GameGadget Gadget;

        // game seconds when it was written and when it was handed to the game
        internal long IssuedAt;
        internal Kind Kind;
        internal string LastWait;
        internal GameLocation Location;
        internal int Lookups;
        internal TA.Coroutine Routine;
        internal int Serial;
        internal long StartedAt;
        internal string Target;
        internal int Ticks;
        internal string Variable;

        // One-character teleport: a place kept in the queue for a request that is still to come, and the flow it
        // is expected from. Target, the acting character and the rest are set when the write arrives.
        internal bool Expected;
        internal GameGadgetListener Listener;

        // party teleport: everyone meant to travel, and the cell the game put each of them on
        internal List<ulong> Members;
        internal readonly Dictionary<ulong, TA.int3> Placed = [];
    }

    // One activation of an Area Activator, a pressure plate or an interaction: the character whose move or use
    // switched its trigger on. A gadget activated by another gadget's Activate operation gets a copy, with the
    // gadget added to Chain (see NoteConditionToggled).
    private sealed class Activation
    {
        // the place this activation keeps in the queue of one-character teleports, until its flow writes
        internal Request Expected;

        // several characters were placed at once, so no single one caused it
        internal bool Group;
        internal ulong MoverGuid;
        internal bool MoverKnown;
        internal bool SoloTeleportEligible;
        internal string MoverName;

        // How it started, in words, and the gadgets it went through, oldest first, for the log: "Area Activator
        // 'ActivatorArea22' (Hero A walked in)" and "ActivatorArea22 > VariableOperation19".
        internal string Origin;
        internal string Chain;

        // for an untraced chain, the translation term naming what started it to the player (see TriggerTerm)
        internal string OriginTerm;

        // the start of the chain is not known to the mod (its flow had no recorded activation), so who caused it
        // is not known either; Origin then says what started it as far as it can tell
        internal bool Untraced;
    }

    private sealed class Torch
    {
        internal int Applied;
        internal int LitIndex;
        internal bool Reported;
        internal string Variable;
        internal WorldGadget World;
    }

    // The names an author may know an NPC by. Both are the campaign's own data, the same on every computer: the
    // name the game shows for a character can depend on the language the game runs in, and two players would then
    // resolve the same command to different gadgets, or one of them to none.
    private sealed class NpcNames
    {
        internal string Definition = string.Empty; // MonsterDefinition.Name: what was typed when creating the NPC
        internal string Title = string.Empty; // its title as stored, not put through the game's translation

        internal bool Matches(string name)
        {
            return Same(Definition, name) || Same(Title, name);
        }

        // the NPC bound to this gadget, as the game binds them (GameLocationCharacter.BoundGadgetId is the key of
        // the gadget table); null while no spawned character is bound to it
        internal static NpcNames Of(ulong gadgetId)
        {
            var character = gadgetId == 0
                ? null
                : ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters?
                    .FirstOrDefault(x => x != null && x.BoundGadgetId == gadgetId);

            if (character == null)
            {
                return null;
            }

            var definition = (character.RulesetCharacter as RulesetCharacterMonster)?.MonsterDefinition;

            return new NpcNames
            {
                Definition = Safe(() => definition?.Name), Title = Safe(() => definition?.GuiPresentation?.Title)
            };
        }

        private static string Safe(Func<string> getter)
        {
            try
            {
                return getter() ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
