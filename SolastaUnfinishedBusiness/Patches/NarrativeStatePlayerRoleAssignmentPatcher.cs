using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api.Helpers;
using SolastaUnfinishedBusiness.Models;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NarrativeStatePlayerRoleAssignmentPatcher
{
    [HarmonyPatch(typeof(NarrativeStatePlayerRoleAssignment), nameof(NarrativeStatePlayerRoleAssignment.BuildHook))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BuildHook_Patch
    {
        //PATCH: ensures dialogs won't break on official campaigns with parties less than 4 (PARTYSIZE)
        private static void TryAdd(
            Dictionary<string, WorldLocationCharacter> playerRolesMap,
            string role,
            WorldLocationCharacter actor)
        {
            playerRolesMap.TryAdd(role, actor);
        }

        // Only the game's unrestricted assignment uses this path. Answers and subsequent speech
        // consume this same role map. Keep campaign roles and personality-tested assignments native.
        private static void AddInterchangeableRole(
            Dictionary<string, WorldLocationCharacter> roles, string role, WorldLocationCharacter actor,
            List<WorldLocationCharacter> candidates, NarrativeStatePlayerRoleAssignment state)
        {
            var sequence = state.NarrativeSequence;
            var party = sequence.PlayerActors;
            var tree = sequence.NarrativeTreeDefinition;

            if (party.Count > ToolsContext.GamePartySize && !tree.IsUserContent)
            {
                // Count authored slots so peers and rebuilt branches choose the same hero without
                // a session-local counter. Start with the first hero past the four authored seats.
                var slot = ToolsContext.GamePartySize;
                foreach (var entry in tree.AllNarrativeStateDescriptions)
                {
                    if (entry?.Type != state.NarrativeStateDescription.Type || entry.CampaignDefinition != null ||
                        entry.DialogLines == null)
                    {
                        continue;
                    }

                    foreach (var line in entry.DialogLines)
                    {
                        if (entry == state.NarrativeStateDescription && line.TextLine == role)
                        {
                            break;
                        }

                        if (line.FlavorFlags is not { Count: > 0 })
                        {
                            slot++;
                        }
                    }

                    if (entry == state.NarrativeStateDescription)
                    {
                        break;
                    }
                }

                for (var i = 0; i < party.Count; i++)
                {
                    var next = party[(slot + i) % party.Count];
                    var index = candidates.IndexOf(next);
                    if (next == null || index < 0 || roles.ContainsValue(next))
                    {
                        continue;
                    }

                    actor = next;
                    // The original method consumes candidate zero after recording the role.
                    candidates.RemoveAt(index);
                    candidates.Insert(0, next);
                    break;
                }
            }

            roles.TryAdd(role, actor);
        }

        //PATCH: supports heroes with best proficiency to roll dialogue choices instead of vanilla behavior
        private static void RemoveAt(List<WorldLocationCharacter> actors, int position)
        {
            if (!Main.Settings.EnableHeroWithBestProficiencyToRollChoice)
            {
                actors.RemoveAt(position);
            }
        }

        [UsedImplicitly]
        public static void Prefix(NarrativeStatePlayerRoleAssignment __instance, out HashSet<string> __state)
        {
            __state = __instance.PlayerRolesMap == null ? [] : [.. __instance.PlayerRolesMap.Keys];
        }

        [UsedImplicitly]
        public static void Postfix(NarrativeStatePlayerRoleAssignment __instance, HashSet<string> __state)
        {
            //PATCH: one hero per role so heroes don't talk to themselves (MULTIPLAYER)
            if (Main.Settings.EnableHeroWithBestProficiencyToRollChoice)
            {
                MultiplayerContext.EnsureUniqueHeroPerRole(__instance, __state);
            }
        }

        [UsedImplicitly]
        public static IEnumerable<CodeInstruction> Transpiler([NotNull] IEnumerable<CodeInstruction> instructions)
        {
            var tryAddMethod = new Action<
                Dictionary<string, WorldLocationCharacter>,
                string,
                WorldLocationCharacter>(TryAdd).Method;
            var removeAtMethod = new Action<
                List<WorldLocationCharacter>,
                int>(RemoveAt).Method;

            return instructions
                // The second dictionary Add binds the unflagged dialog line. Local 0 is the
                // already-filtered candidate list used by the original RemoveAt immediately after it.
                .ReplaceAdd("System.String, WorldLocationCharacter", 2,
                    "NarrativeStatePlayerRoleAssignment.BuildHook.Interchangeable",
                    new CodeInstruction(OpCodes.Ldloc_0),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(BuildHook_Patch),
                        nameof(AddInterchangeableRole))))
                .ReplaceAdd("System.String, WorldLocationCharacter", -1,
                    "NarrativeStatePlayerRoleAssignment.BuildHook.Add",
                    new CodeInstruction(OpCodes.Call, tryAddMethod))
                .ReplaceRemoveAt(-1,
                    "NarrativeStatePlayerRoleAssignment.BuildHook.RemoveAt",
                    new CodeInstruction(OpCodes.Call, removeAtMethod));
        }
    }
}
