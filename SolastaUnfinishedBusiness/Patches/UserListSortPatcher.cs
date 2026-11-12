using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

// Alphabetical view of the Dungeon Maker's lists (issue #42, adapted from BlackguardRogue's Dungeon Maker tools):
// the twelve content lists of the campaign editor, the variable picker, and the Cheats > Campaign Variables panel.
// One setting for all three, off by default: Mod UI > Dungeon Maker > "Show the Dungeon Maker's lists in
// alphabetical order" (Settings.SortDungeonMakerListsAlphabetically).
//
// It only changes what is SHOWN. The first version sorted the campaign's own lists (so the order was saved with the
// campaign, and auto-start quests, which the game starts in list order, entered the journal in another order) and
// sorted every read of the game's variable list. Here nothing but the order of the row objects on screen changes:
//
//   - All three screens fill their table the same way: row object i is bound to item i of the list, in list order.
//   - Afterwards the row objects are put in alphabetical order among themselves (Transform.SetSiblingIndex), which is
//     the order the table's layout draws them in.
//   - Every row keeps the item it was bound to, and the game finds out what a click means from the row's item
//     (list.IndexOf(line.UserContent)), not from the row's position. So select, edit, duplicate, delete and "pick"
//     act on the item that was clicked, and the selection survives switching the setting.
//   - On the next refresh the game binds row object i to item i again, whichever object that now is, and the rows
//     are ordered again. With the setting off, that alone puts everything back in list order.
//
// The order is by internal name, ignoring case; items with the same name keep their list order (a stable sort).
// The editor's move up / move down arrows change the list order, which can't be seen in this view, so they are
// switched off while it is on.
internal static class UserListSorter
{
    internal static bool Alphabetical => Main.Settings.SortDungeonMakerListsAlphabetically;

    // Orders the active row objects of a table by name. They are the table's first children (the screens keep
    // unused pooled rows, switched off, after them), so the sorted rows take sibling positions 0, 1, 2...
    internal static void SortRows<TLine>(Transform table, Func<TLine, string> name) where TLine : Component
    {
        if (!Alphabetical || !table)
        {
            return;
        }

        var rows = new List<KeyValuePair<Transform, string>>();

        for (var i = 0; i < table.childCount; i++)
        {
            var child = table.GetChild(i);

            if (child.gameObject.activeSelf && child.TryGetComponent<TLine>(out var line))
            {
                rows.Add(new KeyValuePair<Transform, string>(child, name(line) ?? string.Empty));
            }
        }

        if (rows.Count < 2)
        {
            return;
        }

        // OrderBy is stable: rows with equal names stay in list order
        var sorted = rows.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase).ToList();

        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].Key.GetSiblingIndex() != i)
            {
                sorted[i].Key.SetSiblingIndex(i);
            }
        }
    }

    // the setting was switched in the mod's menu: have the editor draw its lists again if it is open
    internal static void SettingChanged()
    {
        var editor = Gui.GuiService?.GetScreen<UserCampaignEditorScreen>();

        if (editor && editor.gameObject.activeInHierarchy)
        {
            editor.Dirty = true;
        }
    }
}

[UsedImplicitly]
public static class UserListSortPatcher
{
    //PATCH: alphabetical view of the campaign editor's content lists. One patch for the twelve Refresh methods:
    //each fills its own table and switches its own pair of move arrows.
    [HarmonyPatch]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserCampaignEditorScreen_RefreshLists
    {
        private static readonly Dictionary<string, Func<UserCampaignEditorScreen, (Transform, Button, Button)>> Lists =
            new()
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
            if (!UserListSorter.Alphabetical || !Lists.TryGetValue(__originalMethod.Name, out var parts))
            {
                return;
            }

            var (table, moveUp, moveDown) = parts(__instance);

            UserListSorter.SortRows<UserContentLine>(table, x => x.UserContent?.InternalName);

            // The arrows swap neighbours in the list, which are not neighbours in this view. The game works out
            // their state again on every refresh, so they come back by themselves when the setting is off.
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

    //PATCH: alphabetical view of the variable picker (the popup that asks which campaign variable to use). Its
    //Validate button reads the picked variable from the campaign's list by the index the row's item has there.
    [HarmonyPatch(typeof(UserVariableSelectionModal), nameof(UserVariableSelectionModal.EnumerateLines))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UserVariableSelectionModal_EnumerateLines
    {
        [UsedImplicitly]
        public static void Postfix(UserVariableSelectionModal __instance)
        {
            UserListSorter.SortRows<UserContentLine>(__instance.userVariablesTable, x => x.UserContent?.InternalName);
        }
    }

    //PATCH: alphabetical view of the Cheats > Campaign Variables panel. The game lists the variables in the order
    //of its variable table, which starts out in campaign order and drifts as variables are registered and removed.
    //The panel builds its rows over a few frames while it is still invisible, so they are ordered when it is done.
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

            if (!UserListSorter.Alphabetical || !__instance || !__instance.variableLinesTable)
            {
                yield break;
            }

            UserListSorter.SortRows<CampaignVariableLine>(__instance.variableLinesTable, x => x.GameVariable?.Name);
            LayoutRebuilder.ForceRebuildLayoutImmediate(__instance.variableLinesTable);
        }
    }
}
