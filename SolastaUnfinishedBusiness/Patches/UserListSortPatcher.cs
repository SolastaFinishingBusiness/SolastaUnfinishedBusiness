using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

// The hooks of the Dungeon Maker's list tools (issue #42): sorting and searching the campaign editor's twelve
// content lists, the variable picker and the Cheats > Campaign Variables panel. How the view works, and why it
// never touches the campaign's lists, is explained in CustomUI/DungeonMakerListTools.cs. Each screen has
//   - a patch after the game fills a table: remember the rows in list order, then apply the view;
//   - a patch when the screen is shown: add the bar (sort, direction, search) and say how to apply a change.
[UsedImplicitly]
public static class UserListSortPatcher
{
    private const string Editor = "editor";
    private const string Picker = "picker";
    private const string Cheats = "cheats";

    // a content row's two sort keys; the search looks in both
    private static string ContentName(UserContentLine line)
    {
        return line.UserContent?.InternalName;
    }

    private static string ContentTitle(UserContentLine line)
    {
        return line.UserContent?.DisplayTitle;
    }

    private static string VariableName(CampaignVariableLine line)
    {
        return line.GameVariable?.Name;
    }

    private static string VariableType(CampaignVariableLine line)
    {
        return line.GameVariable?.Type.ToString();
    }

    //PATCH: the view of the campaign editor's content lists. One patch for the twelve Refresh methods: each fills
    //its own table and works out its own pair of move arrows.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_RefreshLists
    {
        internal static readonly Dictionary<string, Func<UserCampaignEditorScreen, (Transform, Button, Button)>>
            Lists = new()
            {
                { "RefreshBiomes", x => (x.userBiomesTable, x.moveBiomeUpButton, x.moveBiomeDownButton) },
                { "RefreshDialogs", x => (x.userDialogsTable, x.moveDialogUpButton, x.moveDialogDownButton) },
                {
                    "RefreshEncounters",
                    x => (x.userEncountersTable, x.moveEncounterUpButton, x.moveEncounterDownButton)
                },
                {
                    "RefreshEncounterTables",
                    x => (x.userEncounterTablesTable, x.moveEncounterTableUpButton, x.moveEncounterTableDownButton)
                },
                { "RefreshItems", x => (x.userItemsTable, x.moveItemUpButton, x.moveItemDownButton) },
                { "RefreshLocations", x => (x.userLocationsTable, x.moveLocationUpButton, x.moveLocationDownButton) },
                { "RefreshLootPacks", x => (x.userLootPacksTable, x.moveLootPackUpButton, x.moveLootPackDownButton) },
                {
                    "RefreshMerchantInventories",
                    x => (x.userMerchantInventoriesTable, x.moveMerchantInventoryUpButton,
                        x.moveMerchantInventoryDownButton)
                },
                { "RefreshMonsters", x => (x.userMonstersTable, x.moveMonsterUpButton, x.moveMonsterDownButton) },
                { "RefreshNpcs", x => (x.userNpcsTable, x.moveNpcUpButton, x.moveNpcDownButton) },
                { "RefreshQuests", x => (x.userQuestsTable, x.moveQuestUpButton, x.moveQuestDownButton) },
                { "RefreshVariables", x => (x.userVariablesTable, x.moveVariableUpButton, x.moveVariableDownButton) }
            };

        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return Lists.Keys.Select(x => AccessTools.Method(typeof(UserCampaignEditorScreen), x));
        }

        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance, MethodBase __originalMethod)
        {
            if (!Lists.TryGetValue(__originalMethod.Name, out var parts))
            {
                return;
            }

            var (table, _, _) = parts(__instance);

            DungeonMakerListTools.Bound<UserContentLine>(table, x => x.UserContent != null);
            ApplyTo(__instance, parts);
        }

        // Applies the view to one list. The move arrows swap neighbours in the campaign's list, which are only
        // neighbours on screen in the plain view, so they are off in any other. The game works their state out
        // again on every refresh, which is how they come back.
        internal static void ApplyTo(
            UserCampaignEditorScreen screen, Func<UserCampaignEditorScreen, (Transform, Button, Button)> parts)
        {
            var (table, moveUp, moveDown) = parts(screen);

            DungeonMakerListTools.Apply<UserContentLine>(table, Editor, ContentName, ContentTitle);

            if (DungeonMakerListTools.IsPlain(Editor))
            {
                return;
            }

            if (moveUp)
            {
                moveUp.interactable = false;
            }

            if (moveDown)
            {
                moveDown.interactable = false;
            }
        }
    }

    //PATCH: the bar on the campaign editor, beside Import and Export above the content lists
    [HarmonyPatch(typeof(UserCampaignEditorScreen), nameof(UserCampaignEditorScreen.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_OnBeginShow
    {
        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance)
        {
            DungeonMakerListTools.Ensure(__instance.transform, Editor, "RightPanel/ExportGroup/ImportContentsButton",
                "ModUi/&DungeonMakerSortTitle", () =>
                {
                    // the rows already hold their items: the twelve lists are only ordered and filtered again,
                    // without the game binding everything anew at every letter typed
                    foreach (var parts in UserCampaignEditorScreen_RefreshLists.Lists.Values)
                    {
                        UserCampaignEditorScreen_RefreshLists.ApplyTo(__instance, parts);
                    }

                    // back to the plain view: a refresh has the game work out the move arrows again
                    if (DungeonMakerListTools.IsPlain(Editor))
                    {
                        __instance.Dirty = true;
                    }
                });
        }
    }

    //PATCH: the view of the variable picker (the popup that asks which campaign variable to use). Its Validate
    //button reads the picked variable from the campaign's list by the index the row's item has there.
    [HarmonyPatch(typeof(UserVariableSelectionModal), nameof(UserVariableSelectionModal.EnumerateLines))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserVariableSelectionModal_EnumerateLines
    {
        [UsedImplicitly]
        public static void Postfix(UserVariableSelectionModal __instance)
        {
            var table = __instance.userVariablesTable;

            DungeonMakerListTools.Bound<UserContentLine>(table, x => x.UserContent != null);
            DungeonMakerListTools.Apply<UserContentLine>(table, Picker, ContentName, ContentTitle);
        }
    }

    //PATCH: the bar on the variable picker, beside Cancel
    [HarmonyPatch(typeof(UserVariableSelectionModal), nameof(UserVariableSelectionModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserVariableSelectionModal_OnBeginShow
    {
        [UsedImplicitly]
        public static void Postfix(UserVariableSelectionModal __instance)
        {
            DungeonMakerListTools.Ensure(__instance.transform, Picker, "MainPanel/ButtonsBar/CancelButton",
                "ModUi/&DungeonMakerSortTitle",
                () => DungeonMakerListTools.Apply<UserContentLine>(
                    __instance.userVariablesTable, Picker, ContentName, ContentTitle));
        }
    }

    //PATCH: the view of the Cheats > Campaign Variables panel. The game lists the variables in the order of its
    //variable table, which starts out in campaign order and drifts as variables are registered and removed. The
    //panel builds its rows over a few frames while it is still invisible, so the view is applied when it is done.
    [HarmonyPatch(typeof(CampaignVariablesModal), nameof(CampaignVariablesModal.EnumerateVariableLines))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CampaignVariablesModal_EnumerateVariableLines
    {
        [UsedImplicitly]
        public static IEnumerator Postfix(IEnumerator values, CampaignVariablesModal __instance)
        {
            try
            {
                while (values.MoveNext())
                {
                    yield return values.Current;
                }
            }
            finally
            {
                (values as IDisposable)?.Dispose();
            }

            if (!__instance || !__instance.variableLinesTable)
            {
                yield break;
            }

            DungeonMakerListTools.Bound<CampaignVariableLine>(
                __instance.variableLinesTable, x => x.GameVariable != null);
            ApplyTo(__instance);
        }

        internal static void ApplyTo(CampaignVariablesModal panel)
        {
            var table = panel.variableLinesTable;

            DungeonMakerListTools.Apply<CampaignVariableLine>(table, Cheats, VariableName, VariableType);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        }
    }

    //PATCH: the bar on the Cheats > Campaign Variables panel, beside Close
    [HarmonyPatch(typeof(CampaignVariablesModal), nameof(CampaignVariablesModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CampaignVariablesModal_OnBeginShow
    {
        [UsedImplicitly]
        public static void Postfix(CampaignVariablesModal __instance)
        {
            DungeonMakerListTools.Ensure(__instance.transform, Cheats, "MainPanel/ButtonsBar/CloseButton",
                "ModUi/&DungeonMakerSortType",
                () => CampaignVariablesModal_EnumerateVariableLines.ApplyTo(__instance));
        }
    }
}
