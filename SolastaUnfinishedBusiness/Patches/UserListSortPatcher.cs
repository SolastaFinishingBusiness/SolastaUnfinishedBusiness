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

    // The item a row of the editor or the picker is bound to, or null for a row that holds none. Eleven of the
    // editor's lists and the picker use UserContentLine or a class derived from it; the location list has a row
    // class of its own.
    private static UserContent ContentOf(Transform row)
    {
        return row.TryGetComponent<UserContentLine>(out var line) ? line.UserContent
            : row.TryGetComponent<UserLocationLine>(out var location) ? location.UserLocation
            : null;
    }

    // Is this row one the game has just bound to an item of the list? Only right after the game filled the table.
    // The editor's twelve Refresh methods and the picker's EnumerateLines bind row i to item i and switch it on for
    // every item, and switch off each further row that is on. A further row keeps the item it last held: the
    // game's UserContentLine.Unbind only releases the row's picture, it never clears UserContent (the location
    // row's Unbind does clear its item). So the item alone can't tell a bound row from a pooled one: a row left
    // over after a delete, or from a bigger campaign opened before, still has one, and turning it back on showed
    // that item twice or another campaign's item, whose double-click made the game read item -1 of the list.
    // Being switched on can tell them apart: a row the search had switched off and that is now past the end of
    // the list is left off by the game, and every row within the list's length has just been switched on.
    private static bool IsBoundRow(Transform row)
    {
        return row.gameObject.activeSelf && ContentOf(row) != null;
    }

    // is this the row the screen shows as selected?
    private static bool IsSelected(Transform row)
    {
        return row.TryGetComponent<UserContentLine>(out var line) ? line.Selected
            : row.TryGetComponent<UserLocationLine>(out var location) && location.Selected;
    }

    // a content row's two sort keys; the search looks in both
    private static string ContentName(Transform row)
    {
        return ContentOf(row)?.InternalName;
    }

    private static string ContentTitle(Transform row)
    {
        return ContentOf(row)?.DisplayTitle;
    }

    private static GameVariable VariableOf(Transform row)
    {
        return row.TryGetComponent<CampaignVariableLine>(out var line) ? line.GameVariable : null;
    }

    private static bool HasVariable(Transform row)
    {
        return VariableOf(row) != null;
    }

    // a variable row's two sort keys
    private static string VariableName(Transform row)
    {
        return VariableOf(row)?.Name;
    }

    private static string VariableType(Transform row)
    {
        return VariableOf(row)?.Type.ToString();
    }

    //PATCH: the view of the campaign editor's content lists. One patch for the twelve Refresh methods: each fills
    //its own table and works out its own pair of move arrows.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_RefreshLists
    {
        // per Refresh method: the list's table, its two move arrows, how to clear its selection, and the group
        // (the foldable section with the list's header) it sits in
        internal static readonly Dictionary<string, ContentList> Lists = new()
        {
            {
                "RefreshBiomes",
                new ContentList(x => x.userBiomesTable, x => x.moveBiomeUpButton, x => x.moveBiomeDownButton,
                    x => Clear(ref x.selectedBiome),
                    x => x.biomesGroupItem)
            },
            {
                "RefreshDialogs",
                new ContentList(x => x.userDialogsTable, x => x.moveDialogUpButton, x => x.moveDialogDownButton,
                    x => Clear(ref x.selectedDialog),
                    x => x.dialogsGroupItem)
            },
            {
                "RefreshEncounters",
                new ContentList(x => x.userEncountersTable, x => x.moveEncounterUpButton, x => x.moveEncounterDownButton,
                    x => Clear(ref x.selectedEncounter),
                    x => x.encountersGroupItem)
            },
            {
                "RefreshEncounterTables",
                new ContentList(x => x.userEncounterTablesTable, x => x.moveEncounterTableUpButton,
                    x => x.moveEncounterTableDownButton, x => Clear(ref x.selectedEncounterTable),
                    x => x.encounterTablesGroupItem)
            },
            {
                "RefreshItems",
                new ContentList(x => x.userItemsTable, x => x.moveItemUpButton, x => x.moveItemDownButton,
                    x => Clear(ref x.selectedItem),
                    x => x.itemsGroupItem)
            },
            {
                "RefreshLocations",
                new ContentList(x => x.userLocationsTable, x => x.moveLocationUpButton, x => x.moveLocationDownButton,
                    x => Clear(ref x.selectedLocation),
                    x => x.locationsGroupItem)
            },
            {
                "RefreshLootPacks",
                new ContentList(x => x.userLootPacksTable, x => x.moveLootPackUpButton, x => x.moveLootPackDownButton,
                    x => Clear(ref x.selectedLootPack),
                    x => x.lootPacksGroupItem)
            },
            {
                "RefreshMerchantInventories",
                new ContentList(x => x.userMerchantInventoriesTable, x => x.moveMerchantInventoryUpButton,
                    x => x.moveMerchantInventoryDownButton, x => Clear(ref x.selectedMerchantInventory),
                    x => x.merchantIventoriesGroupItem)
            },
            {
                "RefreshMonsters",
                new ContentList(x => x.userMonstersTable, x => x.moveMonsterUpButton, x => x.moveMonsterDownButton,
                    x => Clear(ref x.selectedMonster),
                    x => x.monstersGroupItem)
            },
            {
                "RefreshNpcs",
                new ContentList(x => x.userNpcsTable, x => x.moveNpcUpButton, x => x.moveNpcDownButton,
                    x => Clear(ref x.selectedNpc),
                    x => x.npcsGroupItem)
            },
            {
                "RefreshQuests",
                new ContentList(x => x.userQuestsTable, x => x.moveQuestUpButton, x => x.moveQuestDownButton,
                    x => Clear(ref x.selectedQuest),
                    x => x.questsGroupItem)
            },
            {
                "RefreshVariables",
                new ContentList(x => x.userVariablesTable, x => x.moveVariableUpButton, x => x.moveVariableDownButton,
                    x => Clear(ref x.selectedVariable),
                    x => x.variablesGroupItem)
            }
        };

        // sets a list's selected index to "none"; false when nothing was selected
        private static bool Clear(ref int selected)
        {
            if (selected < 0)
            {
                return false;
            }

            selected = -1;

            return true;
        }

        internal sealed class ContentList(
            Func<UserCampaignEditorScreen, Transform> table,
            Func<UserCampaignEditorScreen, Button> moveUp,
            Func<UserCampaignEditorScreen, Button> moveDown,
            Func<UserCampaignEditorScreen, bool> clearSelection,
            Func<UserCampaignEditorScreen, CampaignContentGroupItem> group)
        {
            internal readonly Func<UserCampaignEditorScreen, bool> ClearSelection = clearSelection;
            internal readonly Func<UserCampaignEditorScreen, CampaignContentGroupItem> Group = group;
            internal readonly Func<UserCampaignEditorScreen, Button> MoveDown = moveDown;
            internal readonly Func<UserCampaignEditorScreen, Button> MoveUp = moveUp;
            internal readonly Func<UserCampaignEditorScreen, Transform> Table = table;
        }

        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return Lists.Keys.Select(x => AccessTools.Method(typeof(UserCampaignEditorScreen), x));
        }

        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance, MethodBase __originalMethod)
        {
            if (!Lists.TryGetValue(__originalMethod.Name, out var list))
            {
                return;
            }

            DungeonMakerListTools.Bound(list.Table(__instance), IsBoundRow);
            ApplyTo(__instance, list);
        }

        // Applies the view to one list. The move arrows swap neighbours in the campaign's list, which are only
        // neighbours on screen in the plain view, so they are off in any other. The game works their state out
        // again on every refresh, which is how they come back.
        internal static void ApplyTo(UserCampaignEditorScreen screen, ContentList list)
        {
            var moveUp = list.MoveUp(screen);
            var moveDown = list.MoveDown(screen);

            // Only a list that is open keeps a selection (user direction, 2026-11-13). The editor's shortcut
            // icons work as tabs: one opens its list and folds the eleven others, and a list can also be folded
            // by its header. A list that is folded away loses its selection; in an open list the selection is
            // dropped when the search leaves the selected row out. Either way Edit and Delete never act on an
            // item that isn't shown. The refresh asked for works the list's buttons out again with nothing
            // selected; it can't come back here in a loop, as Clear then finds nothing selected.
            var hidSelected =
                DungeonMakerListTools.Apply(list.Table(screen), Editor, ContentName, ContentTitle, IsSelected);
            var group = list.Group(screen);

            if ((hidSelected || (group && group.folded)) && list.ClearSelection(screen))
            {
                screen.Dirty = true;
            }

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

    //PATCH: a list of the campaign editor was opened or folded away by its header: a folded list loses its
    //selection (see ApplyTo)
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_FoldChanged
    {
        [UsedImplicitly]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return new[]
            {
                "BiomeFoldChanged", "DialogFoldChanged", "EncounterFoldChanged", "EncounterTableFoldChanged",
                "ItemFoldChanged", "LocationFoldChanged", "LootPackFoldChanged", "MerchantInventoryFoldChanged",
                "MonsterFoldChanged", "NpcFoldChanged", "QuestFoldChanged", "VariableFoldChanged"
            }.Select(x => AccessTools.Method(typeof(UserCampaignEditorScreen), x));
        }

        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance)
        {
            ApplyToAll(__instance);
        }

        internal static void ApplyToAll(UserCampaignEditorScreen screen)
        {
            foreach (var list in UserCampaignEditorScreen_RefreshLists.Lists.Values)
            {
                UserCampaignEditorScreen_RefreshLists.ApplyTo(screen, list);
            }
        }
    }

    //PATCH: a shortcut icon of the campaign editor was clicked: it opens its list and folds the others, which is
    //how the user goes from one "tab" to another. The same as above, for the eleven lists it folded.
    [HarmonyPatch(typeof(UserCampaignEditorScreen), nameof(UserCampaignEditorScreen.OnCategoryHeaderFocused))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_OnCategoryHeaderFocused
    {
        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance)
        {
            UserCampaignEditorScreen_FoldChanged.ApplyToAll(__instance);
        }
    }

    //PATCH: the bar on the campaign editor, under the content lists (its buttons are copies of Import)
    [HarmonyPatch(typeof(UserCampaignEditorScreen), nameof(UserCampaignEditorScreen.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_OnBeginShow
    {
        // as for the picker: no list is filled with the last showing's search
        [UsedImplicitly]
        public static void Prefix()
        {
            DungeonMakerListTools.ClearSearch(Editor);
        }

        [UsedImplicitly]
        public static void Postfix(UserCampaignEditorScreen __instance)
        {
            DungeonMakerListTools.Ensure(__instance.transform, Editor, "RightPanel/UserContentScrollview",
                "RightPanel/ExportGroup/ImportContentsButton", "ModUi/&DungeonMakerSortTitle", () =>
                {
                    // the rows already hold their items: the twelve lists are only ordered and filtered again,
                    // without the game binding everything anew at every letter typed
                    UserCampaignEditorScreen_FoldChanged.ApplyToAll(__instance);

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
            DungeonMakerListTools.Bound(__instance.userVariablesTable, IsBoundRow);
            ApplyTo(__instance);
        }

        // The search left the picked variable out: drop the pick, or Validate would hand back a variable that
        // isn't shown. The refresh works Validate's state out again.
        internal static void ApplyTo(UserVariableSelectionModal picker)
        {
            if (!DungeonMakerListTools.Apply(
                    picker.userVariablesTable, Picker, ContentName, ContentTitle, IsSelected))
            {
                return;
            }

            picker.selectedVariable = -1;
            picker.Dirty = true;
        }
    }

    //PATCH: the bar on the variable picker, under its list (its buttons are copies of Cancel)
    [HarmonyPatch(typeof(UserVariableSelectionModal), nameof(UserVariableSelectionModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserVariableSelectionModal_OnBeginShow
    {
        // The game's OnBeginShow fills the list itself (EnumerateLines) before this postfix empties the search, so
        // the rows were filtered by the last showing's search behind an empty field until something was typed.
        // The search is emptied before the game fills the list.
        [UsedImplicitly]
        public static void Prefix()
        {
            DungeonMakerListTools.ClearSearch(Picker);
        }

        [UsedImplicitly]
        public static void Postfix(UserVariableSelectionModal __instance)
        {
            DungeonMakerListTools.Ensure(__instance.transform, Picker, "MainPanel/UserVariablesScrollview",
                "MainPanel/ButtonsBar/CancelButton", "ModUi/&DungeonMakerSortTitle",
                () => UserVariableSelectionModal_EnumerateLines.ApplyTo(__instance));
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

            DungeonMakerListTools.Bound(__instance.variableLinesTable, HasVariable);
            ApplyTo(__instance);
        }

        internal static void ApplyTo(CampaignVariablesModal panel)
        {
            var table = panel.variableLinesTable;

            DungeonMakerListTools.Apply(table, Cheats, VariableName, VariableType);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        }
    }

    //PATCH: the bar on the Cheats > Campaign Variables panel, under its list (its buttons are copies of Close)
    [HarmonyPatch(typeof(CampaignVariablesModal), nameof(CampaignVariablesModal.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CampaignVariablesModal_OnBeginShow
    {
        // as for the picker: no list is filled with the last showing's search
        [UsedImplicitly]
        public static void Prefix()
        {
            DungeonMakerListTools.ClearSearch(Cheats);
        }

        [UsedImplicitly]
        public static void Postfix(CampaignVariablesModal __instance)
        {
            DungeonMakerListTools.Ensure(__instance.transform, Cheats, "MainPanel/VariableLinesScrollview",
                "MainPanel/ButtonsBar/CloseButton", "ModUi/&DungeonMakerSortType",
                () => CampaignVariablesModal_EnumerateVariableLines.ApplyTo(__instance));
        }
    }
}
