using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SolastaUnfinishedBusiness.Models;

// Reads the persistent element stream before compression. This includes guests, enemies, devices,
// merchant inventories and the cached locations whose entities may no longer exist in memory.
internal sealed class SaveDependencyClassifier(byte[] bytes)
{
    private static readonly HashSet<string> OfficialIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AddedIds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> OfficialNames = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AddedNames = new(StringComparer.Ordinal);
    private static bool _ready;
    private bool _uncertain;
    private bool _required;
    private bool _game;
    private bool _session;

    internal static void CacheOfficial(IEnumerable<BaseDefinition> definitions) =>
        Index(definitions, OfficialIds, OfficialNames);

    internal static void CacheAdded(IEnumerable<BaseDefinition> definitions)
    {
        Index(definitions, AddedIds, AddedNames);
        _ready = OfficialIds.Count > 0;
    }

    private static void Index(IEnumerable<BaseDefinition> definitions, HashSet<string> ids, HashSet<string> names)
    {
        foreach (var definition in definitions)
        {
            if (!definition) { continue; }
            if (!string.IsNullOrEmpty(definition.GUID)) { ids.Add(definition.GUID); }
            if (!string.IsNullOrEmpty(definition.Name)) { names.Add(definition.Name); }
        }
    }

    internal static SaveDependency Classify(MemoryStream payload)
    {
        try
        {
            if (CampaignRequirement.CommandsEnabled) { return SaveDependency.Required; }
            if (!_ready || !payload.TryGetBuffer(out var segment)) { return SaveDependency.Unknown; }
            var scan = new SaveDependencyClassifier(segment.Array);
            scan.Elements(segment.Offset, segment.Offset + segment.Count, 0, "");
            return scan._required ? SaveDependency.Required :
                scan._uncertain || !scan._game || !scan._session ? SaveDependency.Unknown : SaveDependency.NoKnownDependency;
        }
        catch (Exception e)
        {
            Main.Info($"Could not classify save: {e.Message}");
            return SaveDependency.Unknown;
        }
    }

    // All frame lengths include the size word itself. Validate a frame inside its parent before
    // treating its body as children: primitive elements store their value there without a type tag.
    private bool Text(ref int at, int end, int limit, out string value)
    {
        value = null;
        var length = 0;
        for (var shift = 0; shift <= 21 && at < end; shift += 7)
        {
            var b = bytes[at++];
            length |= (b & 127) << shift;
            if (length > limit) { return false; }
            if ((b & 128) != 0) { continue; }
            if (length > end - at) { return false; }
            value = Encoding.UTF8.GetString(bytes, at, length);
            at += length;
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

    private bool Header(ref int at, int end, out string name, out int attrEnd, out int next)
    {
        attrEnd = next = at;
        return Text(ref at, end, 512, out name) && name.Length > 0 && name[0] >= ' ' &&
               Frame(at, end, out next) && Frame(at + 4, next, out attrEnd);
    }

    private void Elements(int at, int end, int depth, string parent)
    {
        if (depth > 120) { _uncertain = true; return; }
        while (at < end && !_required)
        {
            var start = at;
            if (!Header(ref at, end, out var name, out var attrEnd, out var next))
            {
                // Dictionary entries contain a raw key before the framed Value. Non-string
                // keys use the declared key type's binary width. Keep unrecognized layouts unknown.
                at = start;
                if (Text(ref at, end, 512, out var key) &&
                    Header(ref at, end, out name, out attrEnd, out next) && name == "Value")
                {
                    if (parent is "ClassesAndLevels" or "ClassesAndSubclasses") { DefinitionName(key); }
                }
                else
                {
                    _uncertain = true;
                    return;
                }
            }

            if (depth == 0) { _game |= name == "Game"; _session |= name == "Session"; }
            // Level history is a trailing hero extension. Some of its entries use the game's
            // unprefixed Item.N reference names. Its following mode and alias markers are metadata.
            if (name == "UbLevelHistoryCount") { return; }
            if (!name.StartsWith("UbLevel", StringComparison.Ordinal))
            {
                Attributes(at + 8, attrEnd, depth);
                Body(name, parent, attrEnd, next, depth);
            }
            at = next;
        }
    }

    private void Attributes(int at, int end, int depth)
    {
        while (at < end)
        {
            if (!Frame(at, end, out var next)) { _uncertain = true; return; }
            at += 4;
            if (!Text(ref at, next, 512, out var key)) { _uncertain = true; return; }
            if (key == "GameLocationsData")
            {
                // Byte-array attribute starts with its payload length, followed by a GLD dictionary.
                if (next - at >= 4 && BitConverter.ToInt32(bytes, at) == next - at - 4)
                {
                    Elements(at + 4, next, depth + 1, key);
                }
                else { _uncertain = true; }
            }
            else if (key != "PortraitPhoto" && !Scalar(key, "", at, next) && next - at > 16)
            {
                // Opaque attributes can embed another snapshot, as GameLocationsData does.
                // Only the hero snapshot portrait is known to contain purely image bytes.
                _uncertain = true;
            }
            at = next;
        }
    }

    private void Body(string name, string parent, int at, int end, int depth)
    {
        if (at == end) { return; }
        if (name is "ClassesAndLevels" or "ClassesAndSubclasses")
        {
            // Hero class maps are packed (raw name/int or name/name), unlike ClassesHistory's
            // framed string items. A stock base class can independently carry a mod-only subclass.
            while (at < end)
            {
                if (!Text(ref at, end, 512, out var className) || className.Length == 0) { _uncertain = true; return; }
                DefinitionName(className);
                if (name == "ClassesAndLevels")
                {
                    if (end - at < 4) { _uncertain = true; return; }
                    if (BitConverter.ToInt32(bytes, at) > 16) { _required = true; }
                    at += 4;
                }
                else if (Text(ref at, end, 512, out var subclassName)) { DefinitionName(subclassName); }
                else { _uncertain = true; return; }
            }
            return;
        }
        // The embedded campaign's compressed asset JSON may contain references outside the ruleset
        // database. Without a complete audit of that asset, its negative classification is unknown.
        if (name == "UCJson") { _uncertain = true; return; }
        var probe = at;
        if (Header(ref probe, end, out _, out _, out _) ||
            (TryDictionaryStart(at, end)))
        {
            Elements(at, end, depth + 1, name);
            return;
        }
        if (!Scalar(name, parent, at, end) && end - at > 16) { _uncertain = true; }
    }

    private bool TryDictionaryStart(int at, int end) =>
        Text(ref at, end, 512, out _) && Header(ref at, end, out var field, out _, out _) && field == "Value";

    private bool Scalar(string field, string parent, int at, int end)
    {
        if (field == "ExtraCharges" && end - at == 4 && BitConverter.ToInt32(bytes, at) > 0)
        {
            // Vanilla spells also serialize extra charges. A mod-specific power effect is identified
            // by its serialized CLR type; a charge count on its own can belong to a stock item.
            return true;
        }
        var reference = field.EndsWith("StringGuid", StringComparison.Ordinal);
        var named = field is "CampaignDefinitionName" or "LocationDefinitionName" ||
                    parent is "ClassesHistory" or "ClassesAndSubclasses";
        if (!Text(ref at, end, 16384, out var value) || at != end)
        {
            if (reference || named) { _uncertain = true; }
            return false;
        }
        if (value.Length == 0) { return true; }
        if (reference)
        {
            if (AddedIds.Contains(value) && !OfficialIds.Contains(value)) { _required = true; }
            else if (!OfficialIds.Contains(value)) { _uncertain = true; }
        }
        else if (named) { DefinitionName(value); }
        else if (field == "Type" && value.Contains(","))
        {
            var assembly = value.Split(',')[1].Trim();
            if (assembly == typeof(Main).Assembly.GetName().Name) { _required = true; }
            else if (assembly != "Assembly-CSharp" && assembly != "mscorlib" && !assembly.StartsWith("UnityEngine", StringComparison.Ordinal))
            { _uncertain = true; }
        }
        return true;
    }

    private void DefinitionName(string name)
    {
        if (OfficialNames.Contains(name)) { return; }
        if (AddedNames.Contains(name)) { _required = true; }
        else { _uncertain = true; }
    }
}
