using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

// EXPERIMENTAL. Lets a Dungeon Maker campaign START A DIALOGUE from a variable, as if the player had clicked the
// NPC or object that owns that dialogue.
//
// Dungeon Maker dialogues are gadget flows (UserGadget.ApplyParameters attaches the dialogue to the gadget's
// "start dialogue" function). Clicking an NPC/object ends up in GameGadget.Interact(flowIndex, character); this
// patch calls the same method for the gadget you name. So the dialogue must already be set up in Dungeon Maker on
// an NPC or object, exactly as for a normal click; this only supplies a different way to start it.
//
// The campaign author creates these variables (names are case-sensitive; missing ones are ignored):
//
//   Dialog_Trigger       String. Set it to the NAME of the NPC or object whose dialogue should start. Any of
//                        these is accepted (case-insensitive):
//                          - the NAME THE PLAYER SEES for the NPC (e.g. "Badri"),
//                          - the NPC's definition name (the name you typed when creating it, e.g. "Ismene"),
//                          - the gadget's own name (e.g. "NpcDialog4"),
//                          - the NPC's internal "unique" name (e.g. "Forge_Envoy").
//                        It is cleared to "" once the dialogue has been started. While it cannot run yet (another
//                        dialogue is running, the target is not in the current location, or the target's
//                        interaction conditions are not met) it stays set and is retried every frame; the reason
//                        is written to the log once.
//   Dialog_ListGadgets   Int. Set to 1 and the mod writes every interactive gadget in the current location to the
//                        log (gadget name and every name found for its NPC), then sets it to 0.
//
// How an NPC is found for a gadget: the game links them through GameLocationCharacter.BoundGadgetId, which is the
// key of the gadget table (GameLocationGadgetManager.gadgetsByGuid); its own PostLoad rebinds the same way, using
// IGameLocationCharacterService.ValidCharacters. From that character we read the monster definition name, the name
// the UI shows (GuiCharacter.Name) and the localized title. If an NPC has not spawned yet, only the gadget name
// works until it does.
//
// ROLL ACTIVATORS (same mechanism): a Roll Activator is a gadget with an interaction flow whose skill check is rolled
// by the character that interacts. This patch can "click" one for a chosen character, e.g. when a PC walks into an
// Area Activator, which lets the check happen automatically. Dungeon Maker's own list of gadgets that an Area
// Activator can enable cannot be changed from here (it comes from game data), so use a variable instead:
//
//   Roll_Trigger         String. Set it to the NAME of a Roll Activator gadget (use Dialog_ListGadgets to see the
//                        names). It is cleared to "" once the roll has been started. It stays set and is retried
//                        every frame while it cannot run yet (a dialogue is running, the gadget is not in this
//                        location, or the gadget's own conditions are not met); the reason is logged once.
//                        Only one request can be pending at a time: if two things set it in the same frame, the
//                        later value replaces the earlier one.
//                        WHO ROLLS: the player character most recently found inside an Area Activator or pressure
//                        plate (within the last 10 seconds), so "whoever walks in" rolls; otherwise the first living
//                        hero in the party.
//                        Typical setup: Area Activator -> Variable Operation that sets Roll_Trigger to the name of
//                        the Roll Activator.
//
// TELEPORTING (same mechanism): a Teleporter gadget runs the game's FunctorTeleport, whose destination is a Node
// (looked up by the Node's name; its transform is the "placement marker") and whose characters are chosen by a
// look-up method (individual = the interacting character, party = all party members). This patch runs that same
// functor, so formation placement, followers and summons behave exactly as with a real teleporter, but starts it from
// a variable. The Node must be in the CURRENT location (this does not change locations).
//
//   Teleport_Node        String. Set it to the NAME of a Node gadget: ONE PC is teleported there (the player
//                        character most recently found inside an Area Activator / pressure plate within the last
//                        10 seconds, otherwise the first living hero).
//   Teleport_PartyNode   String. Set it to the NAME of a Node gadget: the WHOLE PARTY is teleported there.
//                        Both are cleared to "" once the teleport has been performed. They stay set and are retried
//                        while it cannot run yet (a dialogue is running, or no Node with that name exists in this
//                        location); the reason is logged once.
//   Dialog_ListGadgets   Set it to 2 (instead of 1) to list ALL gadgets, including non-interactive ones such as
//                        Nodes, plus the names of the gadget objects in the scene; use this to find Node names.
//
// TORCHES (same mechanism): a torch's "Lit" checkbox in the Dungeon Maker is stored as one of the gadget's named
// CONDITIONS (the creator's checkbox becomes that condition's starting state). The game changes conditions while it
// runs with GameGadget.SetCondition(conditionIndex, state, ...), looking the index up by name in
// WorldGadget.ConditionChoices(); this patch does the same for torches, so they can be lit and put out during play.
// A "torch" is a gadget whose name contains "torch" (TorchNameFilter: InteractiveTorch1, TorchPike1, ...) and that
// has a lit condition. The creator's "Lit" checkbox is stored as the condition Param_DefaultAsLit (only the starting
// state); the condition that turns the flame and light on and off while playing is Effect_Activated (see
// LitConditionNames). Dialog_ListGadgets = 2 lists every gadget object with its condition names.
//
// Three layers, strongest first. Each torch gets the first one that applies; if none applies the torch stays exactly
// as the creator set it. A change is applied once (the torch is not fought afterwards), so an Interactive Torch a
// player toggles stays as the player left it until the layer above it changes again.
//
//   1. Torch_<gadget name>   Int. One torch: 1 = lit, 2 = out, 0 / missing = no opinion.
//                            e.g. a variable named Torch_TorchPike1 controls the gadget named TorchPike1.
//   2. Torches_Lit           Int. ALL torches in the current location: 1 = lit, 2 = out, 0 / missing = no opinion.
//                            Use it to switch every torch at any moment you like; it overrides the automatic mode.
//   3. Torches_Auto          Int or Bool. Non-zero / true = torches follow the in-game clock, but ONLY on
//                            OUTDOOR maps: lit from 6 PM until 8 AM, out from 8 AM until 6 PM (AutoLitFromHour,
//                            AutoOutFromHour). Indoor maps are never touched by this layer, so their torches stay
//                            exactly as the creator set them.
//
// OUTDOOR means the map's environment (UserLocation.Environment) has its Outdoor flag set. That is the game's own
// flag, but it is game data, so the log prints every map's environment name and flag the first time the automatic
// mode looks at it:  TORCH auto: environment '<name>' outdoor=True/False.  If the flag ever disagrees with what you
// expect (Town yes, Town Interiors no, ...), add that environment's name to OutdoorOverrideNames or
// IndoorOverrideNames below.
//
// Typical setups:
//   * "torches on at night outdoors":  create Torches_Auto and set it to 1 once (e.g. from a variable operation on
//     the entrance).
//   * "light all torches when the party finishes a quest":  set Torches_Lit to 1 at that moment, 2 to put them out.
//   * "light just this one torch":  create Torch_<its name> and set it to 1.
//
// Notes: the first living hero in the party is the one that "interacts" for dialogues.// Notes: the first living hero in the party is the one that "interacts" for dialogues. Interact does not walk anyone to the
// target (the walking is part of the click action, not of Interact). Not tested in multiplayer.
internal static class GameDialogBridge
{
    internal const string TriggerVariable = "Dialog_Trigger";
    internal const string ListVariable = "Dialog_ListGadgets";
    internal const string RollVariable = "Roll_Trigger";
    internal const string TeleportNodeVariable = "Teleport_Node";
    internal const string TeleportPartyVariable = "Teleport_PartyNode";
    internal const string TorchesVariable = "Torches_Lit";
    internal const string TorchesAutoVariable = "Torches_Auto";
    internal const string TorchVariablePrefix = "Torch_";

    // a gadget is treated as a torch when its name contains this ...
    private const string TorchNameFilter = "torch";

    // ... and has a condition with one of these names (first match wins, case-insensitive).
    // From the game's own listing of a torch's conditions: [Initialized, Effect_Activated, Param_ManualExtinguish,
    // Param_DefaultAsLit, RemoteActivated, WieldsFlamable]. "Param_DefaultAsLit" is the creator's "Lit" checkbox (the
    // STARTING state only); "Effect_Activated" is the live state of the flame/light effect, so that is what we switch.
    private static readonly string[] LitConditionNames = { "Effect_Activated", "Lit", "IsLit", "Lighted" };

    // automatic mode (outdoor maps only): lit from this hour ...
    private const int AutoLitFromHour = 18; // 6 PM

    // ... until this hour (then out until AutoLitFromHour again)
    private const int AutoOutFromHour = 8; // 8 AM

    // environments the game flags wrongly (matched on EnvironmentDefinition.Name, case-insensitive);
    // normally empty, fill in only after reading the "TORCH auto: environment ..." log lines
    private static readonly string[] OutdoorOverrideNames = { };
    private static readonly string[] IndoorOverrideNames = { };

    // torches are re-evaluated every this many updates, and the torch list is rebuilt every this many evaluations
    private const int TorchEvaluateInterval = 15;
    private const int TorchRefreshEvaluations = 8;

    // the (cheap) id match runs every frame; the (dearer) displayed-name match only every Nth attempt
    private const int DisplayNameScanInterval = 20;

    // how long a PC seen inside an area stays "the one who walked in"
    private const double AreaMemorySeconds = 10;

    private static readonly FieldInfo GadgetsByGuidField =
        AccessTools.Field(typeof(GameLocationGadgetManager), "gadgetsByGuid");

    private static readonly FieldInfo ActiveListenersField = AccessTools.Field(typeof(GameGadget), "activeListeners");

    private static readonly FieldInfo PlayerPlacementMarkersField =
        AccessTools.Field(typeof(FunctorParametersDescription), "playerPlacementMarkers");

    private static bool _busy;
    private static bool _failed;
    private static string _lastReport = string.Empty;
    private static string _lastRollReport = string.Empty;
    private static string _lastTeleportReport = string.Empty;
    private static int _teleportScanTicks;
    private static int _torchTicks;
    private static int _torchEvaluations;
    private static List<WorldGadget> _torchCache = new List<WorldGadget>();

    // torch instance id -> the state (1 lit / 2 out) last applied by this patch
    private static readonly Dictionary<int, int> TorchApplied = new Dictionary<int, int>();
    private static readonly HashSet<int> TorchReported = new HashSet<int>();
    private static string _lastTorchReport = string.Empty;
    private static string _lastEnvironmentReport = string.Empty;
    private static GameLocationCharacter _areaHero;
    private static DateTime _areaHeroTime = DateTime.MinValue;
    private static string _pendingName;
    private static string _pendingGadgetId;
    private static int _scanTicks;

    private static bool Same(string a, string b)
    {
        return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static List<KeyValuePair<ulong, GameGadget>> GadgetEntries()
    {
        var entries = new List<KeyValuePair<ulong, GameGadget>>();
        var service = ServiceRepository.GetService<IGameLocationGadgetService>();

        if (service is not GameLocationGadgetManager ||
            GadgetsByGuidField?.GetValue(service) is not IDictionary gadgets)
        {
            return entries;
        }

        foreach (DictionaryEntry entry in gadgets)
        {
            if (entry.Key is ulong id && entry.Value is GameGadget gadget && gadget != null)
            {
                entries.Add(new KeyValuePair<ulong, GameGadget>(id, gadget));
            }
        }

        return entries;
    }

    private sealed class NpcNames
    {
        internal string Definition = string.Empty; // MonsterDefinition.Name: what you typed when creating the NPC
        internal string Display = string.Empty; // GuiCharacter.Name: what the UI shows
        internal string Title = string.Empty; // localized GuiPresentation.Title

        internal bool Matches(string name)
        {
            return Same(Definition, name) || Same(Display, name) || Same(Title, name);
        }
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

    // the NPC bound to this gadget (null if no spawned character is bound to it)
    private static NpcNames NamesOf(ulong gadgetId)
    {
        var service = ServiceRepository.GetService<IGameLocationCharacterService>();

        if (gadgetId == 0 || service == null)
        {
            return null;
        }

        var character = (service.ValidCharacters ?? new List<GameLocationCharacter>())
            .FirstOrDefault(x => x != null && x.BoundGadgetId == gadgetId);

        if (character == null)
        {
            return null;
        }

        var definition = (character.RulesetCharacter as RulesetCharacterMonster)?.MonsterDefinition;

        return new NpcNames
        {
            Definition = Safe(() => definition?.Name),
            Display = Safe(() => new GuiCharacter(character).Name),
            Title = Safe(() => definition?.GuiPresentation == null ? null : Gui.Localize(definition.GuiPresentation.Title))
        };
    }

    private static int InteractionFlowCount(GameGadget gadget)
    {
        return ActiveListenersField?.GetValue(gadget) is IList listeners
            ? listeners.Cast<object>().Count(x => x is GameGadgetListenerCharacterInteraction)
            : 0;
    }

    // first flow that is an interaction flow and whose conditions currently allow this hero to use it
    private static int FindReadyInteractionFlow(GameGadget gadget, GameLocationCharacter hero)
    {
        if (ActiveListenersField?.GetValue(gadget) is not IList listeners)
        {
            return -1;
        }

        for (var i = 0; i < listeners.Count; i++)
        {
            if (listeners[i] is GameGadgetListenerCharacterInteraction && gadget.WouldInteract(i, hero))
            {
                return i;
            }
        }

        return -1;
    }

    private static GameLocationCharacter FindHero()
    {
        return ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters?
            .FirstOrDefault(x => x?.RulesetCharacter is RulesetCharacterHero hero && !hero.IsDeadOrDyingOrUnconscious);
    }

    private static bool NarrativeBusy()
    {
        var narrative = ServiceRepository.GetService<INarrativeDirectionService>();

        return narrative != null && (narrative.IsSequenceInProgress || narrative.HasPendingSequence);
    }

    private static void ReportOnce(string message)
    {
        if (message == _lastReport)
        {
            return;
        }

        _lastReport = message;
        Main.Info(message);
    }

    private static void ResetPending()
    {
        _pendingName = null;
        _pendingGadgetId = null;
        _scanTicks = 0;
        _lastReport = string.Empty;
    }

    // resolves what the author typed to a gadget: gadget name / internal NPC name every frame, the NPC's other
    // names every DisplayNameScanInterval attempts; remembers the result while pending
    private static GameGadget ResolveTarget(string name)
    {
        if (name != _pendingName)
        {
            ResetPending();
            _pendingName = name;
        }

        var entries = GadgetEntries();

        if (_pendingGadgetId != null)
        {
            var cached = entries.Select(x => x.Value).FirstOrDefault(x => x.UniqueNameId == _pendingGadgetId);

            if (cached != null)
            {
                return cached;
            }

            _pendingGadgetId = null;
        }

        var found = entries.Select(x => x.Value)
            .FirstOrDefault(x => Same(x.UniqueNameId, name) || Same(x.BoundCharacterUniqueName, name));

        if (found == null && _scanTicks++ % DisplayNameScanInterval == 0)
        {
            found = entries
                .Where(x => InteractionFlowCount(x.Value) > 0 && NamesOf(x.Key)?.Matches(name) == true)
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        if (found != null)
        {
            _pendingGadgetId = found.UniqueNameId;
        }

        return found;
    }

    private static void HandleList(IGameVariableService variables)
    {
        // discovery helper: log every interactive gadget in the current location
        if (variables.TryFindVariable(ListVariable, out var list) && list != null &&
            list.Type == GameVariableDefinitions.Type.Int && list.IntValue != 0)
        {
            var showAll = list.IntValue == 2;
            var entries = GadgetEntries();
            var interactive = entries.Where(x => InteractionFlowCount(x.Value) > 0).ToList();
            var shown = showAll ? entries : interactive;

            list.IntValue = 0;

            Main.Info(
                $"DIALOG gadgets in this location: {entries.Count} ({interactive.Count} interactive){(showAll ? ", listing all" : "")}");

            foreach (var entry in shown)
            {
                var gadget = entry.Value;
                var npc = NamesOf(entry.Key);
                var npcText = npc == null
                    ? "npc=(no spawned character bound)"
                    : $"npcDefinition='{npc.Definition}' npcDisplay='{npc.Display}' npcTitle='{npc.Title}'";

                Main.Info(
                    $"DIALOG gadget: name='{gadget.UniqueNameId}' internalNpc='{gadget.BoundCharacterUniqueName}' {npcText} interactionFlows={InteractionFlowCount(gadget)}");
            }

            if (showAll)
            {
                var worlds = UnityEngine.Object.FindObjectsOfType<WorldGadget>()
                    .Where(x => x != null)
                    .OrderBy(x => x.name)
                    .ToList();

                Main.Info($"DIALOG gadget objects in scene: {worlds.Count}");

                foreach (var world in worlds)
                {
                    Main.Info(
                        $"DIALOG gadget object: name='{world.name}' conditions=[{string.Join(", ", SafeConditionChoices(world))}]");
                }
            }
        }
    }

    private static void HandleDialog(IGameVariableService variables)
    {
        if (!variables.TryFindVariable(TriggerVariable, out var trigger) || trigger == null ||
            trigger.Type != GameVariableDefinitions.Type.String || string.IsNullOrEmpty(trigger.StringValue))
        {
            return;
        }

        var name = trigger.StringValue;

        if (NarrativeBusy())
        {
            ReportOnce($"DIALOG '{name}' waiting: another dialogue is in progress.");

            return;
        }

        var hero = FindHero();

        if (hero == null)
        {
            ReportOnce($"DIALOG '{name}' waiting: no living hero available.");

            return;
        }

        var target = ResolveTarget(name);

        if (target == null)
        {
            ReportOnce($"DIALOG '{name}' waiting: no NPC or object with that name in this location.");

            return;
        }

        var flow = FindReadyInteractionFlow(target, hero);

        if (flow < 0)
        {
            ReportOnce($"DIALOG '{name}' waiting: its interaction is not available (conditions not met?).");

            return;
        }

        // clear first so it can never fire twice
        trigger.StringValue = string.Empty;
        ResetPending();
        Main.Info($"DIALOG starting '{name}' -> gadget '{target.UniqueNameId}' (flow {flow}).");
        target.Interact(flow, hero);
    }

    private static void ReportRollOnce(string message)
    {
        if (message == _lastRollReport)
        {
            return;
        }

        _lastRollReport = message;
        Main.Info(message);
    }

    // called whenever an area / pressure plate listener works out which characters are inside it
    internal static void RememberAreaCharacters(List<GameLocationCharacter> involved)
    {
        var hero = involved?.FirstOrDefault(x =>
            x?.RulesetCharacter is RulesetCharacterHero h && !h.IsDeadOrDyingOrUnconscious);

        if (hero == null)
        {
            return;
        }

        _areaHero = hero;
        _areaHeroTime = DateTime.UtcNow;
    }

    // the PC who most recently walked into an area, if recent and still a living party member
    private static GameLocationCharacter FindAreaHero()
    {
        if (_areaHero == null || (DateTime.UtcNow - _areaHeroTime).TotalSeconds > AreaMemorySeconds)
        {
            return null;
        }

        var party = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters;

        return party != null && party.Contains(_areaHero) &&
               _areaHero.RulesetCharacter is RulesetCharacterHero hero && !hero.IsDeadOrDyingOrUnconscious
            ? _areaHero
            : null;
    }

    private static string[] SafeConditionChoices(WorldGadget world)
    {
        try
        {
            return world.ConditionChoices() ?? new string[0];
        }
        catch (Exception)
        {
            return new string[0];
        }
    }

    private static void ReportTorchOnce(string message)
    {
        if (message == _lastTorchReport)
        {
            return;
        }

        _lastTorchReport = message;
        Main.Info(message);
    }

    private static int IntValueOf(IGameVariableService variables, string name)
    {
        return variables.TryFindVariable(name, out var variable) && variable != null &&
               variable.Type == GameVariableDefinitions.Type.Int
            ? variable.IntValue
            : 0;
    }

    private static bool IsOn(IGameVariableService variables, string name)
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

    // is the current map an outdoor map? (only Dungeon Maker maps have an environment)
    private static bool IsOutdoorMap()
    {
        var environment = ServiceRepository.GetService<IGameLocationService>()?.GameLocation?.UserLocation?.Environment;

        if (environment == null)
        {
            return false;
        }

        var outdoor = environment.Outdoor;

        if (OutdoorOverrideNames.Any(x => Same(x, environment.Name)))
        {
            outdoor = true;
        }

        if (IndoorOverrideNames.Any(x => Same(x, environment.Name)))
        {
            outdoor = false;
        }

        var report = $"TORCH auto: environment '{environment.Name}' outdoor={outdoor}";

        if (report != _lastEnvironmentReport)
        {
            _lastEnvironmentReport = report;
            Main.Info(report);
        }

        return outdoor;
    }

    // 0 = no opinion, 1 = lit, 2 = out: what the clock says for the automatic layer on this map right now
    private static int AutoTorchState(IGameVariableService variables)
    {
        if (!IsOn(variables, TorchesAutoVariable))
        {
            return 0;
        }

        var hour = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign?.GameTime?.TimeInfo?.Hour;

        if (hour == null || !IsOutdoorMap())
        {
            return 0;
        }

        return hour >= AutoLitFromHour || hour < AutoOutFromHour ? 1 : 2;
    }

    private static void RefreshTorchCache()
    {
        _torchCache = UnityEngine.Object.FindObjectsOfType<WorldGadget>()
            .Where(x => x != null && x.name != null &&
                        x.name.IndexOf(TorchNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }

    private static void HandleTorches(IGameVariableService variables)
    {
        if (_torchTicks++ % TorchEvaluateInterval != 0)
        {
            return;
        }

        // is any torch control in use at all? (per-torch variables are named Torch_<gadget>)
        var manual = IntValueOf(variables, TorchesVariable);
        var auto = variables.TryFindVariable(TorchesAutoVariable, out _);
        var perTorch = false;

        if (manual == 0 && !auto)
        {
            perTorch = variables.ActiveVariables
                .Any(x => x?.Name != null &&
                          x.Name.StartsWith(TorchVariablePrefix, StringComparison.OrdinalIgnoreCase));
        }

        if (manual == 0 && !auto && !perTorch)
        {
            TorchApplied.Clear();
            _torchCache.Clear();
            _torchEvaluations = 0;
            _lastTorchReport = string.Empty;

            return;
        }

        // finding scene objects is not free: rebuild the list only every few evaluations
        if (_torchEvaluations++ % TorchRefreshEvaluations == 0)
        {
            RefreshTorchCache();
        }

        var autoState = AutoTorchState(variables);
        var anyTorch = false;

        foreach (var world in _torchCache)
        {
            if (world == null)
            {
                continue;
            }

            anyTorch = true;

            var own = IntValueOf(variables, TorchVariablePrefix + world.name);
            var desired = (own is 1 or 2) ? own : (manual is 1 or 2) ? manual : autoState;

            ApplyTorch(world, desired);
        }

        if (!anyTorch && (manual != 0 || autoState != 0))
        {
            ReportTorchOnce(
                $"TORCH control is active but no gadget with '{TorchNameFilter}' in its name is in this location.");
        }
    }

    // brings one torch to the wanted state (1 lit, 2 out, 0 = leave it alone), once per change
    private static void ApplyTorch(WorldGadget world, int desired)
    {
        var id = world.GetInstanceID();

        if (desired != 1 && desired != 2)
        {
            // no opinion any more: forget it so that a later state is applied again
            TorchApplied.Remove(id);

            return;
        }

        var game = world.GameGadget;

        // not built yet: try again at the next evaluation
        if (game == null || (TorchApplied.TryGetValue(id, out var last) && last == desired))
        {
            return;
        }

        var lit = desired == 1;
        var choices = SafeConditionChoices(world);
        var index = Array.FindIndex(choices, c => LitConditionNames.Any(n => Same(n, c)));

        TorchApplied[id] = desired;

        if (index < 0)
        {
            if (TorchReported.Add(id))
            {
                Main.Info(
                    $"TORCH '{world.name}' has no lit condition; its conditions are [{string.Join(", ", choices)}].");
            }

            return;
        }

        if (game.CurrentConditionStates != null &&
            game.CurrentConditionStates.TryGetValue(index, out var current) && current == lit)
        {
            return;
        }

        game.SetCondition(index, lit, new List<GameLocationCharacter>());
        Main.Info($"TORCH '{world.name}': '{choices[index]}' -> {(lit ? "lit" : "out")}.");
    }

    private static void ReportTeleportOnce(string message)
    {
        if (message == _lastTeleportReport)
        {
            return;
        }

        _lastTeleportReport = message;
        Main.Info(message);
    }

    // the world object of the Node with this name (matched on the object's name, the same way the game's own
    // Teleporter finds its destination, or on the gadget's unique name)
    private static WorldGadget FindNode(string name)
    {
        foreach (var candidate in UnityEngine.Object.FindObjectsOfType<WorldGadget>())
        {
            if (candidate != null && (Same(candidate.name, name) || Same(candidate.GameGadget?.UniqueNameId, name)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void HandleTeleports(IGameVariableService variables)
    {
        var pending = false;

        foreach (var (variableName, party) in new[] { (TeleportNodeVariable, false), (TeleportPartyVariable, true) })
        {
            pending |= TryTeleport(variables, variableName, party);
        }

        if (!pending)
        {
            _teleportScanTicks = 0;
            _lastTeleportReport = string.Empty;
        }
    }

    // returns true while a request for this variable is still waiting
    private static bool TryTeleport(IGameVariableService variables, string variableName, bool party)
    {
        if (!variables.TryFindVariable(variableName, out var trigger) || trigger == null ||
            trigger.Type != GameVariableDefinitions.Type.String || string.IsNullOrEmpty(trigger.StringValue))
        {
            return false;
        }

        var nodeName = trigger.StringValue;

        if (NarrativeBusy())
        {
            ReportTeleportOnce($"TELEPORT '{nodeName}' waiting: a dialogue is in progress.");

            return true;
        }

        var characters = ServiceRepository.GetService<IGameLocationCharacterService>();
        var who = party ? null : FindAreaHero() ?? FindHero();

        if (characters?.PartyCharacters == null || (!party && who == null))
        {
            ReportTeleportOnce($"TELEPORT '{nodeName}' waiting: no living hero available.");

            return true;
        }

        // finding scene objects is not free: while waiting, look only every Nth attempt
        if (_teleportScanTicks++ % DisplayNameScanInterval != 0)
        {
            return true;
        }

        var node = FindNode(nodeName);

        if (node == null)
        {
            ReportTeleportOnce($"TELEPORT '{nodeName}' waiting: no Node with that name in this location.");

            return true;
        }

        // clear first so it can never fire twice
        trigger.StringValue = string.Empty;
        _lastTeleportReport = string.Empty;
        _teleportScanTicks = 0;

        // run the game's own teleport functor with the same kind of parameters a Teleporter gadget would give it
        var parameters = new FunctorParametersDescription
        {
            CharacterLookUpMethod = party
                ? FunctorDefinitions.CharacterLookUpMethod.AllPartyMembers
                : FunctorDefinitions.CharacterLookUpMethod.InteractingCharacter,
            ActingCharacters = party
                ? new List<GameLocationCharacter>(characters.PartyCharacters)
                : new List<GameLocationCharacter> { who },
            SourceGadget = node
        };

        PlayerPlacementMarkersField?.SetValue(parameters, new[] { node.transform });

        Main.Info(party
            ? $"TELEPORT party -> node '{nodeName}'."
            : $"TELEPORT '{who.Name}' -> node '{nodeName}'.");

        try
        {
            var routine = new FunctorTeleport().Execute(parameters, null);

            // the teleport functor does all its work in its first step and never waits
            while (routine.MoveNext())
            {
            }
        }
        catch (Exception e)
        {
            // a failed teleport must not switch off the dialogue and roll triggers as well
            Main.Error(e);
        }

        return false;
    }

    private static void HandleRoll(IGameVariableService variables)
    {
        if (!variables.TryFindVariable(RollVariable, out var trigger) || trigger == null ||
            trigger.Type != GameVariableDefinitions.Type.String || string.IsNullOrEmpty(trigger.StringValue))
        {
            return;
        }

        var name = trigger.StringValue;

        if (NarrativeBusy())
        {
            ReportRollOnce($"ROLL '{name}' waiting: a dialogue is in progress.");

            return;
        }

        var roller = FindAreaHero() ?? FindHero();

        if (roller == null)
        {
            ReportRollOnce($"ROLL '{name}' waiting: no living hero available.");

            return;
        }

        var target = GadgetEntries().Select(x => x.Value).FirstOrDefault(x => Same(x.UniqueNameId, name));

        if (target == null)
        {
            ReportRollOnce($"ROLL '{name}' waiting: no object with that name in this location.");

            return;
        }

        var flow = FindReadyInteractionFlow(target, roller);

        if (flow < 0)
        {
            ReportRollOnce($"ROLL '{name}' waiting: its interaction is not available (conditions not met?).");

            return;
        }

        // clear first so it can never fire twice
        trigger.StringValue = string.Empty;
        _lastRollReport = string.Empty;
        Main.Info($"ROLL starting '{name}' (flow {flow}) for '{roller.Name}'.");
        target.Interact(flow, roller);
    }

    internal static void Process()
    {
        // _busy: starting a dialogue can run code that advances the clock, which re-enters this method
        if (_busy || _failed)
        {
            return;
        }

        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null)
        {
            return;
        }

        try
        {
            _busy = true;

            HandleList(variables);
            HandleDialog(variables);
            HandleRoll(variables);
            HandleTeleports(variables);
            HandleTorches(variables);
        }
        catch (Exception e)
        {
            // runs every frame: report once, then switch itself off rather than spam or disturb the game
            _failed = true;
            Main.Error(e);
        }
        finally
        {
            _busy = false;
        }
    }
}

[UsedImplicitly]
[HarmonyPatch(typeof(GameTime), nameof(GameTime.UpdateTime))]
public static class GameDialogTriggerPatcher
{
    [UsedImplicitly]
    public static void Postfix()
    {
        GameDialogBridge.Process();
    }
}

// remember which PC an Area Activator (volumetric trigger) found inside it
[UsedImplicitly]
[HarmonyPatch(typeof(GameGadgetListenerVolumetricTrigger),
    nameof(GameGadgetListenerVolumetricTrigger.ComputeInvolvedCharacters))]
public static class GameAreaEntrantVolumetricPatcher
{
    [UsedImplicitly]
    public static void Postfix(GameGadgetListenerVolumetricTrigger __instance)
    {
        GameDialogBridge.RememberAreaCharacters(__instance.InvolvedCharacters);
    }
}

// the same for pressure plates
[UsedImplicitly]
[HarmonyPatch(typeof(GameGadgetListenerPressurePlate),
    nameof(GameGadgetListenerPressurePlate.ComputeInvolvedCharacters))]
public static class GameAreaEntrantPressurePlatePatcher
{
    [UsedImplicitly]
    public static void Postfix(GameGadgetListenerPressurePlate __instance)
    {
        GameDialogBridge.RememberAreaCharacters(__instance.InvolvedCharacters);
    }
}
