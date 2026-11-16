using System;
using System.Collections.Generic;
using System.Linq;

namespace SolastaUnfinishedBusiness.Models;

// The campaign variable names the Dungeon Maker commands use (issue #42), and what happens when a campaign uses one
// of them for something of its own. A campaign made before these commands existed, or by an author who never read
// the guide, may well have a variable called Clock_Hour or Roll_Trigger_Status; the mod must not write into it, and
// must not take what the campaign writes there for a command.
//
// THE NAMES
//   - commands (String, the campaign writes a gadget's name): Dialog_Trigger, Roll_Trigger, Teleport_Node,
//     Teleport_PartyNode;
//   - requests (Int, the campaign writes a number): Dialog_ListGadgets, Torches_Lit, Torches_Auto, Torch_<gadget>,
//     Clock_SkipHours, Clock_SkipDays, Clock_SkipToHour, Clock_SkipToDay;
//   - switches and counters (Int): Clock_CountMinutes/Hours/Days switch a counter on, and the mod adds to
//     Clock_MinutesElapsed/HoursElapsed/DaysElapsed. A campaign sets a counter back itself (that is how it starts
//     counting again), so the campaign and the mod share these by design and they are not moved;
//   - names only the mod writes: the read-outs Clock_Hour, Clock_Minute, Clock_Day, Clock_DayOfMonth, Clock_Month,
//     Clock_Year, Clock_CampaignDay; every <command>_Status and <skip>_Status; and the mod's own
//     Clock_SkipRemainingSeconds and Clock_SkipTargetMinute. (Commands_SavedState, which exists inside saves only,
//     is looked after where it is written, DungeonMakerCommandsContext "SAVES".)
//
// A NAME ONLY THE MOD WRITES THAT THE CAMPAIGN OWNS (user direction, 2026-11-16: "Let's just skip past the first
// secondary name of FB_ and just go straight to increments"). The mod then writes to <name>_2 instead, or
// <name>_3, ...: the first one the campaign doesn't own. A name is the campaign's own when the campaign itself
// writes it: a Variable Operation gadget in any of its locations, or a Variable Operation among the actions of a
// dialogue line, a quest step or a quest outcome (the places a campaign can write a variable from). Reading one,
// with a Variable Activator, a dialogue condition or a quest condition, is what the read-outs are for and makes
// nothing the campaign's own. Two finer points for the _Status names, which a campaign is meant to read and may
// well tidy up:
//   - a campaign that sets one back to an empty text, from one place only, does not own it; writing a real
//     value into it, or writing into it from more than one place, makes it the campaign's own;
//   - a campaign that tests one for a text the mod never writes (anything but Pending, Running, Succeeded,
//     Failed, Cancelled) owns it: it is plainly using the name for something else.
// The choice is worked out from the campaign's contents each time a location is loaded (Refresh), so it is the
// same on every computer and nothing about it is saved; one log line says so for each name moved.
//
// A COMMAND OR REQUEST NAME THE CAMPAIGN USES FOR SOMETHING ELSE (user direction, 2026-11-16: "If it fails, just
// treat it the author's own and ignore it. Log it in the Player.log once"). There is nothing to move these to:
// the campaign writes them. So when a write can never be a command (the variable has the wrong type, or the text
// names nothing in the location and nothing anywhere in the campaign's data), the variable is the campaign's
// own for the rest of that game (saves of it keep it too, see DungeonMakerCommandsContext "SAVES"): nothing is done, no alert is shown, the variable is not emptied, and the log
// says so once (MarkOwnRequest). A command that names something real and fails for another reason fails as
// before, with its alert. Typing mistakes are pointed out where they are made, in the editor (NameHint,
// ValueHint, shown by CustomUI/DungeonMakerNameHints).
internal static class DungeonMakerNamesContext
{
    internal enum Role
    {
        None,
        Command,
        Request,
        Switch,
        Counter,
        Readout,
        Status,
        Internal
    }

    private static readonly string[] Commands =
    [
        DungeonMakerCommandsContext.DialogVariable, DungeonMakerCommandsContext.RollVariable,
        DungeonMakerCommandsContext.TeleportNodeVariable, DungeonMakerCommandsContext.TeleportPartyVariable
    ];

    // the texts the mod writes into a status variable, and no others
    private static readonly string[] StatusWords =
    [
        DungeonMakerCommandsContext.StatusPending, DungeonMakerCommandsContext.StatusRunning,
        DungeonMakerCommandsContext.StatusSucceeded, DungeonMakerCommandsContext.StatusFailed,
        DungeonMakerCommandsContext.StatusCancelled
    ];

    // the names only the mod writes, in the order they are logged
    private static readonly string[] WrittenNames = DungeonMakerClockContext.ReadoutVariables
        .Concat(Commands.Concat(DungeonMakerClockContext.SkipVariables)
            .Select(x => x + DungeonMakerCommandsContext.StatusSuffix))
        .Concat(DungeonMakerClockContext.OwnVariables)
        .ToArray();

    // per name only the mod writes: the name it writes to with the campaign being played, when that is another one
    private static readonly Dictionary<string, string> Remapped = [];

    // command and request names the campaign being played uses for something else (see MarkOwnRequest)
    private static readonly HashSet<string> OwnRequests = [];

    // the scan of the campaign being edited is kept for this long, so that typing a name doesn't read the whole
    // campaign again at every key
    private const float EditorScanSeconds = 1f;
    private static object _scannedFor;
    private static float _scannedAt;
    private static CampaignUse _scanned;

    #region What a name is

    // what the mod uses this variable name for, if anything; for a status name, the command or skip it belongs to
    internal static Role RoleOf(string name, out string statusOf)
    {
        statusOf = null;

        if (string.IsNullOrEmpty(name))
        {
            return Role.None;
        }

        if (Array.IndexOf(Commands, name) >= 0)
        {
            return Role.Command;
        }

        if (name == DungeonMakerCommandsContext.ListVariable || name == DungeonMakerCommandsContext.TorchesVariable ||
            name == DungeonMakerCommandsContext.TorchesAutoVariable ||
            (name.Length > DungeonMakerCommandsContext.TorchVariablePrefix.Length &&
             name.StartsWith(DungeonMakerCommandsContext.TorchVariablePrefix, StringComparison.Ordinal)) ||
            Array.IndexOf(DungeonMakerClockContext.SkipVariables, name) >= 0)
        {
            return Role.Request;
        }

        if (Array.IndexOf(DungeonMakerClockContext.SwitchVariables, name) >= 0)
        {
            return Role.Switch;
        }

        if (Array.IndexOf(DungeonMakerClockContext.CounterVariables, name) >= 0)
        {
            return Role.Counter;
        }

        if (Array.IndexOf(DungeonMakerClockContext.ReadoutVariables, name) >= 0)
        {
            return Role.Readout;
        }

        if (Array.IndexOf(DungeonMakerClockContext.OwnVariables, name) >= 0 ||
            name == DungeonMakerCommandsContext.SavedStateVariable)
        {
            return Role.Internal;
        }

        if (name.EndsWith(DungeonMakerCommandsContext.StatusSuffix, StringComparison.Ordinal))
        {
            var command = name.Substring(0, name.Length - DungeonMakerCommandsContext.StatusSuffix.Length);

            if (Array.IndexOf(Commands, command) >= 0 ||
                Array.IndexOf(DungeonMakerClockContext.SkipVariables, command) >= 0)
            {
                statusOf = command;

                return Role.Status;
            }
        }

        return Role.None;
    }

    #endregion

    #region Names only the mod writes

    // The name the mod writes to for this one of its names, with the campaign being played: the name itself, or
    // the one it was moved to because the campaign owns it. Every write and every read-back of such a name goes
    // through here.
    internal static string Written(string name)
    {
        return Remapped.TryGetValue(name, out var moved) ? moved : name;
    }

    // DungeonMakerCommandsContext.OnLocationReady, first thing: a location of the campaign has loaded. Works out
    // again, from the campaign as it is now, which of the mod's names the campaign owns and where each goes.
    // Outside a Dungeon Maker campaign or location there is nothing to read, and no name is moved.
    internal static void Refresh(GameLocation location)
    {
        Remapped.Clear();

        try
        {
            var use = Scan(location?.UserCampaign, location?.UserLocation);

            if (use == null)
            {
                return;
            }

            foreach (var name in WrittenNames)
            {
                var moved = use.FreeNameFor(name);

                if (moved == name)
                {
                    continue;
                }

                Remapped[name] = moved;

                var what = RoleOf(name, out _) switch
                {
                    Role.Readout => "its read-out",
                    Role.Status => "its status",
                    _ => "its own value"
                };

                Main.Info($"DM names: `{name}` is the campaign's own; the mod writes {what} to `{moved}`.");
            }
        }
        catch (Exception e)
        {
            // the commands work without this; the names then stay as they are
            Main.Error($"DM names: could not read the campaign's variable use: {e.Message}");
        }
    }

    #endregion

    #region Command and request names the campaign uses for something else

    // a new game is being played (another save, another campaign): what was found out about the last one is gone
    internal static void ResetSession()
    {
        OwnRequests.Clear();
    }

    // the names found so far, for the saved state (DungeonMakerCommandsContext.SaveState), which carries them to
    // a computer that loads or joins the game
    internal static IEnumerable<string> OwnRequestNames => OwnRequests;

    internal static bool IsOwnRequest(string name)
    {
        return OwnRequests.Count > 0 && OwnRequests.Contains(name);
    }

    // From now on, in this game, this command or request name is the campaign's own variable: the mod does not
    // act on it, write to it or empty it. Said once in the log, with what showed it.
    internal static void MarkOwnRequest(string name, string why)
    {
        if (OwnRequests.Add(name))
        {
            Main.Info($"DM names: `{name}` is treated as the campaign's own variable in this game and its " +
                      $"saves, and the mod leaves it alone: {why}.");
        }
    }

    // Is this variable of a type its command or request can never have? Commands are String; requests are Int
    // (Torches_Auto may also be a Bool).
    internal static bool WrongType(string name, GameVariableDefinitions.Type type)
    {
        return RoleOf(name, out _) switch
        {
            Role.Command => type != GameVariableDefinitions.Type.String,
            Role.Request => type != GameVariableDefinitions.Type.Int &&
                            !(name == DungeonMakerCommandsContext.TorchesAutoVariable &&
                              type == GameVariableDefinitions.Type.Bool),
            _ => false
        };
    }

    // Does anything in the campaign's data go by this name, so that a command naming it could ever work? False
    // also when there is no campaign data to look in (the game's own campaigns): the caller then keeps to what it
    // finds in the location.
    internal static bool CampaignKnows(string command, string value, UserCampaign campaign, UserLocation location,
        out bool hasData)
    {
        var names = Targets(command, campaign, campaign == null ? location : null);

        hasData = names != null;

        return names != null && names.Knows(value);
    }

    #endregion

    #region Reading a campaign

    // what a campaign does with variables, as far as the rules above need to know
    internal sealed class CampaignUse
    {
        // per variable, the texts the campaign's Variable Operations write into it (null for a number or a switch)
        private readonly Dictionary<string, List<string>> _writes = [];

        // per variable, the texts the campaign tests it for
        private readonly Dictionary<string, List<string>> _tests = [];

        internal void Write(string variable, string text)
        {
            Add(_writes, variable, text);
        }

        internal void Test(string variable, string text)
        {
            Add(_tests, variable, text);
        }

        private static void Add(Dictionary<string, List<string>> table, string variable, string text)
        {
            if (string.IsNullOrEmpty(variable))
            {
                return;
            }

            if (!table.TryGetValue(variable, out var texts))
            {
                table.Add(variable, texts = []);
            }

            texts.Add(text);
        }

        // Is this name the campaign's own? asStatus: judge it as a status name (see the top of the file).
        private bool Owns(string name, bool asStatus)
        {
            // A status name is the campaign's own when it writes a real value into it, or when it writes into it from
            // more than one place (user, 2026-11-16: the reset is fine "as long as the campaign never reassigns an
            // actual value to it and does not keep writing to it across the span of the campaign"). One Variable
            // Operation that sets it back to an empty text is a tidy-up, and the mod keeps writing there.
            if (_writes.TryGetValue(name, out var written) &&
                (!asStatus || written.Count > 1 || written.Any(x => !string.IsNullOrEmpty(x))))
            {
                return true;
            }

            return asStatus && _tests.TryGetValue(name, out var tested) &&
                   tested.Any(x => !string.IsNullOrEmpty(x) && Array.IndexOf(StatusWords, x) < 0);
        }

        // the name itself when the campaign doesn't own it, otherwise the first of name_2, name_3, ... it doesn't
        internal string FreeNameFor(string name)
        {
            var asStatus = RoleOf(name, out _) == Role.Status;

            if (!Owns(name, asStatus))
            {
                return name;
            }

            for (var i = 2; i < 1000; i++)
            {
                var candidate = $"{name}_{i}";

                if (!Owns(candidate, asStatus))
                {
                    return candidate;
                }
            }

            return name;
        }
    }

    // Reads every place a campaign can write or test a variable from. With no campaign (a location made and
    // played on its own), the location alone. Null when there is neither.
    internal static CampaignUse Scan(UserCampaign campaign, UserLocation location)
    {
        if (campaign == null && location == null)
        {
            return null;
        }

        var use = new CampaignUse();

        foreach (var place in campaign?.UserLocations ?? [location])
        {
            ScanLocation(place, use);
        }

        foreach (var dialog in campaign?.UserDialogs ?? [])
        {
            foreach (var state in dialog?.AllDialogStates ?? [])
            {
                if (state == null)
                {
                    continue;
                }

                ScanFunctors(state.Functors, use);
                use.Test(state.VariableName, state.VariableTest?.StringValue);
            }
        }

        foreach (var quest in campaign?.UserQuests ?? [])
        {
            foreach (var step in quest?.AllQuestStepDescriptions ?? [])
            {
                if (step == null)
                {
                    continue;
                }

                ScanFunctors(step.OnStartFunctors, use);

                foreach (var outcome in step.OutcomesTable ?? [])
                {
                    if (outcome == null)
                    {
                        continue;
                    }

                    ScanFunctors(outcome.OnCompleteFunctors, use);

                    // a "variable" condition keeps the variable's name in its first text
                    if (outcome.ValidatorDescription is { VariableTest: not null } validator)
                    {
                        use.Test(validator.StringParameter, validator.VariableTest.StringValue);
                    }
                }
            }
        }

        return use;
    }

    // A gadget that has a "variable" parameter is a Variable Operation (it also has an operation type) or a
    // Variable Activator (it has a test type). Its text value, if it has one, is what it writes or tests for.
    private static void ScanLocation(UserLocation location, CampaignUse use)
    {
        foreach (var gadget in GadgetsOf(location))
        {
            var values = gadget.ParameterValues;

            if (values == null)
            {
                continue;
            }

            string variable = null;
            string text = null;
            var writes = false;
            var tests = false;

            foreach (var value in values)
            {
                switch (value?.GadgetParameterDescription?.Type)
                {
                    case GadgetBlueprintDefinitions.Type.Variable:
                        variable = value.StringValue;
                        break;
                    case GadgetBlueprintDefinitions.Type.VariableValueString:
                        text = value.StringValue;
                        break;
                    case GadgetBlueprintDefinitions.Type.VariableOperationType
                        or GadgetBlueprintDefinitions.Type.VariableOperationTypeSet:
                        writes = true;
                        break;
                    case GadgetBlueprintDefinitions.Type.VariableTestTypeScalar
                        or GadgetBlueprintDefinitions.Type.VariableTestTypeLogical:
                        tests = true;
                        break;
                }
            }

            if (writes)
            {
                use.Write(variable, text);
            }
            else if (tests)
            {
                use.Test(variable, text);
            }
        }
    }

    // The actions of a dialogue line, a quest step or a quest outcome. A Variable Operation keeps the variable in
    // its first text and the text it writes in its third (UserFunctorParametersDescription.
    // BuildRuntimeFunctorParameters).
    private static void ScanFunctors(List<UserFunctorParametersDescription> functors, CampaignUse use)
    {
        foreach (var functor in functors ?? [])
        {
            if (functor is { Type: "VariableOperation" })
            {
                use.Write(functor.StringParameter, functor.StringParameter3);
            }
        }
    }

    private static IEnumerable<UserGadget> GadgetsOf(UserLocation location)
    {
        return (location?.UserRooms ?? [])
            .Where(x => x?.UserGadgets != null)
            .SelectMany(x => x.UserGadgets)
            .Where(x => x != null);
    }

    #endregion

    #region What a command can name

    // The names a command's text can stand for, read from the campaign's data: the likely ones for this command
    // first, then every other gadget. The commands look a name up in the location they are written in, by the
    // gadget's unique name; Dialog_Trigger also by the NPC's internal name and by the name or title its author
    // gave it (DungeonMakerCommandsContext.FindInteractionTargets, FindDestination); always whatever the case of
    // the letters. This list is wider than what one command accepts (any gadget's name counts): it is there to
    // catch a text that names nothing at all, not to say a command will work.
    internal sealed class TargetNames
    {
        internal readonly List<string> Likely = [];
        internal readonly List<string> Others = [];

        internal bool Knows(string value)
        {
            return Likely.Concat(Others).Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
        }

        // up to three names close to this text (few letters to change), the likely ones first
        internal List<string> Closest(string value)
        {
            var allowed = Math.Max(2, value.Length * 2 / 5);

            return Likely.Select(x => (Name: x, Likely: 0))
                .Concat(Others.Select(x => (Name: x, Likely: 1)))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .Select(x => (x.Name, x.Likely, Distance: Distance(x.Name, value)))
                .Where(x => x.Distance <= allowed)
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Likely)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(x => x.Name)
                .ToList();
        }
    }

    // The names to offer while this text is typed as a command's target (CustomUI/DungeonMakerNameSuggestions),
    // best first, at most max of them:
    //   - the likely names for the command before the other gadgets (TargetNames.Likely, Others);
    //   - within each, names that start with the text, then names that contain it, then names a few letters away
    //     from it (the same distance rule as Closest), each by how close they are and then by name;
    //   - with nothing typed yet, every name counts as a match, so the likely names lead.
    // Capital and small letters don't matter, as in the commands. A name that fits in none of these ways is left
    // out, so a text that names nothing gets no suggestion and the missing-name warning shows instead.
    internal static List<string> Suggest(TargetNames names, string value, int max)
    {
        if (names == null)
        {
            return [];
        }

        value ??= string.Empty;

        // Only the names that start with what is typed, whatever the case of the letters (user, 2026-11-16:
        // "the suggestions used should only compare the typed message with the start. So for example, typing "ac"
        // would suggest ActivatorRoll, but typing "roll" will not"); with nothing typed, every name. The likely
        // kind for the command first, then by name. A text that names nothing gets the missing-name warning
        // instead, with its closest names (Closest).
        return names.Likely.Select(x => (Name: x, Kind: 0))
            .Concat(names.Others.Select(x => (Name: x, Kind: 1)))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Where(x => x.Name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => x.Name)
            .ToList();
    }

    // only: look in this location alone (a gadget writes its variable in its own location); null for the whole
    // campaign (a dialogue's or a quest's action can run anywhere). Null when there is nothing to look in.
    internal static TargetNames Targets(string command, UserCampaign campaign, UserLocation only)
    {
        if (campaign == null && only == null)
        {
            return null;
        }

        var names = new TargetNames();
        var dialogue = command == DungeonMakerCommandsContext.DialogVariable;

        var places = only != null ? new List<UserLocation> { only } : campaign.UserLocations ?? [];

        foreach (var gadget in places.SelectMany(GadgetsOf))
        {
            var blueprint = KindOf(gadget);
            var npc = gadget.ParameterValues?
                .FirstOrDefault(x => x?.GadgetParameterDescription?.Type == GadgetBlueprintDefinitions.Type.Npc)?
                .StringValue;
            bool likely;

            if (dialogue)
            {
                likely = !string.IsNullOrEmpty(npc) || blueprint.Contains("Dialog");
            }
            else if (command == DungeonMakerCommandsContext.RollVariable)
            {
                likely = blueprint.Contains("ActivatorRoll");
            }
            else
            {
                likely = blueprint.Contains("Node");
            }

            Add(likely ? names.Likely : names.Others, gadget.UniqueName);

            if (dialogue)
            {
                Add(names.Likely, npc);
            }
        }

        if (dialogue)
        {
            foreach (var npc in campaign?.UserNpcs ?? [])
            {
                Add(names.Likely, npc?.InternalName);
                Add(names.Likely, npc?.Title);
            }
        }

        return names;

        static void Add(List<string> list, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                list.Add(name);
            }
        }
    }

    // What kind of gadget this is (its blueprint's name, e.g. ActivatorRoll, Node, NpcDialog). The gadget's own
    // GadgetBlueprintName is an empty string in the campaigns read so far: a diagnostic line in game on bac0aa92
    // listed 76 gadgets of a campaign, every one with an empty GadgetBlueprintName and its kind only in the name of
    // its GadgetBlueprint object ("ActivatorArea21:/ActivatorArea"). Reading only the former, no gadget was ever
    // of the likely kind, and with nothing typed the suggestions came in campaign order (Area Activators first for
    // Roll_Trigger). The blueprint object comes first; the name stays as a fallback for a gadget whose blueprint
    // isn't loaded.
    private static string KindOf(UserGadget gadget)
    {
        var blueprint = gadget.GadgetBlueprint;

        return blueprint ? blueprint.name : gadget.GadgetBlueprintName ?? string.Empty;
    }

    // how many letters have to be changed, added or taken out to turn one text into the other, whatever their case
    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var change = a[i - 1] == b[j - 1] ? 0 : 1;

                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + change);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    #endregion

    #region Every name, for the editor's list

    // Every name the mod uses, in the groups the editor's commands list shows them in (CustomUI/
    // DungeonMakerCommandsPopup): the title's translation term and the names. What each name does is NameHint's
    // text, so the list and the hints can't drift apart. The torch-by-name variable stands for all of its kind.
    internal static IEnumerable<(string TitleTerm, string[] Names)> Catalogue()
    {
        var statuses = Commands.Concat(DungeonMakerClockContext.SkipVariables)
            .Select(x => x + DungeonMakerCommandsContext.StatusSuffix)
            .ToArray();

        yield return ("ModUi/&DungeonMakerCommandsGroupCommands", Commands);
        yield return ("ModUi/&DungeonMakerCommandsGroupRequests",
        [
            DungeonMakerCommandsContext.ListVariable, DungeonMakerCommandsContext.TorchesVariable,
            DungeonMakerCommandsContext.TorchesAutoVariable, TorchByNameExample,
            .. DungeonMakerClockContext.SkipVariables, .. DungeonMakerClockContext.SwitchVariables
        ]);
        yield return ("ModUi/&DungeonMakerCommandsGroupReadouts",
            [.. DungeonMakerClockContext.ReadoutVariables, .. DungeonMakerClockContext.CounterVariables]);
        yield return ("ModUi/&DungeonMakerCommandsGroupStatuses", statuses);
        yield return ("ModUi/&DungeonMakerCommandsGroupOwn",
            [.. DungeonMakerClockContext.OwnVariables, DungeonMakerCommandsContext.SavedStateVariable]);
    }

    // how the list writes "Torch_ followed by a torch's name", as the guide does; the list is rich text, so it
    // escapes the angle brackets itself (DungeonMakerCommandsPopup)
    internal const string TorchByNameExample = DungeonMakerCommandsContext.TorchVariablePrefix + "<name>";

    #endregion

    #region What the editor says while a name is typed

    // The campaign being edited, read at most once a second (see EditorScanSeconds).
    private static CampaignUse EditorScan(UserCampaign campaign, UserLocation location)
    {
        var key = (object)campaign ?? location;
        var now = UnityEngine.Time.unscaledTime;

        if (!ReferenceEquals(key, _scannedFor) || now - _scannedAt > EditorScanSeconds || now < _scannedAt)
        {
            _scannedFor = key;
            _scannedAt = now;
            _scanned = Scan(campaign, location);
        }

        return _scanned;
    }

    private const string SkipHoursName = "Clock_SkipHours";
    private const string SkipDaysName = "Clock_SkipDays";

    // The unit each switch and counter counts in, in the order of DungeonMakerClockContext.SwitchVariables and
    // CounterVariables (minutes, hours, days): a word filled into the one switch text and the one counter text.
    private static readonly string[] ClockUnitTerms =
    [
        "ModUi/&DungeonMakerClockUnitMinute", "ModUi/&DungeonMakerClockUnitHour", "ModUi/&DungeonMakerClockUnitDay"
    ];

    // What to tell an author who is typing this as a variable's name: what the mod does with the name, and, for a
    // name the mod writes, where it writes with the campaign as it is now. Null when the mod doesn't use the name.
    internal static string NameHint(string name, UserCampaign campaign, UserLocation location)
    {
        var role = RoleOf(name, out var statusOf);
        string text;

        switch (role)
        {
            case Role.Command:
                text = Gui.Localize("ModUi/&DungeonMakerHint" + name.Replace("_", string.Empty));
                break;
            case Role.Request when name == DungeonMakerCommandsContext.ListVariable:
                text = Gui.Localize("ModUi/&DungeonMakerHintListGadgets");
                break;
            // the two skips by a number share one text, with the range and the unit filled in; the skips to a
            // moment each say what they aim at
            case Role.Request when name == SkipHoursName:
                text = Gui.Format("ModUi/&DungeonMakerHintClockSkipBy", "168",
                    Gui.Localize("ModUi/&DungeonMakerClockUnitHours"));
                break;
            case Role.Request when name == SkipDaysName:
                text = Gui.Format("ModUi/&DungeonMakerHintClockSkipBy", "180",
                    Gui.Localize("ModUi/&DungeonMakerClockUnitDays"));
                break;
            case Role.Request when Array.IndexOf(DungeonMakerClockContext.SkipVariables, name) >= 0:
                text = Gui.Localize("ModUi/&DungeonMakerHint" + name.Replace("_", string.Empty));
                break;
            // the three torch names each say what they do (one text for all three said less, and the list
            // showed it three times)
            case Role.Request when name == DungeonMakerCommandsContext.TorchesVariable:
                text = Gui.Localize("ModUi/&DungeonMakerHintTorchesLit");
                break;
            case Role.Request when name == DungeonMakerCommandsContext.TorchesAutoVariable:
                text = Gui.Localize("ModUi/&DungeonMakerHintTorchesAuto");
                break;
            case Role.Request:
                text = Gui.Localize("ModUi/&DungeonMakerHintTorchOne");
                break;
            // (user, 2026-11-17: the switch's hint "should outright mention Clock_MinutesElapsed", with one text
            // for every unit) a switch and its counter name each other, and the unit is a word filled in
            case Role.Switch:
            {
                var i = Array.IndexOf(DungeonMakerClockContext.SwitchVariables, name);

                text = Gui.Format("ModUi/&DungeonMakerHintClockCount",
                    DungeonMakerClockContext.CounterVariables[i], Gui.Localize(ClockUnitTerms[i]));
                break;
            }
            case Role.Counter:
            {
                var i = Array.IndexOf(DungeonMakerClockContext.CounterVariables, name);

                text = Gui.Format("ModUi/&DungeonMakerHintClockCounter",
                    DungeonMakerClockContext.SwitchVariables[i], Gui.Localize(ClockUnitTerms[i]));
                break;
            }
            // one text for every read-out, with what it holds filled in (Clock_Hour -> ...ClockHoldsHour)
            case Role.Readout:
                text = Gui.Format("ModUi/&DungeonMakerHintClockReadout",
                    Gui.Localize("ModUi/&DungeonMakerClockHolds" + name.Substring("Clock_".Length)));
                break;
            case Role.Status:
                text = Gui.Format("ModUi/&DungeonMakerHintStatus", statusOf);
                break;
            case Role.Internal:
                text = Gui.Localize("ModUi/&DungeonMakerHintInternal");
                break;
            default:
                return null;
        }

        // the saved-state variable is not moved by this rule (it never goes into a campaign's own variable)
        if (role is Role.Readout or Role.Status or Role.Internal &&
            name != DungeonMakerCommandsContext.SavedStateVariable)
        {
            var moved = EditorScan(campaign, location)?.FreeNameFor(name) ?? name;

            if (moved != name)
            {
                text += "\n" + Gui.Format("ModUi/&DungeonMakerHintWrittenAs", name, moved);
            }
        }

        return text;
    }

    // What to tell an author who is typing this text as the value a Variable Operation writes into a command
    // variable: nothing when something goes by that name, otherwise that nothing does, with the closest names.
    // location: the gadget's own location, or null for an action of a dialogue or a quest, which is looked up in
    // every location of the campaign.
    internal static string ValueHint(string variable, string value, UserCampaign campaign, UserLocation location)
    {
        if (string.IsNullOrEmpty(value) || RoleOf(variable, out _) != Role.Command)
        {
            return null;
        }

        var names = Targets(variable, campaign, location);

        if (names == null || names.Knows(value))
        {
            return null;
        }

        var text = Gui.Format(
            location != null ? "ModUi/&DungeonMakerHintNoGadgetLocation" : "ModUi/&DungeonMakerHintNoGadgetCampaign",
            value, variable);
        var closest = names.Closest(value);

        return closest.Count == 0
            ? text
            : text + "\n" + Gui.Format("ModUi/&DungeonMakerHintClosest", string.Join(", ", closest));
    }

    #endregion
}
