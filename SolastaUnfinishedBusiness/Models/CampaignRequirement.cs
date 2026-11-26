using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

internal static class CampaignRequirement
{
    private const string Field = "requiresFinishingBusiness";
    private const string Preference = "FB.DungeonMaker.SuppressRequirementPrompt";
    private sealed class Choice
    {
        internal bool? Editor;
        internal bool? Saved;
    }

    // Two campaigns with identical titles can be open in separate contexts. A loaded session also
    // owns a distinct instance reconstructed from the host's or save file's compressed campaign JSON.
    private static readonly ConditionalWeakTable<UserCampaign, Choice> Choices = new();
    internal static bool? Get(UserCampaign campaign) =>
        campaign != null && Choices.TryGetValue(campaign, out var choice) ? choice.Editor : null;
    internal static void Set(UserCampaign campaign, bool required) =>
        Choices.GetOrCreateValue(campaign).Editor = required;

    internal static bool SuppressPrompt
    {
        get => PlayerPrefs.GetInt(Preference, 0) != 0;
        set { PlayerPrefs.SetInt(Preference, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    internal static bool ShouldPromptOnAuthorSave(UserCampaign campaign) =>
        campaign != null && Get(campaign) == null && !SuppressPrompt && campaign.UserVariables.Any(v =>
            v != null && DungeonMakerNamesContext.RoleOf(v.InternalName, out _) is
                DungeonMakerNamesContext.Role.Command or DungeonMakerNamesContext.Role.Request or
                DungeonMakerNamesContext.Role.Switch or DungeonMakerNamesContext.Role.Counter or DungeonMakerNamesContext.Role.Readout &&
            !DungeonMakerNamesContext.WrongType(v.InternalName, v.VariableType));

    internal static bool CommandsEnabled
    {
        get
        {
            var campaign = ServiceRepository.GetService<ISessionService>()?.Session?.UserCampaign ??
                           ServiceRepository.GetService<IGameLocationService>()?.GameLocation?.UserCampaign;
            return campaign != null && Choices.TryGetValue(campaign, out var choice) && choice.Saved == true;
        }
    }

    // Called once the file is confirmed written with the running version. When the title was already in the pool,
    // SaveUserCampaign stored a clone of the campaign there before this runs, so that clone carries the choice from
    // before the save; starting the campaign later in this session plays that clone. It is brought up to date too.
    internal static void Saved(UserCampaign campaign)
    {
        var choice = Choices.GetOrCreateValue(campaign);

        choice.Saved = choice.Editor;

        if (campaign.Title == null ||
            ServiceRepository.GetService<IUserCampaignPoolService>() is not UserCampaignPoolManager manager ||
            manager.pool == null ||
            !manager.pool.TryGetValue(campaign.Title, out var cached) ||
            ReferenceEquals(cached, campaign) || cached == null)
        {
            return;
        }

        var copy = Choices.GetOrCreateValue(cached);

        copy.Editor = choice.Editor;
        copy.Saved = choice.Saved;
    }

    internal static string FieldJson(UserCampaign campaign, bool session)
    {
        var choice = Choices.GetOrCreateValue(campaign);
        var value = session ? choice.Saved : choice.Editor;
        return "\"" + Field + "\":" + (value.HasValue ? value.Value ? "true" : "false" : "null") + ",";
    }

    // Bind the parsed object before PostLoadJson: the pool can automatically rewrite it during
    // loading. Session deserialization takes this same path before its cached campaign starts play.
    private static UserCampaign Bind(string json, UserCampaign campaign)
    {
        if (campaign == null) { return null; }
        string version = null;
        bool? flag = null;
        try
        {
            using var reader = new JsonTextReader(new StringReader(json));
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1) { continue; }
                var key = (string)reader.Value;
                if (!reader.Read()) { break; }
                if (key == Field && reader.TokenType == JsonToken.Boolean) { flag = (bool)reader.Value; }
                else if (key == "finishingBusinessVersion" && reader.TokenType == JsonToken.String) { version = (string)reader.Value; }
                else { reader.Skip(); }
                if (flag.HasValue && version != null) { break; }
            }
            if (string.IsNullOrEmpty(version) || UpdateContext.CompareVersions(version, "1.19.0-dev.0") < 0) { flag = null; }
        }
        catch (Exception) { flag = null; }
        var choice = Choices.GetOrCreateValue(campaign);
        choice.Editor = choice.Saved = flag;
        return campaign;
    }

    private static string SessionJson(object content) => DungeonMakerStampContext.StampedJson(content, true);

    // The game rarely keeps the campaign object it read: the campaign editor edits a clone (Show), keeps clones for
    // undo (NotifyBeforeModification), and the pool stores a clone of every campaign it saves (SaveUserCampaign). The
    // choice is keyed by object, so each clone takes over its source's choice; otherwise an editor save would write
    // null and drop the author's choice.
    [HarmonyPatch(typeof(UserCampaign), nameof(UserCampaign.Clone))]
    private static class ClonePatch
    {
        private static void Postfix(UserCampaign __instance, UserCampaign __result)
        {
            if (__result == null || !Choices.TryGetValue(__instance, out var source))
            {
                return;
            }

            var copy = Choices.GetOrCreateValue(__result);

            copy.Editor = source.Editor;
            copy.Saved = source.Saved;
        }
    }

    [HarmonyPatch]
    private static class CampaignJsonPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(UserCampaignPoolManager), nameof(UserCampaignPoolManager.ReadCampaignFromDisk));
            yield return AccessTools.Method(typeof(Session), nameof(Session.SerializeElements));
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var op in instructions)
            {
                if (op.operand is MethodInfo { Name: "FromJson", ReturnType: var type } && type == typeof(UserCampaign))
                {
                    var dup = new CodeInstruction(OpCodes.Dup);
                    dup.labels.AddRange(op.labels);
                    op.labels.Clear();
                    yield return dup;
                    yield return op;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CampaignRequirement), nameof(Bind)));
                }
                else
                {
                    if (op.Calls(DungeonMakerStampContext.ToJsonMethod))
                    {
                        op.operand = AccessTools.Method(typeof(CampaignRequirement), nameof(SessionJson));
                    }
                    yield return op;
                }
            }
        }
    }
}
