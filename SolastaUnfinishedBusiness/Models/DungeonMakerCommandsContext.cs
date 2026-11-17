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
//   Teleport_PartyNode   the name of a gadget (a Node, usually): the whole party, with the guests travelling with it,
//   is
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
//   - when a character damages a gadget that reacts to damage (a button that can be attacked), casts a spell or
//     effect on it, reveals a tag on it or steals from it, it is that character, if it is a party member or a
//     guest travelling with the party;
//   - whatever such a flow sets off acts for the same character, through any number of steps: another flow or
//     gadget, a variable or a quest step that a Variable or Quest Activator reacts to, a dialogue and the
//     actions of its lines, a timer it sets going (issue #42: the original author's test campaign is built this
//     way, an Area Activator activating a Variable Operation gadget that writes the variable). See "FOLLOWING A
//     CHAIN" below. If the start of such a chain isn't traced to one character, the one eligible character the
//     game passed along the chain acts, if there is exactly one;
//   - anything nobody set off (a timer that runs from the start, the location loading, a variable set by the
//     mod itself or from the cheats panel, a quest the game advanced, or a chain they start) has no acting
//     character, so the command fails and says so: a line in the log with what wrote the variable, the chain,
//     what started it and the characters passed along (LogResolution), and the game's alert at the top of the
//     screen (AlertFailure).
//   For Roll_Trigger the character must still be a conscious party member in the location when the command runs.
//   Dialog_Trigger uses the acting character when there is one, otherwise the first conscious party member opens
//   the dialogue as a stand-in: the dialogue and what it sets off then act for the request's acting character if
//   it has one (unconscious, say), and for nobody otherwise (StandInRecord).
//   Teleport_PartyNode needs nobody.
//
// FOLLOWING A CHAIN, so the commands work through as many kinds of flow as possible without changing what the
// game's own tools do. The game's own gadgets pass an activation on in several steps, each a flow that switches on
// the next one. An Area Activator takes three to reach its targets (read from its prefab):
//     the area's flow sets the gadget's own "Activated" condition         (SetGadgetCondition)
//       > the flow waiting on that condition raises the event TriggeredOn (RaiseGadgetEvent)
//       > the flow waiting on that event runs the Activate operation      (UserGadgetOperation)
//       > the target gadget's flow waiting on its RemoteActivated condition starts.
//   The pressure plates and buttons do the same, a Roll Activator goes from its roll to the Success or Failure
//   targets through a condition, and an author's own gadgets can be chained in any number.
//   - Every kind of flow tells its gadget "I am triggered" through one method, GameGadget.ListenerStatusChanged,
//     and does so at once, inside the step that caused it: the functor that sets the condition or raises the
//     event (seven game functors, the only callers of GameGadget.SetCondition and RaiseGadgetEvent), writes the
//     variable (GameGadgetListenerVariableChanged.VariableValueChanged) or advances the quest
//     (GameQuestManager raises its quest events inside StartQuest and AchieveStep). The flow itself starts at a
//     later gadget update.
//   - So one rule covers them all, without a patch per functor or per kind of flow: a flow that becomes
//     triggered while the game runs another flow's steps (RunningFlow) was switched on by that flow, and gets a
//     copy of its record of who set it off (NoteListenerStatusChanged). The game starts the new flow later, and
//     the record goes with it (NoteFlowStarted).
//   - A dialogue: the "start dialogue" step of a flow builds the dialogue (NoteSequencePrepared), which then
//     belongs to whoever set that flow off; while the game carries out the actions of one of its lines
//     (NarrativeState.ExecuteFunctors, the only place a dialogue runs functors), what they set off, and a command
//     variable they write themselves, act for that character.
//   - A timer: the game counts an "after a time" flow's seconds only while its conditions hold, and hands its
//     flow no characters. The character whose flow completes those conditions (enables a Timed Activator), or
//     resets its time, is kept for the timer and acts when it fires (NoteConditionSet), until something without
//     a character sets it going again or it is stopped.
//   - What a character does to a gadget's bound character: the game tells the gadget who (BeginCauser).
//   - The game hands its own list of characters along conditions and events (ActingCharacters), but for an area
//     that is everyone standing in it, refreshed on every move, and some twenty game functors act on that list
//     (who an effect hits, who rolls, who receives an item). It is never changed here, nor is any other state of
//     the game: only the mod's own record travels, and every hook only looks.
//   - A flow switched on by a flow that has no record gets a record that says what started the chain
//     (Untraced), for the failure message.
//   - The mod's own writes are no link: the <command>_Status variables, the Clock_ read-outs and counters, and the
//     emptying of a command variable carry no character, and don't replace a record a flow already has. A
//     campaign's own flow writing to such a variable is an ordinary write.
//   - Not followed: a change made from outside any gadget flow and any dialogue (a quest's own actions when the
//     game advances it, the cheats panel); a flow that was running when the game was saved (the game does not
//     resume it after a load).
//
// SAVES. The game keeps in a save which
// flows are triggered and have not started (GameGadget.flowsSaveData, "pending trigger"), how far each timer has
// counted, and the command variables themselves; it starts those flows and the mod takes those commands again
// after a load. Who is behind them is the mod's own knowledge, so it goes into the save too:
//   - right before the game writes the campaign's variables (BeforeVariablesSaved), the mod puts one String
//     variable among them, Commands_SavedState (or Commands_SavedState_2, ... when the campaign has a variable of
//     that name; it is known after a load by the signature its text starts with, not by its name): the records of the
//     triggered flows, the timers set going by a character, the waiting commands' acting characters and the whole
//     one-character teleport queue, and the state last applied to each torch (SaveState). It is taken out again as
//     soon as they are written, so it exists in the save only and the running game is not changed by saving;
//   - after a load, before the location's first gadget update (PrepareLocation; the game updates the gadgets of
//     a loading location twice before it is ready, and the flows the save kept can start there), it is read, taken
//     out of the variables, and what still fits is put back before the waiting commands are taken (RestoreState);
//   - nothing is written when nothing is waiting and no torch was switched, so a campaign that doesn't use the
//     commands or the torch variables has no such variable in its saves, and a save loads without the mod as
//     before (the game then has one more String variable, which nothing reads).
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
//   A state is applied to a torch once, when the variable that decides it asks for a new one; a torch toggled by
//   hand afterwards stays so, also over leaving the location and over a save and load (TorchStates).
//
// DISCOVERY
//   Dialog_ListGadgets   Int. 1 lists the interactive gadgets of the location in the log, 2 lists every gadget
//                        object with its conditions (to find Node and torch names); the mod sets it back to 0.
//
// A CAMPAIGN THAT USES THESE NAMES ITSELF (DungeonMakerNamesContext has the rules): a name only the mod writes
// (a <command>_Status, a clock read-out) that the campaign writes too is the campaign's own, and the mod writes to
// <name>_2 instead (every write of such a name goes through DungeonMakerNamesContext.Written); a command variable
// whose type is wrong, or whose text names nothing in the location or anywhere in the campaign, is the
// campaign's own for the rest of the game, and is left alone without a message (NamesNothing, MarkOwnRequest).
//
// HOW IT RUNS, and why (rather than polling these variables from the game clock's per-frame update):
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

    // The mod's own String variable, which exists only inside a save: what the commands and chains that were
    // waiting when the game was saved need to keep their acting character (see "SAVES" at the top).
    internal const string SavedStateVariable = "Commands_SavedState";
    private const string SavedStateVersion = "1";

    // What the saved state's text starts with. The mod's variable is known by this, not by its name: when the
    // campaign has a variable called Commands_SavedState, the mod's is Commands_SavedState_2, and so on.
    private const string SavedStateSignature = "FB-DM-STATE";

    // how many names are tried, and looked at after a load, before giving up
    private const int SavedStateNames = 50;

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

    // Per flow, who set it off: the activation whose flow has not started yet, and the one whose flow the game is
    // running (or ran last). The first is a plain dictionary, emptied when the location is left, because it is
    // written into saves (SaveState) and so has to be listed. An Area Activator's, a pressure plate's or an
    // interaction's flow gets its record from the character who moved or used it (NoteOccupationChanged,
    // NoteInteracted); a condition or gadget event flow gets a copy of the record of the flow that switched it on
    // (NoteListenerStatusChanged).
    private static Dictionary<GameGadgetListener, Activation> _causes = [];
    private static ConditionalWeakTable<GameGadgetListener, Activation> _runs = new();

    // while the game's Teleport functor moves characters (see BeginGroupMove): who it moved, and the
    // activations those moves caused
    private static readonly HashSet<ulong> GroupMovers = [];
    private static readonly List<Activation> GroupActivations = [];
    private static int _groupDepth;

    private static readonly List<Torch> Torches = [];

    // Per torch of the game being played (TorchKey: its location and its gadget name), the state the mod last
    // applied to it and the variable that decided it. A torch's lit condition is part of its gadget's state, which
    // the game keeps when the party leaves the location and in saves; keeping this over the same span is what
    // makes "applied once per change" hold there too, so a torch a player toggled by hand isn't set back on coming
    // back or on loading. Kept in saves with the saved state ("L" entries), emptied when another game is played
    // (PrepareLocation).
    private static readonly Dictionary<string, TorchState> TorchStates = [];

    // the automatic torches' day or night at the last hour change, to see it switch (NoteHourChanged)
    private static bool? _autoLitPeriod;

    private static int _serial;

    // > 0 while this class writes a variable itself, so its own write is not taken as a request
    private static int _writing;

    // the party teleport whose Teleport functor is being stepped right now, if any (see NoteCharacterTeleported)
    private static Request _placing;

    // the Dialog_Trigger request whose dialogue a stand-in character is opening right now, if any (see
    // StepInteraction and NoteInteracted)
    private static Request _standInFor;

    // the Variable Operation functor that is running right now, if any (see BeginVariableOperation)
    private static FunctorParametersDescription _operation;

    // The gadget the game is updating right now, and the flow it is running in one go inside
    // GameGadget.ExecuteFunctorSequenceSync, if any: together they say which flow's steps are running (see
    // RunningFlow).
    private static GameGadget _updating;
    private static GameGadgetListener _runningSync;

    // Timers ("after a time" and "every N hours" flows) that a character's flow set going, with that character's
    // record: it is the cause of the timer's next firing (see NoteConditionSet and NoteTimerReset).
    private static Dictionary<GameGadgetListener, Activation> _armed = [];

    // Dialogues (and observation sequences) a gadget flow started, with that flow's record: the dialogue's
    // actions are carried out for the same character (see NoteSequencePrepared and BeginNarrativeActions).
    private static ConditionalWeakTable<NarrativeSequence, Activation> _dialogues = new();

    // > 0 while the game carries out the actions of a dialogue step (NarrativeState.ExecuteFunctors), and the
    // record of the dialogue they belong to, if it has one
    // the variable the mod put into the campaign's variables for the save being written, by its name
    private static string _savedStateMade;

    private static int _narrativeDepth;
    private static Activation _narrativeRecord;
    private static readonly Stack<Activation> NarrativeBefore = [];

    // While the game tells a gadget that the character bound to it was damaged, had a spell or effect put on it,
    // had a tag revealed or was stolen from: the character who did it, as the game named it, and the word for
    // the log. _causerSet says the game is doing that now (the character itself can be unknown).
    private static bool _causerSet;
    private static GameLocationCharacter _causer;
    private static string _causerHow;

    // a chain's list of gadget names for the log is cut at the front beyond this length (gadgets that keep
    // activating each other in a circle would make it grow without end)
    private const int MaxChainLength = 300;

    private static IGameVariableService _subscribedTo;

    // the location object whose commands' state has been set up (PrepareLocation); kept after it unloads, so the
    // gadget updates the game still makes while the location exits don't set it up again
    private static GameLocation _preparedLocation;

    private static GameLocation _torchLocation;
    private static bool _listRequested;
    private static bool _torchesDirty;
    private static bool _torchControlSeen;
    private static int _torchRetriesLeft;
    private static string _lastTorchReport = string.Empty;


    private static GameLocation CurrentLocation => ServiceRepository.GetService<IGameLocationService>()?.GameLocation;

    #region Hooks

    // GameLocation.UpdateGadgets, before it runs. While a location loads (IGameLocationService.LocationIsReady is
    // still false), GameLocationManager.LoadLocationAsync updates the new location's gadgets twice before it calls
    // ReadyLocation: right after the LocationEntered event and right after LocationFinalized (read in its IL). The
    // game's per-frame update (GameLocation.UpdateLocation) doesn't update gadgets of a loading location until it
    // is ready. Those two updates are the first time any of the location's flows can start, including those a save
    // kept as triggered (GameGadget.PostLoad sets PendingSavedTrigger at LocationPostLoading, earlier in the same
    // coroutine), and they can write command variables. So the location's state (the subscription, the save's
    // records and teleport queue, the waiting commands) is set up here, before the first of them, once per
    // location object. The location being left is still updated while it exits; it was set up when it loaded, so
    // it is never set up again.
    internal static void BeforeGadgetUpdate(GameLocation location)
    {
        if (location == null || ReferenceEquals(location, _preparedLocation) ||
            !ReferenceEquals(location, CurrentLocation) ||
            ServiceRepository.GetService<IGameLocationService>() is not { LocationIsReady: false })
        {
            return;
        }

        PrepareLocation();
    }

    // GameLocationManager.ReadyLocation: a location has finished loading (entered, or loaded from a save). Its state
    // was set up before its first gadget update (PrepareLocation); this covers a location that became ready without
    // one, then starts what only runs once the location is ready.
    internal static void OnLocationReady()
    {
        if (!ReferenceEquals(_preparedLocation, CurrentLocation))
        {
            PrepareLocation();
        }

        // the mod version the campaign's file was saved with, for the log
        DungeonMakerStampContext.NotePlaying(CurrentLocation?.UserCampaign);

        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null)
        {
            return;
        }

        _listRequested = IntValueOf(variables, ListVariable) != 0;
        _torchesDirty = true;
        _torchRetriesLeft = TorchRetries;
        DungeonMakerClockContext.OnLocationReady();
    }

    // Sets up the commands for the location that is loading (BeforeGadgetUpdate) or, failing that, has just become
    // ready (OnLocationReady): the subscription to the game's variable event, the saved state (records of triggered
    // flows and timers, the one-character teleport queue), the commands the variables already hold with the acting
    // characters the save kept for them. Done before any of the location's flows can start, so a flow that starts
    // while the location loads finds its record, its kept place in the teleport queue comes after the saved queue,
    // and the variables it writes are seen with the flow that wrote them.
    private static void PrepareLocation()
    {
        _preparedLocation = CurrentLocation;

        Guarded("location set-up", PrepareLocationState);
    }

    private static void PrepareLocationState()
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        // which of the mod's names this campaign owns, before anything below writes to one
        DungeonMakerNamesContext.Refresh(CurrentLocation);

        // the variable service belongs to the game being played (a GameSubmanager, made anew with each game, new or
        // loaded): move the subscription when it is another one
        if (!ReferenceEquals(_subscribedTo, variables))
        {
            DungeonMakerNamesContext.ResetSession();
            TorchStates.Clear();
            _autoLitPeriod = null;

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
        // this covers a location that went away without it). Requests already made for this location stay.
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

        // what a save kept of the chains and commands that were waiting: first the records and the teleport
        // queue, then, once the commands below are taken, their acting characters
        Dictionary<string, string[]> savedWaiting = null;

        // records of flows of a location that went away without the unload hook must not be mistaken for this
        // location's (they are listed by gadget name when a save is written)
        var gadgetsHere = new HashSet<GameGadget>(GadgetsById().Values);

        foreach (var table in new[] { _causes, _armed })
        {
            foreach (var gone in table.Keys
                         .Where(x => !x.SourceGadget || !gadgetsHere.Contains(x.SourceGadget.GameGadget))
                         .ToArray())
            {
                table.Remove(gone);
            }
        }

        Guarded("saved commands", () => savedWaiting = RestoreState(variables, location));

        // A command variable can already hold a value here without an event having told us: a save made while it
        // was waiting, or a write made outside a location. Take it now. No gadget flow is behind it, so it has no
        // acting character, unless the save kept one (just below).
        foreach (var command in Commands)
        {
            if (!DungeonMakerNamesContext.IsOwnRequest(command.Key) && !Waiting.ContainsKey(command.Key) &&
                SoloTeleports.All(x => x.Expected || x.Variable != command.Key) &&
                variables.TryFindVariable(command.Key, out var variable) && variable is
                {
                    Type: GameVariableDefinitions.Type.String
                } && !string.IsNullOrEmpty(variable.StringValue))
            {
                Capture(command.Value, command.Key, variable.StringValue, null);
            }
        }

        foreach (var saved in savedWaiting ?? [])
        {
            // the same command, still waiting for the same target, and nothing newer has named its character
            if (Waiting.TryGetValue(saved.Key, out var request) && request.Target == saved.Value[2] &&
                !request.ActorKnown)
            {
                ReadActor(request, saved.Value, 3);
                Main.Info($"DM command {request.Variable} = '{request.Target}': acting character taken from the " +
                          "save: " + (request.ActorKnown
                              ? $"acts for {request.ActorName ?? "?"}"
                              : $"no acting character: {request.ActorProblem}") + ".");
            }
        }

        // A dialogue, roll or teleport that was running when the game was saved can't be picked up again: its
        // variable was already cleared when it started, and nothing in the save says how far it got. Its status
        // would read Running for the rest of the campaign, so it is closed as Cancelled. (Leaving a location
        // closes them the same way, in DropRequests; this is the load.) A request of this location that is
        // running now is in Started.
        foreach (var command in Commands.Keys)
        {
            if (StringValueOf(variables, DungeonMakerNamesContext.Written(command + StatusSuffix)) ==
                StatusRunning &&
                Started.All(x => x.Variable != command))
            {
                WriteStatus(command, StatusCancelled);
                Main.Info($"DM command {command} cancelled: it was running when the game was saved.");
            }
        }
    }

    // GameLocationManager.UnloadLocationAsync: the location is going away, with its gadgets and characters.
    internal static void OnLocationUnloading()
    {
        DropRequests();
        Torches.Clear();
        _torchLocation = null;
        _operation = null;
        _updating = null;
        _runningSync = null;
        _armed = [];
        _dialogues = new ConditionalWeakTable<NarrativeSequence, Activation>();
        _narrativeDepth = 0;
        _narrativeRecord = null;
        NarrativeBefore.Clear();
        _causerSet = false;
        _causer = null;
        _causerHow = null;
        _causes = [];
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

    // A flow was just set off (an Area Activator's or pressure plate's, an interaction's, or one switched on by
    // another flow), and it has a Variable Operation on Teleport_Node: keep its place in the queue of
    // one-character teleports now. The game runs gadget flows in the order of its
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
    // set off after it. NoteFlowStarted then carries the record over to the flow when the game starts it.
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

        var activation = _standInFor != null
            ? StandInRecord(_standInFor, gadget, actor)
            : new Activation
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

    // The record of a dialogue that Dialog_Trigger opened through a stand-in (a conscious party member picked
    // because the request has no usable acting character). The stand-in is not the cause: the flow, its dialogue
    // and whatever they set off act for the request's acting character when it has one (it may be unconscious, or
    // gone, which the command it leads to then reports), and for nobody otherwise, with the request's own reason.
    // StandIn is set either way: the game hands the stand-in to the flow as its interacting character and passes it
    // along conditions and events, so "the one character the game passed along" (ResolveActor) would name the
    // stand-in, and must not be used for this chain.
    private static Activation StandInRecord(Request request, GameGadget gadget, GameLocationCharacter standIn)
    {
        var chain = ExtendChain(request.Chain, gadget.UniqueNameId);
        var origin = $"{DialogVariable} '{request.Target}'" +
                     (request.Origin != null ? $" (written by a chain started by {request.Origin})" : string.Empty) +
                     $", opened through {standIn?.Name ?? "a party member"}";

        if (request.ActorKnown)
        {
            return new Activation
            {
                MoverKnown = true,
                SoloTeleportEligible = CanTriggerSoloTeleport(FindCharacter(request.ActorGuid)),
                MoverGuid = request.ActorGuid,
                MoverName = request.ActorName ?? FindCharacter(request.ActorGuid)?.Name,
                StandIn = true,
                Origin = origin,
                Chain = chain
            };
        }

        return new Activation
        {
            Untraced = true,
            StandIn = true,
            Origin = $"{origin}, with no acting character ({request.ActorProblem ?? "none was known"})",
            OriginTerm = request.ProblemTerm ?? "Message/&DungeonMakerTriggerOther",
            Chain = chain
        };
    }

    // GameGadget.Update, around the whole of it: the game is updating this gadget. This is where the game starts a
    // gadget's triggered flows and runs their steps (functors), so "which flow is running" can be read from the
    // gadget while it lasts (RunningFlow). The returned token goes back to EndGadgetUpdate. Nothing of the game
    // is changed by either.
    internal static GameGadget BeginGadgetUpdate(GameGadget gadget)
    {
        var token = _updating;

        _updating = gadget;

        return token;
    }

    internal static void EndGadgetUpdate(GameGadget token)
    {
        _updating = token;
    }

    // GameGadget.ExecuteFunctorSequenceSync, around the whole of it: the game is running all the steps of one flow
    // of this gadget in one go (a flow that is not asynchronous, any flow during the fast travel simulation, and
    // the "location entered" flows the game forces after a load). The returned token goes back to EndSyncFlow.
    internal static GameGadgetListener BeginSyncFlow(GameGadget gadget, int listenerIndex)
    {
        var token = _runningSync;
        var listeners = gadget?.activeListeners;

        _runningSync = listeners != null && listenerIndex >= 0 && listenerIndex < listeners.Count
            ? listeners[listenerIndex]
            : null;

        return token;
    }

    internal static void EndSyncFlow(GameGadgetListener token)
    {
        _runningSync = token;
    }

    // The flow whose steps the game is running at this very moment, or null when it is running none.
    //
    // How the game runs a flow (GameGadget.Update, from its IL): a triggered flow whose conditions match is
    // acknowledged (AcknowledgeTrigger), then either run at once to its end (ExecuteFunctorSequenceSync) or
    // started as a coroutine kept in activeFunctorSequence, with currentListenerIndex saying which flow it is.
    // The coroutine is only ever stepped by GameGadget.Update (activeFunctorSequence.Run()), one stretch per
    // update, including whatever its steps wait on; a gadget runs one flow at a time. So:
    //   - inside ExecuteFunctorSequenceSync, it is that flow;
    //   - otherwise, inside the Update of a gadget that has a coroutine, it is the coroutine's flow. (While the
    //     coroutine exists the game does nothing else in Update than step it: it starts no other flow of the
    //     gadget. currentListenerIndex alone can't be used: the game sets it back to 0, not to "none".)
    // Outside a gadget's Update no flow is running: what happens then (a character moving, a dialogue's or a
    // quest's functor, this mod's own writes) is not a step of any flow.
    private static GameGadgetListener RunningFlow()
    {
        if (_runningSync != null)
        {
            return _runningSync;
        }

        var gadget = _updating;

        if (gadget?.activeFunctorSequence == null)
        {
            return null;
        }

        var listeners = gadget.activeListeners;
        var index = gadget.currentListenerIndex;

        return listeners != null && index >= 0 && index < listeners.Count ? listeners[index] : null;
    }

    // What is setting things off right now, for anything that becomes triggered or gets going meanwhile:
    //   - the actions of a dialogue step, if the game is carrying some out: the record of the dialogue (none when
    //     no gadget flow started it);
    //   - otherwise the gadget flow whose steps are running, with its record of who set it off (none when it was
    //     started by something that has no character: a timer nobody set going, the location loading...).
    // False when neither is going on: the change comes from a character moving, the clock, or code.
    private static bool TryCurrentSource(out Activation record, out GameGadgetListener flow)
    {
        flow = null;

        if (_narrativeDepth > 0)
        {
            record = _narrativeRecord;

            return true;
        }

        record = null;
        flow = RunningFlow();

        if (flow == null)
        {
            return false;
        }

        _runs.TryGetValue(flow, out record);

        return true;
    }

    // The record a flow gets when the source above switches it on: a copy of the source's record with the gadget
    // added to its chain, or, when the source has none, a record that says what the chain started with
    // (Untraced), so that a command written further down can say so.
    private static Activation Carry(Activation from, GameGadgetListener flow, string gadgetName)
    {
        if (from != null)
        {
            return from.Copy(ExtendChain(from.Chain, gadgetName));
        }

        if (flow == null)
        {
            // a dialogue no gadget flow started (a cutscene, or one started by code)
            return new Activation
            {
                Untraced = true,
                Origin = "a dialogue nobody's gadget flow started",
                OriginTerm = "Message/&DungeonMakerTriggerNoGadget",
                Chain = gadgetName
            };
        }

        var sourceName = GadgetNameOf(flow);

        return new Activation
        {
            Untraced = true,
            Origin = $"{DescribeTrigger(flow)} '{sourceName}'",
            OriginTerm = TriggerTerm(flow),
            Chain = ExtendChain(sourceName, gadgetName)
        };
    }

    // GameGadget.ListenerStatusChanged: a flow of this gadget tells the gadget that its trigger changed. Every
    // kind of flow does that through this one method when it becomes triggered (the gadget's constructor gives it
    // to each of its listeners as their ListenerStatusChanged handler; found by listing the 30 places that invoke
    // the handler), so this is the one place that sees "a flow became triggered" for all of them: a condition or
    // a gadget event (how the game passes an activation from flow to flow, see "FOLLOWING A CHAIN" at the top), a
    // variable's value or a quest step (a Variable or Quest Activator), a timer, damage, and the rest. The flow
    // then gets its record, whichever applies first:
    //   - a timer's: the record of the character whose flow set the timer going (_armed), if any. What happens
    //     to be running when the clock makes it fire is not its cause;
    //   - a flow about the gadget's bound character (damaged, spell or effect, tag revealed, stolen from): the
    //     character who did it, when the game named one (BeginCauser);
    //   - otherwise the record of what is setting things off right now (TryCurrentSource): the game calls this
    //     at once, inside the step that changes the condition, raises the event, writes the variable or advances
    //     the quest, so the running flow, or the dialogue step being carried out, is what switched it on.
    // The game copies the running flow's ActingCharacters along conditions and events only, and for an Area
    // Activator those are everyone standing in it, not the one who walked in. Nothing of that is changed here;
    // only the mod's own record travels.
    //
    // Not handled here: an Area Activator's, a pressure plate's and an interaction's flows, whose own hooks know
    // the character (NoteOccupationChanged, NoteInteracted).
    //
    // The mod's own variable writes carry nothing: a flow set off by one of them (a
    // Variable Activator on Roll_Trigger_Status or on a Clock_ variable, say) starts without a record, also when
    // the write happens to be made while some character's flow runs. A campaign's own flows writing those
    // variables are ordinary writes.
    //
    // The game calls this only when a flow becomes triggered, but some kinds call it again while they still are
    // (a timer, each second until its flow starts). The record of the first call stays: it is the trigger the
    // flow will run for, and the game runs it once.
    internal static void NoteListenerStatusChanged(GameGadgetListener listener)
    {
        if (listener == null || CurrentLocation == null ||
            listener is GameGadgetListenerVolumetricTrigger or GameGadgetListenerPressurePlate
                or GameGadgetListenerCharacterInteraction)
        {
            return;
        }

        if (!IsPending(listener))
        {
            // the trigger was taken back before its flow ran
            ForgetExpected(listener);
            _causes.Remove(listener);

            return;
        }

        if (_writing > 0 || _causes.TryGetValue(listener, out _))
        {
            return;
        }

        var gadgetName = GadgetNameOf(listener);
        Activation carried = null;

        if (listener is GameGadgetListenerElapsedTime or GameGadgetListenerHourUpdate)
        {
            if (_armed.TryGetValue(listener, out var armed))
            {
                carried = armed.Copy(ExtendChain(armed.Chain, gadgetName));
            }
        }
        else
        {
            if (_causerSet && listener is GameGadgetListenerDamaged or GameGadgetListenerAlterationInflicted
                    or GameGadgetListenerTagRevealed or GameGadgetListenerThiefDetected)
            {
                carried = CauserRecord(listener, gadgetName);
            }

            if (carried == null && TryCurrentSource(out var from, out var flow))
            {
                carried = Carry(from, flow, gadgetName);
            }
        }

        if (carried == null)
        {
            // Nothing is behind it that the mod follows (logging every such trigger here would log every torch
            // the game lights). If the flow then writes a command, ResolveActor says it has no acting character.
            return;
        }

        _causes.Add(listener, carried);
        ExpectSoloTeleport(listener, carried);
    }

    // The record for a flow set off by something done to the gadget's bound character, from the character the
    // game named as having done it: that character when it is a party member or a guest travelling with the
    // party, a record saying who it was otherwise (an enemy's hit can't act for a hero). Null when the game named
    // nobody (a trap, the environment): what is running then decides, as for any other flow.
    private static Activation CauserRecord(GameGadgetListener listener, string gadgetName)
    {
        var causer = _causer;

        if (causer == null)
        {
            return null;
        }

        var origin = $"{DescribeTrigger(listener)} '{gadgetName}' ({_causerHow} {causer.Name})";

        if (!CanTriggerSoloTeleport(causer))
        {
            return new Activation
            {
                Untraced = true,
                Origin = origin,
                OriginTerm = "Message/&DungeonMakerTriggerOther",
                Chain = gadgetName
            };
        }

        return new Activation
        {
            MoverKnown = true,
            SoloTeleportEligible = true,
            MoverGuid = causer.Guid,
            MoverName = causer.Name,
            Origin = origin,
            Chain = gadgetName
        };
    }

    // GameGadget.OnBoundCharacterDamaged, OnBoundCharacterAlterationInflicted, OnBoundCharacterTagRevealed and
    // OnBoundCharacterCharacterWasStolenItems, around each: the game tells a gadget what happened to the
    // character bound to it (a button or lever that can be attacked is a gadget with such a character). The game
    // hands these methods who did it (the damage's source id, the caster, the revealing character, the thief), but
    // passes only the amount, the kind or the tag on to the gadget's flows, so it is noted here for the flow that
    // becomes triggered inside (NoteListenerStatusChanged). The returned token goes back to EndCauser.
    internal static (bool, GameLocationCharacter, string) BeginCauser(GameLocationCharacter causer, string how)
    {
        var token = (_causerSet, _causer, _causerHow);

        _causerSet = true;
        _causer = causer;
        _causerHow = how;

        return token;
    }

    // the same, for the methods that get the character as a rules object or as its id
    internal static (bool, GameLocationCharacter, string) BeginCauser(ulong rulesetGuid, string how)
    {
        // a location character's id is the id of its rules object (GameLocationCharacter.Guid)
        return BeginCauser(rulesetGuid != 0 && CurrentLocation != null ? FindCharacter(rulesetGuid) : null, how);
    }

    internal static void EndCauser((bool, GameLocationCharacter, string) token)
    {
        (_causerSet, _causer, _causerHow) = token;
    }

    // GameGadget.SetCondition, after it changed a condition of this gadget: does that set one of the gadget's
    // timers going, or stop it?
    //
    // A timer flow ("after a time", "every N hours") counts only while the conditions it waits for hold
    // (GameGadgetListenerElapsedTime.RealTimeSecondTicked adds the seconds only when IsMatchingConditions; a Timed
    // Activator's timer waits for Enabled on and Triggered off). The game hands a timer's flow no characters at
    // all. So the character is kept here: when a condition change made by a character's flow completes the
    // conditions a timer waits for, that flow set the timer going, and its record is kept for the timer (_armed)
    // until the timer fires, which hands it on (NoteListenerStatusChanged).
    //
    // A kept record never outlives what it stands for:
    //   - set going again by something with no single known character (a flow nobody's move started, a group
    //     arriving, code): the record is dropped, not kept from the time before;
    //   - a condition it waits for stops holding (the timer is disabled, or has fired and set its own Triggered
    //     condition): dropped;
    //   - the location is left: dropped with everything else.
    // A repeating Timed Activator keeps its character from one round to the next: its own "reacting to trigger"
    // flow, which acts for that character, switches Triggered off again and resets the time.
    //
    // Only the conditions are compared, with the gadget's own values. The game's IsMatchingConditions is not
    // called: it also rebuilds the flow's list of involved characters.
    internal static void NoteConditionSet(GameGadget gadget, int conditionIndex, bool? before, bool state)
    {
        var listeners = gadget?.activeListeners;

        if (listeners == null || before == state || CurrentLocation == null)
        {
            return;
        }

        var states = gadget.currentConditionStates;

        foreach (var listener in listeners)
        {
            if (listener is not (GameGadgetListenerElapsedTime or GameGadgetListenerHourUpdate) ||
                listener.ExpectedConditionStates is not { } expected ||
                !expected.TryGetValue(conditionIndex, out var wanted))
            {
                continue;
            }

            if (wanted != state)
            {
                _armed.Remove(listener);
            }
            else if (expected.All(x =>
                         x.Key == conditionIndex || (states.TryGetValue(x.Key, out var now) && now == x.Value)))
            {
                Arm(listener);
            }
        }
    }

    // FunctorResetTimeLapse.Execute (the "reset time lapse" step a repeating Timed Activator ends its round
    // with): the step sets every "after a time" timer of its own gadget back to zero, so whoever's flow runs it
    // sets those timers going. Noted when the game builds the step, inside the flow that runs it.
    internal static void NoteTimersReset(FunctorParametersDescription parameters)
    {
        var listeners = parameters?.SourceGadget ? parameters.SourceGadget.GameGadget?.activeListeners : null;

        if (listeners == null || CurrentLocation == null)
        {
            return;
        }

        foreach (var listener in listeners)
        {
            if (listener is GameGadgetListenerElapsedTime)
            {
                Arm(listener);
            }
        }
    }

    // keeps the record of what is setting things off now for this timer, or drops the one it had (see
    // NoteConditionSet for the rule)
    private static void Arm(GameGadgetListener listener)
    {
        _armed.Remove(listener);

        if (_writing == 0 && TryCurrentSource(out var from, out _) &&
            from is { MoverKnown: true, Untraced: false, Group: false })
        {
            _armed.Add(listener, from.Copy(from.Chain));
        }
    }

    // NarrativeDirectionManager.PrepareDialogSequence and PrepareObservationSequence, after they built a dialogue
    // (or an observation) for the Dungeon Maker's or the game's "start dialogue" step: if a gadget flow is doing
    // that, the dialogue belongs to whoever set that flow off (the character who talked to the NPC, who walked
    // into the area that starts it, or the acting character of Dialog_Trigger, which starts it through an
    // interaction).
    //
    // Why the flow's record and not a speaker: the game gives the dialogue the party members it picked for it
    // (all those nearby, sorted), and hands out the speaking roles by personality (NarrativeState.
    // SortByPersonalityFlagRelevance), so who speaks a line says nothing about who started the talk. The one thing
    // that does is the flow that started it.
    internal static void NoteSequencePrepared(NarrativeSequence sequence)
    {
        if (sequence == null || CurrentLocation == null)
        {
            return;
        }

        _dialogues.Remove(sequence);

        if (TryCurrentSource(out var from, out var flow))
        {
            _dialogues.Add(sequence, Carry(from, flow, null));
        }
    }

    // NarrativeState.ExecuteFunctors, around the whole of it: a dialogue step ends and the game carries out its
    // actions (set a variable, advance a quest, give an item...; the only place a dialogue runs functors). What
    // they set off acts for whoever the dialogue belongs to (TryCurrentSource).
    internal static void BeginNarrativeActions(NarrativeState state)
    {
        var sequence = state?.NarrativeSequence;

        // what was current goes back when this step's actions are done (EndNarrativeActions)
        NarrativeBefore.Push(_narrativeRecord);
        _narrativeDepth++;
        _narrativeRecord = sequence != null && _dialogues.TryGetValue(sequence, out var record) ? record : null;
    }

    internal static void EndNarrativeActions()
    {
        if (NarrativeBefore.Count == 0)
        {
            return;
        }

        _narrativeRecord = NarrativeBefore.Pop();
        _narrativeDepth = NarrativeBefore.Count;
    }

    // A chain's gadget names with one more gadget at the end. Steps inside one gadget don't repeat its name
    // ("ActivatorArea22 > VariableOperation19", not the area three times), and a very long chain loses its front.
    private static string ExtendChain(string chain, string gadgetName)
    {
        if (string.IsNullOrEmpty(chain))
        {
            return gadgetName;
        }

        if (string.IsNullOrEmpty(gadgetName))
        {
            return chain;
        }

        const string Separator = " > ";

        if (chain == gadgetName || chain.EndsWith(Separator + gadgetName, StringComparison.Ordinal))
        {
            return chain;
        }

        var grown = chain + Separator + gadgetName;

        if (grown.Length <= MaxChainLength)
        {
            return grown;
        }

        // keep the end, from a whole gadget name on
        var cut = grown.IndexOf(Separator, grown.Length - MaxChainLength, StringComparison.Ordinal);

        return "..." + (cut >= 0 ? grown.Substring(cut) : Separator + gadgetName);
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
            GameGadgetListenerGadgetEvent => "a gadget event of",
            GameGadgetListenerDamaged => "damage to the character of",
            GameGadgetListenerAlterationInflicted => "a spell or effect on the character of",
            GameGadgetListenerTagRevealed => "a tag revealed on the character of",
            GameGadgetListenerThiefDetected => "a theft from the character of",
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

    // AcknowledgeTrigger of every kind of flow: the game is about to run the flow for the trigger it noted
    // (GameGadget.Update and ForceLocationEntered call it right before they start the flow's steps). From here
    // until its next start, the flow's steps act for whoever caused this trigger. A flow started without a record
    // behind it (a trigger kept in a save whose record could not be restored, a timer nobody set going) gets none,
    // rather than inheriting the one of an older run.
    internal static void NoteFlowStarted(GameGadgetListener listener)
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
        // day and night of the automatic layer: a switch is a change of Torches_Auto's decision everywhere
        var lit = AutoLitPeriod();

        if (_autoLitPeriod != null && lit != null && lit != _autoLitPeriod)
        {
            ForgetTorchesElsewhere(TorchesAutoVariable);
        }

        _autoLitPeriod = lit ?? _autoLitPeriod;

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

        // a command or request name this campaign uses for something else (see DungeonMakerNamesContext)
        if (DungeonMakerNamesContext.IsOwnRequest(name))
        {
            return;
        }

        if (DungeonMakerNamesContext.WrongType(name, variable.Type))
        {
            DungeonMakerNamesContext.MarkOwnRequest(name,
                $"it is a {variable.Type} variable, which this command never is");

            return;
        }

        if (Commands.TryGetValue(name, out var kind))
        {
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
            ForgetTorchesElsewhere(name);
        }
        else
        {
            DungeonMakerClockContext.OnVariableChanged(name);
        }
    }

    #endregion

    #region Saves

    // GameVariableManager.SerializeElements in write mode, before it runs: the game is about to write the campaign
    // variables into a save (or into the copy of the game a multiplayer host sends). What is waiting is put into
    // the mod's variable for that moment only, so it is in the save and nowhere else: AfterVariablesSaved takes
    // it out again, and the game being played is exactly as it was (the same on a computer that saves and on one
    // that doesn't). Returns whether it did, so that the patch knows to call AfterVariablesSaved.
    internal static bool BeforeVariablesSaved()
    {
        try
        {
            var variables = ServiceRepository.GetService<IGameVariableService>();

            if (variables == null || CurrentLocation == null)
            {
                return false;
            }

            var text = SaveState();

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            // The first of Commands_SavedState, Commands_SavedState_2, ... that is not a variable of the campaign, so
            // the mod's variable never overwrites a campaign's. It is made here
            // and taken out again by AfterVariablesSaved, so it is always the mod's own.
            var name = SavedStateName(0);

            for (var i = 1; i < SavedStateNames && variables.IsVariableRegistered(name); i++)
            {
                name = SavedStateName(i);
            }

            if (variables.IsVariableRegistered(name))
            {
                return false;
            }

            _writing++;

            try
            {
                variables.RegisterVariable(name, GameVariableDefinitions.Scope.Campaign, text);
            }
            finally
            {
                _writing--;
            }

            _savedStateMade = name;

            return true;
        }
        catch (Exception e)
        {
            // a save must never fail because of this
            Main.Error($"DM commands: could not keep the waiting commands in the save: {e.Message}");

            return false;
        }
    }

    internal static void AfterVariablesSaved()
    {
        try
        {
            if (_savedStateMade != null)
            {
                Unregister(ServiceRepository.GetService<IGameVariableService>(), _savedStateMade);
            }
        }
        catch (Exception e)
        {
            Main.Error($"DM commands: could not remove the saved commands variable: {e.Message}");
        }

        _savedStateMade = null;
    }

    // Commands_SavedState, then Commands_SavedState_2, _3, ...
    private static string SavedStateName(int index)
    {
        return index == 0 ? SavedStateVariable : $"{SavedStateVariable}_{index + 1}";
    }

    // takes one of the mod's own variables out of the campaign's variables
    private static void Unregister(IGameVariableService variables, string name)
    {
        if (variables == null || !variables.IsVariableRegistered(name))
        {
            return;
        }

        _writing++;

        try
        {
            variables.UnregisterVariable(name);
        }
        finally
        {
            _writing--;
        }
    }

    // After a load: finds the mod's saved-state variable among Commands_SavedState, Commands_SavedState_2, ...,
    // takes it out of the campaign's variables and returns its text, or null when the save has none. Only a
    // String variable whose text starts with the mod's signature is the mod's; a campaign's own variable of one
    // of these names is neither read nor touched.
    private static string TakeSavedState(IGameVariableService variables)
    {
        for (var i = 0; i < SavedStateNames; i++)
        {
            var name = SavedStateName(i);

            if (!variables.IsVariableRegistered(name))
            {
                // the mod took the first free name when it saved, so there is none beyond a free one
                return null;
            }

            var text = StringValueOf(variables, name);

            if (text == null || !text.StartsWith(SavedStateSignature + ";", StringComparison.Ordinal))
            {
                continue;
            }

            Unregister(variables, name);

            return text;
        }

        return null;
    }

    // What is waiting, as one line of text, or "" when nothing is. Entries are separated by ';' and their fields
    // by ','; every field is escaped (Uri.EscapeDataString), so names can hold anything. It starts with the mod's
    // signature and the format's version. A flow is named by its gadget's unique name and its flow id, which is how the
    // game itself names them in a save (GameSector.SerializeElements, GameGadget.flowsSaveData); a character by its id,
    // which the game saves with it (RulesetEntity's "Guid" attribute).
    //   C gadget flow record   a flow that is triggered and has not started, with who is behind it
    //   A gadget flow record   a timer a character's flow set going
    //   W variable target actor   a waiting Dialog_Trigger, Roll_Trigger or Teleport_PartyNode
    //   T target actor         a waiting one-character teleport, in the queue's order
    //   P gadget flow          a place kept in that queue for a flow of C, at its place in the order
    //   O variable             a command or request name the mod has found to be the campaign's own variable
    //                          (DungeonMakerNamesContext.MarkOwnRequest). It decides whether a later write is a
    //                          command, so a computer that loads or joins the game must know it as well as one that
    //                          saw it happen, or the two would act differently on the same write.
    //   L key location state decider   what was last applied to a torch (TorchStates), in any location of the game
    // Not kept: flows that were running (the game doesn't resume them after a load, so their record and the
    // places they kept have nothing to go with), and commands already handed to the game (cancelled on load).
    private static string SaveState()
    {
        var entries = new List<string>();

        foreach (var pair in _causes)
        {
            if (IsPending(pair.Key) && FlowKey(pair.Key, out var gadget, out var flow))
            {
                entries.Add(Pack(["C", gadget, flow, .. RecordFields(pair.Value)]));
            }
        }

        foreach (var pair in _armed)
        {
            if (FlowKey(pair.Key, out var gadget, out var flow))
            {
                entries.Add(Pack(["A", gadget, flow, .. RecordFields(pair.Value)]));
            }
        }

        foreach (var request in Waiting.Values)
        {
            entries.Add(Pack(["W", request.Variable, request.Target, .. ActorFields(request)]));
        }

        foreach (var request in SoloTeleports)
        {
            if (!request.Expected)
            {
                entries.Add(Pack(["T", request.Target, .. ActorFields(request)]));
            }
            else if (request.Listener is { } listener && IsPending(listener) &&
                     _causes.TryGetValue(listener, out var cause) && ReferenceEquals(cause.Expected, request) &&
                     FlowKey(listener, out var gadget, out var flow))
            {
                entries.Add(Pack(["P", gadget, flow]));
            }
        }

        foreach (var name in DungeonMakerNamesContext.OwnRequestNames.OrderBy(x => x, StringComparer.Ordinal))
        {
            entries.Add(Pack(["O", name]));
        }

        foreach (var torch in TorchStates.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            entries.Add(Pack(["L", torch.Key, torch.Value.Location, torch.Value.Applied.ToString(),
                torch.Value.Decider]));
        }

        return entries.Count == 0
            ? string.Empty
            : SavedStateSignature + ";" + SavedStateVersion + ";" + string.Join(";", entries);
    }

    // PrepareLocation, after a load and before the location's first gadget update: takes the saved state out of
    // the campaign's variables (so it is in a save only) and puts back what still fits the loaded game. Returns the
    // waiting commands' entries by variable, for PrepareLocation to apply once it has taken the commands
    // themselves.
    //
    // Nothing stale is put back: a record only for a flow that exists and is still triggered or has the game's
    // "pending trigger kept in the save" flag (the game starts exactly those after a load; an area's or a plate's
    // is not kept by the game, which works out again who stands in it); a gadget name that more than one gadget
    // has names none; a kept place only for a record that was put back. A character who is gone by the time the
    // command runs fails it there, as always (ValidActor).
    private static Dictionary<string, string[]> RestoreState(IGameVariableService variables, GameLocation location)
    {
        var waiting = new Dictionary<string, string[]>();
        var entries = (TakeSavedState(variables) ?? string.Empty).Split(';');

        if (entries.Length < 3 || entries[0] != SavedStateSignature || entries[1] != SavedStateVersion)
        {
            return waiting;
        }

        var gadgets = GadgetsById().Values
            .Where(x => x?.UniqueNameId != null)
            .GroupBy(x => x.UniqueNameId)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.First());
        var fields = entries.Skip(2).Select(Unpack).Where(x => x.Length > 0).ToList();
        var restored = 0;

        GameGadgetListener Find(string gadgetName, string flowId)
        {
            return gadgets.TryGetValue(gadgetName, out var gadget)
                ? gadget.activeListeners?.FirstOrDefault(x => x != null && x.FlowId == flowId)
                : null;
        }

        // records first: the queue's kept places refer to them
        foreach (var entry in fields.Where(x => x[0] is "C" or "A" && HoldsRecord(x)))
        {
            var listener = Find(entry[1], entry[2]);

            if (listener == null)
            {
                continue;
            }

            var record = ReadRecord(entry, 3);

            if (entry[0] == "A")
            {
                if (listener is not (GameGadgetListenerElapsedTime or GameGadgetListenerHourUpdate))
                {
                    continue;
                }

                _armed[listener] = record;
                restored++;

                // a timer that came due while the game loaded has fired without its record: it gets it now
                if (!listener.IsTriggered || _causes.ContainsKey(listener))
                {
                    continue;
                }

                record = record.Copy(ExtendChain(record.Chain, entry[1]));
            }
            else if (!IsPending(listener) || _causes.ContainsKey(listener))
            {
                continue;
            }

            _causes.Add(listener, record);
            restored++;
        }

        foreach (var entry in fields)
        {
            switch (entry[0])
            {
                case "W" when entry.Length == 3 + ActorFieldCount:
                    waiting[entry[1]] = entry;
                    break;
                case "T" when entry.Length == 2 + ActorFieldCount:
                    var request = new Request
                    {
                        Kind = Kind.TeleportOne,
                        Variable = TeleportNodeVariable,
                        Location = location,
                        Target = entry[1],
                        Serial = ++_serial,
                        IssuedAt = GameSeconds()
                    };

                    ReadActor(request, entry, 2);
                    NewestSerial[TeleportNodeVariable] = request.Serial;
                    SoloTeleports.Add(request);
                    restored++;
                    break;
                case "O" when entry.Length == 2:
                    DungeonMakerNamesContext.MarkOwnRequest(entry[1],
                        "it was the campaign's own variable when the game was saved");
                    restored++;
                    break;
                case "L" when entry.Length == 5 && entry[3] is "1" or "2":
                    TorchStates[entry[1]] = new TorchState
                    {
                        Location = entry[2], Applied = entry[3] == "1" ? 1 : 2, Decider = Text(entry[4])
                    };
                    restored++;
                    break;
                case "P" when entry.Length == 3:
                    if (Find(entry[1], entry[2]) is { } listener && _causes.TryGetValue(listener, out var cause) &&
                        cause.Expected == null)
                    {
                        ExpectSoloTeleport(listener, cause);
                    }

                    break;
            }
        }

        Main.Info($"DM commands: {restored} waiting record(s) and teleport(s) put back from the save, " +
                  $"{waiting.Count} waiting command(s) to match.");

        return waiting;
    }

    // Is this flow waiting to be started by the game? Either its trigger is set, or it was set when the game was
    // saved: GameGadget.PostLoad puts that back as PendingSavedTrigger, without setting the listener's own trigger,
    // and the game's own save keeps a flow when either is set (read in the IL). Saving,
    // restoring and the teleport queue's kept places all ask this, so a chain loaded from a save, then saved again
    // before it has run, keeps who is behind it.
    private static bool IsPending(GameGadgetListener listener)
    {
        return listener != null && (listener.IsTriggered || listener.PendingSavedTrigger);
    }

    // the gadget's unique name and the flow's id, when the flow has both
    private static bool FlowKey(GameGadgetListener listener, out string gadget, out string flow)
    {
        gadget = listener?.SourceGadget ? listener.SourceGadget.GameGadget?.UniqueNameId : null;
        flow = listener?.FlowId;

        return !string.IsNullOrEmpty(gadget) && !string.IsNullOrEmpty(flow);
    }

    private static string Pack(string[] fields)
    {
        return string.Join(",", fields.Select(x => Uri.EscapeDataString(x ?? string.Empty)));
    }

    private static string[] Unpack(string entry)
    {
        return string.IsNullOrEmpty(entry) ? [] : entry.Split(',').Select(Uri.UnescapeDataString).ToArray();
    }

    private static string Flag(bool value)
    {
        return value ? "1" : "0";
    }

    private static string Text(string field)
    {
        return string.IsNullOrEmpty(field) ? null : field;
    }

    private static ulong Id(string field)
    {
        return ulong.TryParse(field, out var id) ? id : 0;
    }

    // A record's fields. A record entry may stop before its last field (StandIn), which then reads as not set.
    private const int RecordFieldCount = 10;

    private static string[] RecordFields(Activation record)
    {
        return
        [
            Flag(record.MoverKnown), record.MoverGuid.ToString(), record.MoverName,
            Flag(record.SoloTeleportEligible), Flag(record.Group), Flag(record.Untraced), record.Origin,
            record.OriginTerm, record.Chain, Flag(record.StandIn)
        ];
    }

    // whether an entry of this kind holds a whole record from field 3 on (with or without its last field)
    private static bool HoldsRecord(string[] entry)
    {
        return entry.Length == 3 + RecordFieldCount || entry.Length == 3 + RecordFieldCount - 1;
    }

    private static Activation ReadRecord(string[] entry, int at)
    {
        return new Activation
        {
            MoverKnown = entry[at] == "1",
            MoverGuid = Id(entry[at + 1]),
            MoverName = Text(entry[at + 2]),
            SoloTeleportEligible = entry[at + 3] == "1",
            Group = entry[at + 4] == "1",
            Untraced = entry[at + 5] == "1",
            Origin = Text(entry[at + 6]),
            OriginTerm = Text(entry[at + 7]),
            Chain = Text(entry[at + 8]),
            StandIn = entry.Length > at + 9 && entry[at + 9] == "1"
        };
    }

    private const int ActorFieldCount = 8;

    private static string[] ActorFields(Request request)
    {
        return
        [
            Flag(request.ActorKnown), request.ActorGuid.ToString(), request.ActorName, request.ActorProblem,
            request.ProblemTerm, request.Chain, request.Origin, request.ActorNote
        ];
    }

    private static void ReadActor(Request request, string[] entry, int at)
    {
        request.ActorKnown = entry[at] == "1";
        request.ActorGuid = Id(entry[at + 1]);
        request.ActorName = Text(entry[at + 2]);
        request.ActorProblem = Text(entry[at + 3]);
        request.ProblemTerm = Text(entry[at + 4]);
        request.Chain = Text(entry[at + 5]);
        request.Origin = Text(entry[at + 6]);
        request.ActorNote = Text(entry[at + 7]);
    }

    #endregion

    #region Requests

    // Takes a write of a command variable: "" cancels what was waiting, anything else replaces it. For
    // Teleport_Node nothing is replaced: the request joins the queue.
    private static void Capture(Kind kind, string variableName, string value, FunctorParametersDescription operation)
    {
        if (DungeonMakerNamesContext.IsOwnRequest(variableName))
        {
            return;
        }

        // A text that names nothing, here or anywhere in the campaign, was never meant as a command: the campaign
        // uses this name for something of its own. Nothing is done with it, and nothing of an earlier command
        // either.
        if (!string.IsNullOrEmpty(value) && NamesNothing(kind, variableName, value))
        {
            DungeonMakerNamesContext.MarkOwnRequest(variableName,
                $"the campaign wrote '{value}' into it, and nothing in the campaign goes by that name");

            return;
        }

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

    // Can this text never be the target of this command? True only when nothing in the loaded location goes by
    // the name now (looked up as the command itself would) and nothing in the campaign's data does either (every
    // location's gadgets, and for a dialogue the campaign's NPCs, so something that is not there yet, such as an
    // NPC that spawns later or a gadget of another location, still counts as a name). Without campaign data
    // (the game's own campaigns) it is never true, and the command waits and fails as before.
    private static bool NamesNothing(Kind kind, string variableName, string value)
    {
        var location = CurrentLocation;

        if (location == null)
        {
            return false;
        }

        try
        {
            var here = kind is Kind.Dialog or Kind.Roll
                ? FindInteractionTargets(value).Count > 0
                : WorldGadgets().Any(x => Same(x.name, value) || Same(x.GameGadget?.UniqueNameId, value));

            return !here &&
                   !DungeonMakerNamesContext.CampaignKnows(variableName, value, location.UserCampaign,
                       location.UserLocation, out var hasData) && hasData;
        }
        catch (Exception)
        {
            // when in doubt it is a command
            return false;
        }
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

        if (!IsPending(listener))
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

            // A dialogue Dialog_Trigger opened through a stand-in for a request with no acting character
            // (StandInRecord): the character the game hands the flow is the stand-in, so it isn't used.
            case GameGadgetListenerCharacterInteraction
                when _runs.TryGetValue(listener, out var opened) && opened.StandIn:
                request.Chain = opened.Chain;
                request.Origin = opened.Origin;
                request.ActorProblem = $"the chain {opened.Chain} was started by {opened.Origin}";
                request.ProblemTerm = opened.OriginTerm;
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

            // Any other flow, and a dialogue's action. A flow switched on by another flow (through a condition, a
            // gadget event, a variable, a quest step), by a dialogue, by a timer a character's flow set going, or
            // by what a character did to the gadget's bound character has the record that was carried to it
            // (NoteListenerStatusChanged), so it acts for whoever set the first flow off.
            default:
            {
                Activation chained = null;

                if (listener != null)
                {
                    _runs.TryGetValue(listener, out chained);
                }
                else if (_narrativeDepth > 0)
                {
                    // written by a dialogue step's own action: no gadget flow, but the dialogue has a record
                    chained = _narrativeRecord ?? Carry(null, null, null);
                }

                // The game refills a flow's list of characters at each trigger only for condition and gadget
                // event flows (it copies the list of the flow that switched them on). The other kinds keep
                // whatever an earlier run left in it, so there the list says nothing.
                var passedMeans = listener is GameGadgetListenerConditionToggle or GameGadgetListenerGadgetEvent;

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
                else if (passedMeans && heroes.Count == 1 && singleActorEligible && chained is not { StandIn: true })
                {
                    // The start of the chain wasn't traced to one character (the party arriving together, a flow
                    // kept in a save, a change the mod doesn't follow), but the game passed exactly one eligible
                    // character along the chain: that one acts, by the same rule as an area the party arrives in.
                    // A timer, a variable or a quest passes nobody, so a chain they start never gets here. Not for
                    // a chain started by a dialogue opened through a stand-in: the character the game passes along
                    // it is the stand-in the mod picked (StandInRecord).
                    request.ActorGuid = heroes[0];
                    request.ActorKnown = true;
                    request.ActorNote = "the start of the chain was not traced to one character; the one eligible " +
                                        "character the game passed along the chain acts";
                }
                else if (chained == null && listener == null)
                {
                    request.ActorProblem = "the object that set the variable could not be identified";
                    request.ProblemTerm = TriggerTerm(null);
                }
                else if (chained == null && passedMeans)
                {
                    request.ActorProblem = $"'{gadgetName}' was switched on by a change made outside any gadget " +
                                           "flow (a quest, code, a trigger kept in a save), so no " +
                                           "character is tied to it";
                    request.ProblemTerm = "Message/&DungeonMakerTriggerOther";
                }
                else if (chained == null)
                {
                    request.ActorProblem = $"it was set from {DescribeTrigger(listener)} '{gadgetName}', which " +
                                           "no character's flow set off";
                    request.ProblemTerm = TriggerTerm(listener);
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
            }
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

        // true when the character opening the dialogue is a stand-in rather than the request's acting character
        var standIn = false;

        if (actor == null)
        {
            if (isRoll)
            {
                Finish(request, StatusFailed, $"a roll needs one particular character, but {problem}");

                return;
            }

            // A dialogue belongs to the party, so anyone conscious can open it. The game needs a character to
            // interact with the gadget; the one picked here only opens the dialogue and is not the acting
            // character of what the dialogue sets off (NoteInteracted).
            actor = FirstConsciousHero();
            standIn = true;

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
            $"DM command {request.Variable}: starting the {what} of '{gadget.UniqueNameId}' (flow {request.Flow}) " +
            (standIn
                ? $"through {actor.Name}, who is not its acting character ({problem ?? "none"})."
                : $"for {actor.Name}."));

        // Interact marks the flow as triggered and the gadget as dirty; the game's gadget update runs it. With a
        // stand-in, the postfix (NoteInteracted) records the request's own cause for the flow instead of the
        // stand-in.
        _standInFor = standIn ? request : null;

        try
        {
            gadget.Interact(request.Flow, actor);
        }
        finally
        {
            _standInFor = null;
        }

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

    // The game's alert, in the failure colour; nothing when no screen can show it (loading). Every error the
    // commands, the torches and the clock show in game comes through here.
    //
    // PLAYTEST: while the location or campaign map being played is a
    // Dungeon Maker playtest (the session service's PlaytestingUserLocation / PlaytestingUserCampaignMap, set by
    // the editors' Playtest buttons), the alert starts with the game's own word for Playtest (its term
    // MainMenu/&UserLocationsPlaytestTitle, so it reads right in every language), and the same text is added to
    // the game's log (the console at the bottom of the screen) as a line of its own, in the log's failure
    // colour. Outside a playtest the alert is as before and nothing goes into the log.
    //
    // MULTIPLAYER: callers run inside the gadget update, which every computer runs in step. Showing a message is
    // the one part that depends on each computer's own screen, so it may never throw into its caller: an
    // exception on one computer only would skip the rest of that caller's work there (a torch pass, for instance)
    // and leave the computers different. Hence the catch around all of it, whoever calls.
    internal static void ShowFailureAlert(string text)
    {
        try
        {
            if (!IsPlaytest())
            {
                Gui.GuiService?.ShowAlert(text, Gui.ColorFailure, 6f);

                return;
            }

            var prefix = Gui.Localize(PlaytestTitleTerm);

            Gui.GuiService?.ShowAlert(Gui.Format(PlaytestAlertTerm, prefix, text), Gui.ColorFailure, 6f);
            AddPlaytestLogLine(prefix, text);
        }
        catch (Exception e)
        {
            Main.Error(e);
        }
    }

    // The same for a message made from a term (and its arguments): the term is looked up inside the guard too,
    // so a failing lookup can't escape either (the torch messages use this).
    private static void ShowFailureAlertTerm(string term, params object[] args)
    {
        string text;

        try
        {
            text = args.Length == 0 ? Gui.Localize(term) : Gui.Format(term, args.Select(x => $"{x}").ToArray());
        }
        catch (Exception e)
        {
            Main.Error(e);

            return;
        }

        ShowFailureAlert(text);
    }

    private const string PlaytestTitleTerm = "MainMenu/&UserLocationsPlaytestTitle";

    // "{0}: {1}": the playtest word and the error
    private const string PlaytestAlertTerm = "Message/&DungeonMakerPlaytestAlert";

    // The game's log reads "{x}" as a parameter's place also inside a parameter's own text: after putting the
    // parameters in, GameRecordEntry.BreakdownFragments asks TextBreaker.HasUnassignedParameters again, which
    // takes any word whose first '}' comes two characters after its first '{' as parameter number x (read in the
    // IL). An error naming a gadget "{0}" would then show the prefix in its
    // place, "{1}" would put the error into itself again and again, and "{9}" would ask for a parameter that isn't
    // there. A zero-width space after every '{' and before every '}' keeps the text as it reads, and no '}' can
    // then be two characters after a '{' (not even in "{}", which becomes four characters).
    private const char ZeroWidthSpace = '\u200B';

    private static string NoPlaceholders(string text)
    {
        return string.IsNullOrEmpty(text)
            ? text
            : text.Replace("{", "{" + ZeroWidthSpace).Replace("}", ZeroWidthSpace + "}");
    }

    private const char FullWidthAmpersand = '\uFF06';

    // "{0}:": the playtest word with its colon, the log line's first word
    private const string PlaytestPrefixTerm = "Feedback/&DungeonMakerPlaytestPrefix";

    private static bool IsPlaytest()
    {
        var session = ServiceRepository.GetService<ISessionService>();

        return session != null && (session.PlaytestingUserLocation || session.PlaytestingUserCampaignMap);
    }

    private static void AddPlaytestLogLine(string prefix, string text)
    {
        try
        {
            var console = Gui.Game?.GameConsole;

            if (console == null)
            {
                return;
            }

            // The error is the line itself, in the failure colour, and the prefix with its colon is its one
            // parameter. The log breaks only its base line into words (at spaces and line breaks), and puts a
            // parameter's text in as one piece that is never broken: with the error as a parameter a long error
            // would run past the log panel's edge instead of wrapping, and in "{0}: {1}" the colon would be a word of
            // its own, shown as "Playtest :". The log runs its base line through
            // Gui.Localize, which gives back a text that is no term unchanged unless the text holds a '&'
            // (Gui.LocalizeImpl, IL_0043-005a: it then looks for a term and logs a missing one), so a '&' in the
            // error is shown as a full-width one. Its braces are kept from being read as parameters as above.
            var line = "{0} " + NoPlaceholders(text).Replace('&', FullWidthAmpersand);
            var entry = new GameConsoleEntry(line, console.consoleTableDefinition,
                baseType: ConsoleStyleDuplet.ParameterType.Negative);

            entry.AddParameter(ConsoleStyleDuplet.ParameterType.Base, Gui.Format(PlaytestPrefixTerm, prefix));
            console.AddEntry(entry);
        }
        catch (Exception e)
        {
            // the log line is a help to the author; failing to add it must not stop the alert's caller
            Main.Error(e);
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
        // to the name the mod writes to in this campaign (another one when the campaign owns that name)
        WriteString(DungeonMakerNamesContext.Written(commandVariable + StatusSuffix), status);
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
    // looked at again only when a torch variable is written or the hour changes, not every few frames. What was
    // applied to each torch is kept in TorchStates for the whole game, so entering the location again or loading
    // a save applies only what has changed since (see ApplyTorch).
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
                    Key = TorchKey(location, world.name),
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
                ShowFailureAlertTerm("Message/&DungeonMakerTorchesNone");
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

            // the strongest layer with an opinion decides, and is noted with what it decided
            notBuilt |= own is 1 or 2
                ? !ApplyTorch(torch, own, torch.Variable)
                : manual is 1 or 2
                    ? !ApplyTorch(torch, manual, TorchesVariable)
                    : !ApplyTorch(torch, auto, TorchesAutoVariable);
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

    // whether the automatic layer asks for lit torches at this hour (on an outdoor map, with Torches_Auto on)
    private static bool? AutoLitPeriod()
    {
        var hour = Gui.GameCampaign?.GameTime?.TimeInfo?.Hour;

        return hour == null ? null : hour >= AutoLitFromHour || hour < AutoOutFromHour;
    }

    // a torch's entry in TorchStates: the location (its Dungeon Maker title, or the game's location definition)
    // and the torch's gadget name
    private static string TorchKey(GameLocation location, string gadgetName)
    {
        return TorchLocationKey(location) + "\n" + gadgetName;
    }

    private static string TorchLocationKey(GameLocation location)
    {
        return location?.UserLocation?.Title ??
               (location?.LocationDefinition ? location.LocationDefinition.Name : string.Empty);
    }

    // A torch variable changed (OnVariableChanged), or the automatic layer switched between day and night
    // (NoteHourChanged). The torches of the current location see that change at the next gadget update
    // (ApplyTorches). Those of other locations are not loaded, so their entry is forgotten when that variable was
    // what decided their state: on the next visit, what the variables then ask for is applied again, as it would
    // have been had the change been seen there. A change of a layer below the deciding one leaves the entry.
    private static void ForgetTorchesElsewhere(string decider)
    {
        if (TorchStates.Count == 0)
        {
            return;
        }

        var here = TorchLocationKey(CurrentLocation);

        foreach (var key in TorchStates
                     .Where(x => x.Value.Decider == decider && x.Value.Location != here)
                     .Select(x => x.Key)
                     .ToArray())
        {
            TorchStates.Remove(key);
        }
    }

    // Brings one torch to the wanted state, once per change: a torch a player toggles afterwards stays as the
    // player left it until the variable that decides it changes again, also over leaving the location and over a
    // save and load (TorchStates). Returns false when the torch's game object does not exist yet.
    private static bool ApplyTorch(Torch torch, int desired, string decider)
    {
        if (desired is not (1 or 2))
        {
            // no opinion any more: forget it so that a later state is applied again
            TorchStates.Remove(torch.Key);

            return true;
        }

        var gadget = torch.World.GameGadget;

        if (gadget == null)
        {
            return false;
        }

        if (TorchStates.TryGetValue(torch.Key, out var last) && last.Applied == desired)
        {
            // the same state, possibly decided by another layer now: the change that counts from here is that
            // layer's
            last.Decider = decider;

            return true;
        }

        TorchStates[torch.Key] = new TorchState
        {
            Location = TorchLocationKey(CurrentLocation), Applied = desired, Decider = decider
        };

        if (torch.LitIndex < 0)
        {
            if (!torch.Reported)
            {
                torch.Reported = true;
                ShowFailureAlertTerm("Message/&DungeonMakerTorchNoCondition", torch.World.name);
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
    // switched its trigger on. A flow switched on by the flow this started gets a copy, with its gadget added to
    // Chain (see NoteListenerStatusChanged).
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

        // the chain started with a dialogue Dialog_Trigger opened through a stand-in party member, so the characters
        // the game passes along it name the stand-in and are not used to find an acting character (StandInRecord)
        internal bool StandIn;

        // The same activation for the next flow of the chain, with that flow's chain text. The place in the
        // teleport queue is not copied: each flow keeps its own (ExpectSoloTeleport).
        internal Activation Copy(string chain)
        {
            return new Activation
            {
                MoverKnown = MoverKnown,
                SoloTeleportEligible = SoloTeleportEligible,
                MoverGuid = MoverGuid,
                MoverName = MoverName,
                Group = Group,
                Untraced = Untraced,
                StandIn = StandIn,
                Origin = Origin,
                OriginTerm = OriginTerm,
                Chain = chain
            };
        }
    }

    // what was last applied to one torch (TorchStates)
    private sealed class TorchState
    {
        // 1 lit, 2 out
        internal int Applied;

        // the variable whose value decided it: Torch_<name>, Torches_Lit or Torches_Auto
        internal string Decider;

        // TorchLocationKey of the torch's location
        internal string Location;
    }

    private sealed class Torch
    {
        // its entry in TorchStates
        internal string Key;
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
