using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SolastaUnfinishedBusiness.Models;

// Walks the persistent save before compression, including the snapshots of unloaded locations.
internal sealed partial class SaveDependencyClassifier(byte[] bytes, Action<string, string, string> note = null)
{
    private sealed class Index
    {
        internal readonly HashSet<string> Ids = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, HashSet<string>> Names = new(StringComparer.Ordinal);

        internal void Add(string type, string name)
        {
            if (!Names.TryGetValue(type, out var names)) { Names[type] = names = new(StringComparer.Ordinal); }
            names.Add(name);
        }

        internal bool Contains(string type, string name) => Names.TryGetValue(type, out var names) && names.Contains(name);
    }

    private static readonly Index Official = new(), Added = new();
    private static readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Bases = new(StringComparer.Ordinal);
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private bool _required, _uncertain, _game, _session;
    private string _path;
    private static bool _ready;
    private static volatile bool _layoutsLoaded;
    private static readonly object LayoutsLock = new();
    private readonly Action<string, string, string> _note = note;

    // Parsed on the first classification, inside its try/catch, so a bad table makes saves unknown rather than
    // failing the game's save. The rows are parsed into new dictionaries and copied into the shared ones only
    // once all of them parsed, under a lock, so no classification walks a partly filled table; a failed parse
    // leaves the tables empty and is tried again on the next save.
    private static void LoadLayouts()
    {
        if (_layoutsLoaded) { return; }
        lock (LayoutsLock)
        {
            if (_layoutsLoaded) { return; }
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var bases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var row in SaveDependencyLayouts.Rows.Split('\n'))
            {
                var f = row.TrimEnd('\r').Split('|');
                if (f.Length != 4) { continue; }
                if (f[1] == "B") { bases[f[0]] = f[3]; continue; }
                var key = f[0] + "|" + f[1] + "|" + f[2];
                // Multiple incompatible declarations at the same site cannot identify a wire layout.
                fields[key] = fields.TryGetValue(key, out var existing) && existing != f[3] ? "?" : f[3];
            }
            foreach (var pair in fields) { Fields[pair.Key] = pair.Value; }
            foreach (var pair in bases) { Bases[pair.Key] = pair.Value; }
            _layoutsLoaded = true;
        }
    }

    internal static void CacheOfficial(IEnumerable<BaseDefinition> definitions) => Cache(Official, definitions);
    internal static void CacheAdded(IEnumerable<BaseDefinition> definitions)
    {
        Cache(Added, definitions);
        _ready = Official.Ids.Count > 0;
    }

    private static void Cache(Index index, IEnumerable<BaseDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (!definition) { continue; }
            if (!string.IsNullOrEmpty(definition.GUID)) { index.Ids.Add(definition.GUID); }
            if (string.IsNullOrEmpty(definition.Name)) { continue; }
            // A derived feature can be referenced through the base feature database.
            for (var type = definition.GetType(); typeof(BaseDefinition).IsAssignableFrom(type); type = type.BaseType)
            {
                index.Add(type.FullName, definition.Name);
            }
        }
    }

    internal static SaveDependency Classify(MemoryStream payload)
    {
        try
        {
            if (CampaignRequirement.CommandsEnabled) { return SaveDependency.Required; }
            LoadLayouts();
            if (!_ready || !payload.TryGetBuffer(out var segment)) { return SaveDependency.Unknown; }
            return new SaveDependencyClassifier(segment.Array).Walk(segment.Offset, segment.Offset + segment.Count);
        }
        catch (Exception e)
        {
            Main.Info($"Could not classify save: {e.Message}");
            return SaveDependency.Unknown;
        }
    }

    private SaveDependency Walk(int at, int end)
    {
        Children(at, end, "$", 0);
        return _required ? SaveDependency.Required :
            _uncertain || !_game || !_session ? SaveDependency.Unknown : SaveDependency.NoKnownDependency;
    }

    private void Unknown(string owner, string field, int at)
    {
        _uncertain = true;
        _note?.Invoke(_path + " [" + owner + "/" + field + "]", "unknown", at.ToString());
    }

    private bool Text(ref int at, int end, out string value)
    {
        value = null;
        uint size = 0;
        for (var shift = 0; shift <= 28 && at < end; shift += 7)
        {
            var b = bytes[at++];
            if (shift == 28 && b > 7) { return false; }
            size |= (uint)(b & 127) << shift;
            if ((b & 128) != 0) { continue; }
            if (size > end - at) { return false; }
            try { value = Utf8.GetString(bytes, at, (int)size); }
            catch (DecoderFallbackException) { return false; }
            at += (int)size;
            return true;
        }
        return false;
    }

    private bool Frame(int at, int end, out int next)
    {
        next = at;
        if (end - at < 4) { return false; }
        var size = BitConverter.ToInt32(bytes, at);
        if (size < 4 || size > end - at) { return false; }
        next = at + size;
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex HeroFeature = new(
        @"^FeatureDefinition\.Tag[0-9]+\.[0-9]+(StringGuid)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string Pattern(string field)
    {
        // Reference lists encode a decimal index in each sibling's name.
        var dot = field.LastIndexOf('.');
        if (dot < 0) { return field; }
        var i = dot + 1;
        while (i < field.Length && field[i] >= '0' && field[i] <= '9') { i++; }
        return i == dot + 1 ? field : field.Substring(0, dot + 1) + "#" + field.Substring(i);
    }

    private static bool IsDerivedFrom(string type, string expected)
    {
        while (type != null)
        {
            if (type == expected) { return true; }
            Bases.TryGetValue(type, out type);
        }
        return false;
    }

    private static string Layout(string owner, string kind, string field)
    {
        // The following maps carry database names as untagged strings. Adjacent gadget
        // keys, flow names and quest outcome strings use the ordinary text reader.
        switch ((owner, kind, field))
        {
            case ("GameLocationEffect", "A", "Direction"): return "12";
            // The identically named field on GameAdventureEntryLoreText contains prose.
            // The quest log entry writes GameQuest.QuestTreeDefinition.Name under that key.
            case ("GameAdventureEntryQuest", "A", "NarrativeTreeDefinitionName"): return "@QuestTreeDefinition";
            case ("GameAdventureConversationInfo", "A", "ItemDefinitionName"):
            case ("GameQuestValidatorItemEquiped", "A", "ItemDefinitionName"):
            case ("GameQuestValidatorItemUsed", "A", "ItemDefinitionName"): return "@ItemDefinition";
            case ("GameQuestValidatorSpellCast", "A", "SpellDefinitionName"): return "@SpellDefinition";
            case ("RulesetCharacterHero+Snapshot", "A", "Race"):
            case ("RulesetCharacterHero+Snapshot", "A", "SubRace"): return "@CharacterRaceDefinition";
            case ("GameLocationBattle", "E", "DefeatedMonsters"):
            case ("GameAdventureEntryBattle", "E", "MonsterNumbers"): return "D:@MonsterDefinition:4";
            case ("GameAchievementsData", "E", "CraftingCountByTool"): return "D:@ToolTypeDefinition:4";
            case ("GameCampaignMap", "E", "IntroducedLocationNames"): return "L:@LocationDefinition";
            case ("RulesetCharacterHero+Snapshot", "E", "Classes"): return "L:@CharacterClassDefinition";
            case ("RulesetCharacterHero+Snapshot", "E", "Subclasses"): return "L:@CharacterSubclassDefinition";
            case ("GameFactionManager", "E", "DynamicProminentMembers"): return "D:@FactionDefinition:L:@MonsterDefinition";
            case ("GameFactionManager", "E", "FactionRelations"): return "D:@FactionDefinition:4";
            case ("RulesetCharacterHero", "E", "ClassesAndLevels"): return "D:@CharacterClassDefinition:level";
            case ("RulesetCharacterHero", "E", "ClassesAndSubclasses"): return "D:@CharacterClassDefinition:@CharacterSubclassDefinition";
            case ("RulesetCharacterHero", "E", "ClassesHistory"): return "L:@CharacterClassDefinition";
            case ("Session", "E", "CampaignDefinitionName"):
            case ("GameSaveDescriptor", "A", "CampaignDefinitionName"): return "@CampaignDefinition";
            case ("GameSaveDescriptor", "A", "LocationDefinitionName"): return "@LocationDefinition";
            case ("$", "E", "VersionNumber"): return "8";
            // Present only in Dungeon Maker saves. A standalone location's save holds no copy of the location
            // (no UCJson), and the game rebuilds it from the location's own file, whose rooms and gadgets may be
            // the mod's, so a named user location or campaign makes the save unknown.
            case ("Session", "E", "UserCampaignName"):
            case ("Session", "E", "UserLocationName"):
            case ("GameSaveDescriptor", "A", "UserCampaignName"):
            case ("GameSaveDescriptor", "A", "UserLocationName"): return "usercontent";
            case ("GameCampaignMap", "E", "AllElementIds"): return "L:text";
            // The mod's per-hero spell system: true means the hero casts from the mod's spell-point pool.
            case ("RulesetCharacterHero", "E", "FbUsesSpellPoints"): return "modflag";
            // Marks a Monk whose stock Patient Defense grant is saved under the game's own name; the game skips the
            // element on load, so it is no dependency either way.
            case ("RulesetCharacterHero", "E", "FbMonkPatientDefenseClassGrant"): return "bool";
            case ("$", "E", "GameLocation"): return "GameLocation";
            case ("$", "E", "Session"): case ("$", "E", "Game"): case ("$", "E", "GameSaveDescriptor"): return field;
            case ("GameLocationManager", "A", "GameLocationsData"): return "cache";
            case ("cache", "E", "GLD"): return "D:text:GameLocationPersistentData";
        }

        if (owner == "RulesetCharacterHero" && kind == "E")
        {
            if (HeroFeature.IsMatch(field)) { return field.EndsWith("StringGuid", StringComparison.Ordinal) ? "guid" : "@FeatureDefinition"; }
            if (field.StartsWith("Tag", StringComparison.Ordinal) && uint.TryParse(field.Substring(3), out _)) { return "text"; }
            if (field.StartsWith("FeaturesCount", StringComparison.Ordinal) && uint.TryParse(field.Substring(13), out _)) { return "4"; }
        }
        var pattern = Pattern(field);
        var current = owner;
        while (current != null)
        {
            if (kind == "A" && field == current + ".Version") { return "4"; }
            if (current == "RulesetCharacter" && kind == "E" && field == "RegenerationLapseByRegenerationFeatureName")
            {
                return "D:@FeatureDefinitionRegeneration:4";
            }
            if (current == "RulesetCharacter" && kind == "E" && field == "MorphotypeElements") { return "D:text:@MorphotypeElementDefinition"; }
            // Effect names the game looks up by name in its databases. Each is matched against every database.
            // An effect proxy's name is always its source definition's (spells, then features, are searched when it
            // is used), so a name in no database makes the save unknown; the other two may hold other text, which
            // is ignored.
            if ((current, kind, field) is ("RulesetCharacterEffectProxy", "E", "EffectDefinitionName"))
            {
                return "sourcename";
            }
            if ((current, kind, field) is ("RulesetCondition", "E", "EffectDefinitionName") or
                ("GameLocationEffect", "A", "EffectSourceName"))
            {
                return "anyname";
            }
            if (Fields.TryGetValue(current + "|" + kind + "|" + pattern, out var shape)) { return shape; }
            Bases.TryGetValue(current, out current);
        }
        if (kind == "E")
        {
            if (owner is "Game" or "GameLocation")
            {
                if (field == "SubmanagerList") { return "L:text"; }
                if (Bases.TryGetValue(field, out var parent) && parent is "GameSubmanager" or "GameLocationSubmanager") { return field; }
            }

        }
        return null;
    }

    // Reference-list helpers reuse the same item title across distinct lists. The
    // preceding count identifies the database and bounds the following indices.
    private sealed class Siblings
    {
        internal string ReferencePrefix, ReferenceDatabase;
        internal int ReferenceCount, MapGroup;
        internal HashSet<string> DynamicNames;
        internal bool History, HistoryItems;
    }

    // Level history is optional bookkeeping and is ignored by vanilla. Validate its
    // specific primitive and list layouts without treating its undo records as live dependencies.
    private static readonly System.Text.RegularExpressions.Regex HistoryField = new(
        @"^UbLevel[0-9]+\.(?:(?:FeatureTag|Repertoire)[0-9]+\.)?(?:Extra[0-9]+\.)?([A-Za-z]+)$");

    private static readonly System.Text.RegularExpressions.Regex HistoryReferenceCount = new(
        @"^(?:FightingStyles|Skills|ToolTypes|Feats|Languages|MetamagicOptions|Invocations|Cantrips|Spells|Prepared)?(?:Added|Removed)Count$");

    private static string HistoryShape(string field, Siblings siblings)
    {
        var numbered = Pattern(field);
        if (siblings.HistoryItems && numbered is "Item.#" or "Item.#StringGuid") { return "text"; }
        var match = HistoryField.Match(field);
        if (!match.Success) { return null; }
        var suffix = match.Groups[1].Value;
        siblings.HistoryItems = HistoryReferenceCount.IsMatch(suffix);
        if (siblings.HistoryItems) { return "4"; }
        return suffix switch
        {
            "Level" or "ClassLevel" or "FeatureTagCount" or "RepertoireCount" or "ExtraCount" => "4",
            "ExpertisesAdded" or "ExpertisesRemoved" or "AttributeNames" => "L:text",
            "AttributeDeltas" => "L:4",
            "Created" or "IsNewTag" => "bool",
            "Tag" or "Class" or "Subclass" or "AddedSubclass" or "Feature" or "Race" or
                "ClassStringGuid" or "SubclassStringGuid" or "AddedSubclassStringGuid" or
                "FeatureStringGuid" or "RaceStringGuid" => "text",
            _ => null
        };
    }

    private void Children(int at, int end, string owner, int depth)
    {
        var siblings = new Siblings();
        while (at < end && (!_required || _note != null))
        {
            if (!Element(ref at, end, owner, null, null, depth, siblings)) { Unknown(owner, "frame", at); return; }
        }
    }

    private bool Element(ref int at, int end, string owner, string expected, string shape, int depth, Siblings siblings = null)
    {
        if (depth > 120 || !Text(ref at, end, out var field) || field.Length == 0 ||
            (expected != null && expected != field) || !Frame(at, end, out var next) ||
            !Frame(at + 4, next, out var body)) { return false; }
        var attr = at + 8;
        at = next;
        var parentPath = _path;
        if (_note != null) { _path = parentPath + "/" + field; }
        var valid = ReadElement(owner, expected, shape, depth, siblings, field, attr, body, next);
        _path = parentPath;
        return valid;
    }

    private bool ReadElement(string owner, string expected, string shape, int depth, Siblings siblings, string field,
        int attr, int body, int next)
    {
        if (owner == "$") { _game |= field == "Game"; _session |= field == "Session"; }
        shape ??= Layout(owner, "E", field);
        if (owner == "RulesetCharacterHero" && siblings != null)
        {
            if (field == "UbLevelHistoryCount" && next - body == 4)
            {
                siblings.History = BitConverter.ToInt32(bytes, body) >= 0;
                shape = "4";
            }
            else if (siblings.History)
            {
                shape ??= HistoryShape(field, siblings);
            }
        }
        if (siblings != null && owner == "GameSector" && siblings.DynamicNames?.Contains(field) == true)
        {
            shape = shape == null ? "GameGadget" : "?";
        }
        if (siblings != null && owner == "GameCampaignMap")
        {
            if (field == "AllElementIds") { siblings.MapGroup++; }
            else if (shape == null && siblings.DynamicNames?.Contains(field) == true)
            {
                shape = siblings.MapGroup switch { 1 => "GameCampaignNode", 2 => "GameCampaignSegment", _ => null };
            }
        }
        if (shape != null && shape.StartsWith("scope:", StringComparison.Ordinal) && siblings != null)
        {
            var parts = shape.Split(':');
            siblings.ReferencePrefix = parts[1] + ".";
            siblings.ReferenceDatabase = parts[2];
            siblings.ReferenceCount = next - body == 4 ? BitConverter.ToInt32(bytes, body) : 0;
            shape = "4";
        }
        if (siblings?.ReferencePrefix != null && field.StartsWith(siblings.ReferencePrefix, StringComparison.Ordinal) &&
            uint.TryParse(field.Substring(siblings.ReferencePrefix.Length), out var index) && index < siblings.ReferenceCount)
        {
            shape = "@" + siblings.ReferenceDatabase;
        }
        // A framed subtree does not establish its owner's CLR type by resemblance.
        // Preserve its outer boundary so later siblings can still provide positive evidence.
        if (shape is null or "?") { Unknown(owner, field, body); return true; }
        var count = -1;
        var hasType = false;
        bool? isNull = null;
        // Type precedes the object's versioned attributes and selects a derived serializer.
        while (attr < body)
        {
            if (!Frame(attr, body, out var after)) { return false; }
            var v = attr + 4;
            if (!Text(ref v, after, out var key)) { return false; }
            if (key == "Type")
            {
                if (hasType || IsScalar(shape) || shape.StartsWith("L:", StringComparison.Ordinal) ||
                    shape.StartsWith("D:", StringComparison.Ordinal)) { return false; }
                if (!Text(ref v, after, out var type) || v != after) { return false; }
                hasType = true;
                var parts = type.Split(',');
                if (parts.Length < 2) { return false; }
                _note?.Invoke(_path, "type", type);
                if (parts[1].Trim() == "SolastaUnfinishedBusiness") { _required = true; }
                else if (parts[1].Trim() != "Assembly-CSharp" || !IsDerivedFrom(parts[0], shape))
                { Unknown(owner, field, v); return true; }
                shape = parts[0];
            }
            else if (key == "Count" && after - v == 4 && count == -1)
            {
                count = BitConverter.ToInt32(bytes, v);
                if (count < 0) { return false; }
            }
            else if (key == "Null" && after - v == 1 && bytes[v] <= 1 && isNull == null) { isNull = bytes[v] != 0; }
            else if (key is "Null" or "Count") { return false; }
            else
            {
                var wire = Layout(shape, "A", key);
                if (wire == "cache" && after - v >= 4 && BitConverter.ToInt32(bytes, v) == after - v - 4)
                {
                    Children(v + 4, after, "cache", depth + 1);
                }
                else if (wire == "L:1" && after - v >= 4 && BitConverter.ToInt32(bytes, v) == after - v - 4) { }
                else
                {
                    var previous = _required;
                    if (!Scalar(wire, ref v, after, key) || v != after)
                    {
                        _required = previous;
                        Unknown(shape, key, v);
                    }
                }
            }
            attr = after;
        }

        if (field == "UCJson" && owner == "Session") { Unknown(owner, field, body); return true; }
        if (isNull == true)
        {
            return owner != "$" && count == -1 && body == next;
        }
        if (shape != null && (shape.StartsWith("D:", StringComparison.Ordinal) || shape.StartsWith("L:", StringComparison.Ordinal)))
        {
            var previous = _required;
            // Dynamically named children of sectors and persistent arrays are identified by
            // the preceding list of names in this same object.
            var dynamicList = siblings != null && ((owner == "GameSector" && field == "AllGadgetNames") ||
                                                     (owner == "GameCampaignMap" && field == "AllElementIds"));
            var names = dynamicList ? new HashSet<string>(StringComparer.Ordinal) : null;
            // Solasta without the mod has one class per hero and stops at level 16, so a second class, or a class
            // history (one entry per level gained) longer than 16, needs the mod.
            if (owner == "RulesetCharacterHero" &&
                ((field == "ClassesAndLevels" && count > 1) || (field == "ClassesHistory" && count > 16)))
            {
                _required = true;
            }
            if (!Collection(shape, field, body, next, count, depth, names))
            {
                // A prefix of a malformed packed run cannot establish a dependency.
                _required = previous;
                Unknown(owner, field, body);
                if (dynamicList) { siblings.DynamicNames = null; }
            }
            else if (dynamicList) { siblings.DynamicNames = names; }
        }
        else if (shape != null && IsScalar(shape))
        {
            var valueStart = body;
            var previous = _required;
            if (count >= 0 || !Scalar(shape, ref body, next, field) || body != next)
            {
                _required = previous;
                Unknown(owner, field, body);
            }
            else if (shape == "text" && expected != null && siblings?.DynamicNames != null &&
                     Text(ref valueStart, next, out var name)) { siblings.DynamicNames.Add(name); }
        }
        else
        {
            if (count >= 0) { Unknown(owner, field, body); }
            Children(body, next, shape, depth + 1);
        }
        return true;
    }

    private bool Collection(string shape, string field, int at, int end, int count, int depth, HashSet<string> names)
    {
        if (count < 0 || count > end - at) { return false; }
        if (shape.StartsWith("L:", StringComparison.Ordinal))
        {
            var item = shape.Substring(2);
            for (var i = 0; i < count; i++)
            {
                var metadata = names != null ? new Siblings { DynamicNames = names } : null;
                if (!Element(ref at, end, field, field + "Item", item, depth + 1, metadata)) { return false; }
            }
        }
        else
        {
            var split = shape.IndexOf(':', 2);
            if (split < 0) { return false; }
            var key = shape.Substring(2, split - 2);
            var value = shape.Substring(split + 1);
            for (var i = 0; i < count; i++)
            {
                if (!Member(key, "Key", ref at, end, field, depth) ||
                    !Member(value, "Value", ref at, end, field, depth)) { return false; }
            }
        }
        return at == end;
    }

    // A dictionary packs each simple side independently. Lists always frame every item.
    private bool Member(string shape, string side, ref int at, int end, string field, int depth) =>
        IsScalar(shape) ? Scalar(shape, ref at, end, field) : Element(ref at, end, field, side, shape, depth + 1);

    private static bool IsScalar(string shape) => shape is "bool" or "modflag" or "text" or "usercontent" or "anyname" or "sourcename" or "bits" or "guid" or "level" or "char" ||
        shape[0] is >= '0' and <= '9' or '@' || shape.StartsWith("ref:", StringComparison.Ordinal);

    private bool Scalar(string shape, ref int at, int end, string field)
    {
        if (string.IsNullOrEmpty(shape)) { return false; }
        if (shape is "text" or "guid" || shape[0] == '@' || shape.StartsWith("ref:", StringComparison.Ordinal))
        {
            if (!Text(ref at, end, out var value)) { return false; }
            if (value.Length == 0 || shape == "text") { return true; }
            var guid = shape == "guid";
            var db = guid ? "" : shape.Substring(shape[0] == '@' ? 1 : 4);
            _note?.Invoke(_path + "|" + field, guid ? "guid" : db, value);
            if (guid ? Official.Ids.Contains(value) : Official.Contains(db, value)) { return true; }
            if (guid ? Added.Ids.Contains(value) : Added.Contains(db, value)) { _required = true; }
            else { Unknown(guid ? "GUID" : db, value, at); }
            return true;
        }
        if (shape is "usercontent" or "anyname" or "sourcename")
        {
            if (!Text(ref at, end, out var value)) { return false; }
            if (value.Length == 0) { return true; }
            if (shape == "usercontent") { Unknown("UserContent", field, at); return true; }
            _note?.Invoke(_path + "|" + field, "any", value);
            if (Official.Contains(nameof(BaseDefinition), value)) { return true; }
            if (Added.Contains(nameof(BaseDefinition), value)) { _required = true; }
            else if (shape == "sourcename") { Unknown(nameof(BaseDefinition), value, at); }
            return true;
        }
        if (shape == "bits")
        {
            if (end - at < 8) { return false; }
            var bits = BitConverter.ToInt32(bytes, at);
            var length = BitConverter.ToInt32(bytes, at + 4);
            if (bits < 0 || length != (bits + 7L) / 8 || length > end - at - 8) { return false; }
            at += 8 + length;
            return true;
        }
        if (shape == "char")
        {
            if (at == end) { return false; }
            var size = bytes[at] < 128 ? 1 : bytes[at] < 224 ? 2 : 3;
            if (size > end - at) { return false; }
            try { if (Utf8.GetCharCount(bytes, at, size) != 1) { return false; } }
            catch (DecoderFallbackException) { return false; }
            at += size;
            return true;
        }
        // "modflag" is a bool written by the mod itself, where true is a dependency on the mod.
        if (shape is "bool" or "modflag")
        {
            if (at == end || bytes[at] > 1) { return false; }
            if (shape == "modflag" && bytes[at] == 1) { _required = true; }
            at++;
            return true;
        }
        var width = shape switch { "1" => 1, "2" => 2, "4" or "level" => 4, "8" => 8, "12" => 12, "16" => 16, _ => 0 };
        if (width == 0 || width > end - at) { return false; }
        if (shape == "level" && BitConverter.ToInt32(bytes, at) > 16) { _required = true; }
        at += width;
        return true;
    }
}
