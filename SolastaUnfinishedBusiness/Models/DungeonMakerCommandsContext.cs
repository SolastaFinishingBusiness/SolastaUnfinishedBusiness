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
//   Teleport_Node        the name of a Node gadget: the acting character is teleported onto its exact cell.
//   Teleport_PartyNode   the name of a Node gadget: the whole party, with the guests travelling with it, is
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
//   - anything else (a timer, a variable or condition activator, a quest step, a variable set from a dialogue, a
//     save loaded while the command was waiting) has no acting character, so the command fails and says so.
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
//   - The character is put on the Node's exact cell, height included, with the game's own TeleportCharacter and
//     none of its placement search: a cell another hero stands on, a cell with no floor and a hazard are all
//     allowed, and no safer cell is substituted. Arrival is checked once, right there (the character's three
//     coordinates). Only then is the character's footing checked, so a fall or a hazard follows as the game does
//     it; a fall after arriving is not a failed teleport.
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

    // the Dungeon Maker gadget a teleport's destination must be (GadgetBlueprint.Name)
    private const string NodeBlueprint = "Node";

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

    // Per Area Activator / pressure plate flow, whose move made it fire (see NoteOccupationChanged): the
    // activation whose flow has not started yet, and the one whose flow the game is running (or ran last).
    private static ConditionalWeakTable<GameGadgetListener, Activation> _causes = new();
    private static ConditionalWeakTable<GameGadgetListener, Activation> _runs = new();

    // per interaction flow: the place it keeps in the queue of one-character teleports (see NoteInteracted)
    private static ConditionalWeakTable<GameGadgetListener, Request> _interactionPlaces = new();

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

    private static IGameVariableService _subscribedTo;
    private static GameLocation _torchLocation;
    private static bool _listRequested;
    private static bool _torchesDirty;
    private static bool _torchControlSeen;
    private static int _torchRetriesLeft;
    private static string _lastTorchReport = string.Empty;
    private static string _lastTypeReport = string.Empty;

    private static bool Enabled => Main.Settings.EnableDungeonMakerVariableCommands;

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

        if (!Enabled || variables == null)
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
        _causes = new ConditionalWeakTable<GameGadgetListener, Activation>();
        _runs = new ConditionalWeakTable<GameGadgetListener, Activation>();
        _interactionPlaces = new ConditionalWeakTable<GameGadgetListener, Request>();
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
        if (operation?.StringParameter != TeleportNodeVariable || _serial != serialBefore || !Enabled ||
            CurrentLocation == null)
        {
            return;
        }

        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables != null && variables.TryFindVariable(TeleportNodeVariable, out var variable) && variable is
            {
                Type: GameVariableDefinitions.Type.String
            } && !string.IsNullOrEmpty(variable.StringValue))
        {
            Capture(Kind.TeleportOne, TeleportNodeVariable, variable.StringValue, operation);
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
            var activation = new Activation
            {
                MoverKnown = mover != null,
                SoloTeleportEligible = CanTriggerSoloTeleport(mover),
                MoverGuid = mover?.Guid ?? 0,
                MoverName = mover?.Name,
                // the party is put down together when a location is entered: no one character "walked in"
                Group = ServiceRepository.GetService<IGameLocationService>() is not { LocationIsReady: true }
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

    // GameGadget.Interact, after it ran: a character used a gadget. The same as above for an interaction: if the
    // game accepted it (the flow is triggered) and the flow has a Variable Operation on Teleport_Node, its place
    // in the queue is kept now, so that a flow which waits before it writes is not overtaken by a plate or an
    // area set off after it. An interaction has one flow waiting at a time, so one place per listener is enough;
    // using the gadget again before the flow started keeps the place it has.
    //
    // The character is fixed here as well. The game writes the interacting character into the flow's functor
    // parameters at every accepted Interact, so if a second character uses the gadget before the flow has
    // written the variable, the parameters name the second one by then. The place belongs to the first: its
    // character, and whether it may cause a one-character teleport, are noted with the place, and the write
    // that fills the place takes them from there instead of from the parameters.
    internal static void NoteInteracted(GameGadget gadget, int listenerIndex, GameLocationCharacter actor)
    {
        var listeners = gadget?.activeListeners;

        if (listeners == null || listenerIndex < 0 || listenerIndex >= listeners.Count ||
            listeners[listenerIndex] is not GameGadgetListenerCharacterInteraction { IsTriggered: true } listener ||
            (_interactionPlaces.TryGetValue(listener, out var held) && held.Expected && SoloTeleports.Contains(held)))
        {
            return;
        }

        var kept = KeepPlace(listener);

        _interactionPlaces.Remove(listener);

        if (kept == null)
        {
            return;
        }

        kept.ActorFixed = true;

        if (CanTriggerSoloTeleport(actor))
        {
            kept.ActorKnown = true;
            kept.ActorGuid = actor.Guid;
            kept.ActorName = actor.Name;
        }
        else
        {
            kept.ActorProblem = actor == null
                ? "the interaction had no acting character"
                : "the triggering character was outside the party and guest rosters";
        }

        _interactionPlaces.Add(listener, kept);
    }

    // Adds a place to the queue of one-character teleports for this flow, if the flow has a Variable Operation on
    // Teleport_Node; returns it, or null when the flow has none.
    private static Request KeepPlace(GameGadgetListener listener)
    {
        if (!Enabled || CurrentLocation == null || listener.FunctorParams == null ||
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

    // AcknowledgeTrigger of those two listeners: the game is about to run the flow for the trigger it noted. From
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
        if (!Enabled || ServiceRepository.GetService<IGameLocationService>() is not { LocationIsReady: true })
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
        if (_writing > 0 || variable?.Name == null || !Enabled || CurrentLocation == null)
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
                // the requests made so far; a place kept for one still to come is not a request yet
                foreach (var waiting in SoloTeleports.Where(x => !x.Expected).ToArray())
                {
                    Finish(waiting, StatusCancelled, "the variable was cleared");
                }
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

        // a place kept by an interaction already holds its character (NoteInteracted)
        if (!request.ActorFixed)
        {
            ResolveActor(operation, request);
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

        // an interaction's place is kept by its listener (NoteInteracted)
        if (listener is GameGadgetListenerCharacterInteraction)
        {
            if (!_interactionPlaces.TryGetValue(listener, out var place) || !place.Expected ||
                !SoloTeleports.Contains(place))
            {
                return null;
            }

            _interactionPlaces.Remove(listener);

            return place;
        }

        if (listener == null || !_runs.TryGetValue(listener, out var activation) ||
            activation.Expected is not { Expected: true } kept || !SoloTeleports.Contains(kept))
        {
            return null;
        }

        activation.Expected = null;

        return kept;
    }

    // A place kept at the head of the queue: is its request still to come? Yes while the flow has not started
    // (its trigger is set) or is running (the gadget's coroutine is on this flow). A flow that has ended without
    // writing, or that the game never starts, gives its place up, so the requests behind it are not held back.
    private static bool StepExpected(Request kept)
    {
        var listener = kept.Listener;
        var gadget = listener?.SourceGadget ? listener.SourceGadget.GameGadget : null;

        if (gadget == null || !ReferenceEquals(kept.Location, CurrentLocation))
        {
            return false;
        }

        if (gadget.activeFunctorSequence != null && gadget.currentListenerIndex == listener.Index)
        {
            // running: it may be waiting on purpose before it writes
            return GameSeconds() - kept.IssuedAt <= MaxWaitSeconds;
        }

        // not started yet: the game starts a triggered flow at the gadget's next update, unless something keeps it
        return listener.IsTriggered && ++kept.Ticks <= ExpectedPatienceTicks;
    }

    // Works out which character a write acts for, from the gadget flow that made it. It never picks among
    // several: a request that can't name exactly one character keeps the reason instead.
    private static void ResolveActor(FunctorParametersDescription operation, Request request)
    {
        if (operation == null)
        {
            request.ActorProblem = "no object's Variable Operation set the variable (a quest, a dialogue, a cheat, " +
                                   "or a save loaded while it was waiting), so no character is tied to it";

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
            // something a character used: the game hands the flow exactly that character (GameGadget.Interact)
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
            default:
                request.ActorProblem = listener == null
                    ? "the object that set the variable could not be identified"
                    : $"'{gadgetName}' set it from a {listener.GetType().Name.Replace("GameGadgetListener", "")} " +
                      "activation (a timer, a variable or another event), which has no acting character";
                break;
        }
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
            // The game's own Teleporter takes any gadget of that name as its destination. A command names its
            // destination in free text, so a slip could send characters to a door or a chest: only a Node is taken.
            case 1 when nodes[0].UserGadget?.GadgetBlueprintName != NodeBlueprint:
                Finish(request, StatusFailed,
                    $"'{request.Target}' is a {nodes[0].UserGadget?.GadgetBlueprintName ?? "gadget of the game"}, not a Node");

                return null;
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

        var destination = positioning.GetGridPositionFromWorldPosition(node.transform);

        // from here the request is no longer waiting: its variable is cleared so the same name can be written again
        SoloTeleports.Remove(request);
        ClearVariable(request);
        WriteStatus(request, StatusRunning);
        Main.Info(
            $"DM command {request.Variable}: teleporting {character.Name} to '{node.name}' at {destination}.");

        // What the game's Teleport functor does before it moves a character, and the call it moves it with. The
        // functor first searches for a free, safe cell near the marker; that search is left out on purpose, so
        // the character lands on the cell itself.
        ServiceRepository.GetService<IGameLocationActionService>()?
            .StopCharacterActions(character, CharacterAction.InterruptionType.Kill, true);
        positioning.TeleportCharacter(character, destination, character.Orientation);

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
            ReportOnce(ref _lastTorchReport,
                $"DM torches: a torch variable is in use but no gadget with '{TorchNameFilter}' in its name is in this location.");
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

        // the acting character was noted when the place was kept, and is not read again at the write
        internal bool ActorFixed;
        internal GameGadgetListener Listener;

        // party teleport: everyone meant to travel, and the cell the game put each of them on
        internal List<ulong> Members;
        internal readonly Dictionary<ulong, TA.int3> Placed = [];
    }

    // one activation of an Area Activator or pressure plate: the character whose move switched its trigger on
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
