using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace SolastaUnfinishedBusiness.Models;

// The mod's version in the campaigns and locations the Dungeon Maker saves, development builds included.
//
// WHERE IT GOES, and why there. A campaign file (and a location file) is the text Unity's JsonUtility.ToJson makes
// of the UserCampaign object, written by UserCampaignPoolManager.SaveUserCampaign (UserLocationPoolManager.
// SaveUserLocation), which also hands the same bytes to the platform's cloud storage. The mod adds one field of
// its own at the very start of that text:
//     {"finishingBusinessVersion":"1.18.0-dev.0330274b","dungeonMakerVersion":...
//   - Reading is not disturbed: JsonUtility.FromJson ignores a field the object has no member for. The game
//     relies on that itself: it first reads the whole campaign text as the much smaller UserContent, to check its
//     kind (ReadCampaignFromDisk). So a game without the mod loads the file as before.
//   - Nothing an author sees or edits is used (the description, the "technical info" text), and no campaign
//     variable is made.
//   - It stays true: a game without the mod that saves the campaign again writes only the object's own fields,
//     so the field is gone, and the file no longer claims a mod version that didn't write it. A field kept in the
//     object (in one of its text fields) would be carried along and go stale.
//   - The text is changed where the game makes it (the call to ToJson in the two save methods is replaced by
//     StampedJson), so the file and the cloud copy are the same.
//
// WHAT IS RECORDED: the version of the code that is running (UpdateContext.LoadedBuildVersion): "1.18.0" for a
// release, "<version>-dev.<first 8 characters of the commit>" for a development build.
//
// HOW TWO VERSIONS ARE ORDERED is the updater's business, not decided here: UpdateContext.CompareVersions is
// used as it is. That is Semantic Versioning's pre-release order: a development build carries the
// NEXT version and is older than the release with the same numbers (1.17.0 < 1.17.1 < 1.18.0-dev.<commit> <
// 1.18.0).
// "Equal" only means "the same numbers": two development builds of one version differ by their commit alone,
// which says which code it was, not which is newer, so the log then says that the commits differ.
//
// WHAT IS DONE WITH IT: nothing is compared or refused yet. The version a campaign file was saved with is
// remembered when the file is read and written to the log, there and when the campaign is played.
internal static class DungeonMakerStampContext
{
    private const string FieldName = "finishingBusinessVersion";
    private const string FieldStart = "{\"" + FieldName + "\":\"";

    // JsonUtility.ToJson(object), the call the game makes its file text with. Looked up by name: the type lives in
    // a Unity module this project does not reference.
    public static readonly MethodInfo ToJsonMethod =
        AccessTools.Method("UnityEngine.JsonUtility:ToJson", [typeof(object)]);

    private static readonly Func<object, string> ToJson = ToJsonMethod == null
        ? null
        : (Func<object, string>)Delegate.CreateDelegate(typeof(Func<object, string>), ToJsonMethod);

    // the stamp of the file text read last (see NoteRead), until the reading method's postfix takes it
    private static string _lastRead;

    // The campaign SaveUserCampaign wrote last since the last file text was read. ReadCampaignFromDisk saves the
    // campaign it reads itself in some cases (after PostLoadJson, and when the file name doesn't match the title;
    // three calls to SaveUserCampaign before it returns), so by the time its postfix runs, the file may carry the
    // running version instead of the stamp read from it.
    private static UserCampaign _savedSinceRead;

    // per campaign title, the version its file was saved with (campaigns are known to the game by their title)
    private static readonly Dictionary<string, string> CampaignStamps = [];

    // the campaign whose version was last written to the log on being played, so it is said once per campaign
    private static string _loggedPlaying;

    // Replaces JsonUtility.ToJson(object) in SaveUserCampaign and SaveUserLocation: the same text, with the mod's
    // field first. Whatever goes wrong, the game's own text is returned.
    public static string StampedJson(object content) => StampedJson(content, false);

    internal static string StampedJson(object content, bool session)
    {
        var json = ToJson(content);

        try
        {
            var version = UpdateContext.LoadedBuildVersion ?? Main.ModVersion;

            // only a JSON object with at least one field of its own, and a version that needs no escaping
            if (string.IsNullOrEmpty(version) || version.IndexOfAny(['"', '\\']) >= 0 || json == null ||
                json.Length < 3 || json[0] != '{' || json[1] != '"')
            {
                return json;
            }

            var flag = content is UserCampaign campaign ? CampaignRequirement.FieldJson(campaign, session) : "";
            return FieldStart + version + "\"," + flag + json.Substring(1);
        }
        catch (Exception)
        {
            return json;
        }
    }

    // Replaces TextReader.ReadToEnd() in ReadCampaignFromDisk: the same text, with the mod's field noted when the
    // text starts with it (it only ever does when this mod wrote the file).
    public static string ReadNoted(TextReader reader)
    {
        var text = reader.ReadToEnd();

        _lastRead = null;
        _savedSinceRead = null;

        try
        {
            if (text != null && text.StartsWith(FieldStart, StringComparison.Ordinal))
            {
                var end = text.IndexOf('"', FieldStart.Length);

                // a version is short; anything else is not ours
                if (end > FieldStart.Length && end - FieldStart.Length <= 64)
                {
                    _lastRead = text.Substring(FieldStart.Length, end - FieldStart.Length);
                }
            }
        }
        catch (Exception)
        {
            _lastRead = null;
        }

        return text;
    }

    // UserCampaignPoolManager.ReadCampaignFromDisk, after it ran: the campaign it read, or null when the file was
    // not a campaign. Remembers the version noted from the file's text, and says it in the log. When the game saved
    // that campaign again while reading it, the file now carries what that save wrote (NoteCampaignSaved has
    // recorded it), and the stamp read before is only said in the log.
    internal static void NoteCampaignRead(UserCampaign campaign)
    {
        var stamp = _lastRead;
        var rewritten = campaign != null && ReferenceEquals(_savedSinceRead, campaign);

        _lastRead = null;
        _savedSinceRead = null;

        if (campaign?.Title == null)
        {
            return;
        }

        if (rewritten)
        {
            Main.Info($"DM campaign '{campaign.Title}' was " + (stamp == null
                          ? "saved without a mod version"
                          : $"last saved with the mod at version {stamp}") +
                      "; the game saved it again while reading it, so the file now carries " +
                      (CampaignStamps.TryGetValue(campaign.Title, out var now) ? now : "no mod version") + ".");

            return;
        }

        if (stamp == null)
        {
            CampaignStamps.Remove(campaign.Title);

            return;
        }

        if (!CampaignStamps.TryGetValue(campaign.Title, out var known) || known != stamp)
        {
            Main.Info($"DM campaign '{campaign.Title}' was last saved with the mod at version {stamp}" +
                      Compared(stamp) + ".");
        }

        CampaignStamps[campaign.Title] = stamp;
    }

    // UserCampaignPoolManager.SaveUserCampaign, after it ran, with the file it writes. The game catches any error
    // while saving and only logs it, so returning says nothing about the file: it counts as saved with the running
    // version only when its text now starts with that version's stamp.
    internal static void NoteCampaignSaved(UserCampaign campaign, string path)
    {
        var version = UpdateContext.LoadedBuildVersion ?? Main.ModVersion;

        if (campaign?.Title == null || string.IsNullOrEmpty(version) || !StartsWithStamp(path, version, campaign))
        {
            return;
        }

        CampaignStamps[campaign.Title] = version;
        CampaignRequirement.Saved(campaign);
        _savedSinceRead = campaign;
    }

    // whether the file at path starts with the mod's field holding this version (only its first characters are read)
    private static bool StartsWithStamp(string path, string version, UserCampaign campaign)
    {
        var expected = FieldStart + version + "\"," + CampaignRequirement.FieldJson(campaign, false);

        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            using var reader = new StreamReader(path);
            var buffer = new char[expected.Length];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);

            return read == expected.Length && new string(buffer) == expected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // DungeonMakerCommandsContext.OnLocationReady: a location of this campaign has loaded. Says once per campaign
    // which mod version its file was saved with, or that it carries none.
    internal static void NotePlaying(UserCampaign campaign)
    {
        var title = campaign?.Title;

        if (title == null || title == _loggedPlaying)
        {
            return;
        }

        _loggedPlaying = title;

        Main.Info(CampaignStamps.TryGetValue(title, out var stamp)
            ? $"DM campaign '{title}' being played was last saved with the mod at version {stamp}{Compared(stamp)}."
            : $"DM campaign '{title}' being played carries no mod version (saved without the mod, or before " +
              "the mod recorded it).");
    }

    // " (older than ...)" and the like, for the log; nothing acts on it. The order is UpdateContext.CompareVersions'
    // (see "HOW TWO VERSIONS ARE ORDERED" at the top).
    private static string Compared(string stamp)
    {
        var running = UpdateContext.LoadedBuildVersion ?? Main.ModVersion;

        if (string.IsNullOrEmpty(stamp) || string.IsNullOrEmpty(running))
        {
            return string.Empty;
        }

        if (stamp == running)
        {
            return " (the running version)";
        }

        int order;

        try
        {
            order = UpdateContext.CompareVersions(stamp, running);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        return order switch
        {
            < 0 => $" (older than the running {running})",
            > 0 => $" (newer than the running {running})",

            // the same numbers, but not the same text: a development build's commit, or its mark, differs
            _ => $" (the same version numbers as the running {running}, but another build: the commit differs)"
        };
    }
}
