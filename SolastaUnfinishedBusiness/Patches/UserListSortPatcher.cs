using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

// Alphabetizes every content list in the Dungeon Maker campaign editor, plus the variable picker popup and the
// Cheats -> Campaign Variables panel (Ctrl+Shift+V, see the last patch in this file).
//
// Simple "always sort" behaviour (no manual override): each list is sorted by InternalName, case-insensitive and
// stable, right before the game draws it. Both UIs bind line i to list[i] and keep the selection as an index,
// so we sort the BACKING list (UserCampaign.UserXxx, a List<T> of UserContent) and remap the selected index.
// Consequence: the editor's Up/Down arrows have no lasting effect, because the next redraw re-sorts.
//
// Each list has its own small patch class (same attribute form the working variables patch used) on purpose:
// Harmony in this game rejects [HarmonyPatch(typeof(X))] combined with TargetMethods().
//
// NOTE: Quests are included. GameCampaign.AutostartUserQuests starts auto-start quests in LIST ORDER, so sorting
// changes the order autostarted quests enter the journal. To leave quests alone, delete the "Quests" class below.
internal static class UserListSorter
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    // sorts the backing list in place; returns the (possibly remapped) selected index
    internal static int Sort<T>(string name, UserCampaign campaign, List<T> list, int selectedIndex)
        where T : UserContent
    {
        if (list == null || list.Count < 2)
        {
            return selectedIndex;
        }

        var sorted = list.OrderBy(x => x.InternalName ?? string.Empty, Comparer).ToList();

        if (sorted.SequenceEqual(list))
        {
            return selectedIndex;
        }

        var selected = selectedIndex >= 0 && selectedIndex < list.Count ? list[selectedIndex] : null;

        list.Clear();
        list.AddRange(sorted);

        Main.Info($"USERSORT {name}: sorted {sorted.Count} items in '{campaign?.InternalName}'.");

        return selected == null ? selectedIndex : list.IndexOf(selected);
    }
}

// Editor screen lists: each patch sorts its list right before the game (re)builds the lines.
public static class UserListSortPatches
{
    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshBiomes")]
    public static class Biomes
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedBiome)
        {
            ___selectedBiome = UserListSorter.Sort("Biomes", __instance.UserCampaign, __instance.UserCampaign?.UserBiomes, ___selectedBiome);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshDialogs")]
    public static class Dialogs
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedDialog)
        {
            ___selectedDialog = UserListSorter.Sort("Dialogs", __instance.UserCampaign, __instance.UserCampaign?.UserDialogs, ___selectedDialog);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshEncounters")]
    public static class Encounters
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedEncounter)
        {
            ___selectedEncounter = UserListSorter.Sort("Encounters", __instance.UserCampaign, __instance.UserCampaign?.UserEncounters, ___selectedEncounter);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshEncounterTables")]
    public static class EncounterTables
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedEncounterTable)
        {
            ___selectedEncounterTable = UserListSorter.Sort("EncounterTables", __instance.UserCampaign, __instance.UserCampaign?.UserEncounterTables, ___selectedEncounterTable);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshItems")]
    public static class Items
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedItem)
        {
            ___selectedItem = UserListSorter.Sort("Items", __instance.UserCampaign, __instance.UserCampaign?.UserItems, ___selectedItem);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshLocations")]
    public static class Locations
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedLocation)
        {
            ___selectedLocation = UserListSorter.Sort("Locations", __instance.UserCampaign, __instance.UserCampaign?.UserLocations, ___selectedLocation);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshLootPacks")]
    public static class LootPacks
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedLootPack)
        {
            ___selectedLootPack = UserListSorter.Sort("LootPacks", __instance.UserCampaign, __instance.UserCampaign?.UserLootPacks, ___selectedLootPack);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshMerchantInventories")]
    public static class MerchantInventories
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedMerchantInventory)
        {
            ___selectedMerchantInventory = UserListSorter.Sort("MerchantInventories", __instance.UserCampaign, __instance.UserCampaign?.UserMerchantInventories, ___selectedMerchantInventory);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshMonsters")]
    public static class Monsters
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedMonster)
        {
            ___selectedMonster = UserListSorter.Sort("Monsters", __instance.UserCampaign, __instance.UserCampaign?.UserMonsters, ___selectedMonster);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshNpcs")]
    public static class Npcs
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedNpc)
        {
            ___selectedNpc = UserListSorter.Sort("Npcs", __instance.UserCampaign, __instance.UserCampaign?.UserNpcs, ___selectedNpc);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshQuests")]
    public static class Quests
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedQuest)
        {
            ___selectedQuest = UserListSorter.Sort("Quests", __instance.UserCampaign, __instance.UserCampaign?.UserQuests, ___selectedQuest);
        }
    }

    [UsedImplicitly]
    [HarmonyPatch(typeof(UserCampaignEditorScreen), "RefreshVariables")]
    public static class Variables
    {
        [UsedImplicitly]
        public static void Prefix(UserCampaignEditorScreen __instance, ref int ___selectedVariable)
        {
            ___selectedVariable = UserListSorter.Sort("Variables", __instance.UserCampaign, __instance.UserCampaign?.UserVariables, ___selectedVariable);
        }
    }
}

// Variable picker popup: same sort (it shares the same backing list as the editor).
[UsedImplicitly]
[HarmonyPatch(typeof(UserVariableSelectionModal), "EnumerateLines")]
public static class UserListSortVariablePickerPatcher
{
    [UsedImplicitly]
    public static void Prefix(UserCampaign ___userCampaign, ref int ___selectedVariable)
    {
        ___selectedVariable = UserListSorter.Sort("Variables (picker)", ___userCampaign, ___userCampaign?.UserVariables,
            ___selectedVariable);
    }
}

// Cheats -> Campaign Variables panel (Ctrl+Shift+V).
//
// That panel is not one of the editor lists above. It shows IGameVariableService.ActiveVariables, and
// GameVariableManager.get_ActiveVariables simply returns `new List<GameVariable>(activeVariables.Values)`: the order of a
// dictionary. Nothing sorts it, so the order is just the order the variables were registered. It starts out alphabetical
// (the campaign registers its variables in the order of the sorted editor list) but drifts as variables are registered
// later, removed or re-registered (for example location-scoped ones when you enter or leave a location), because a
// dictionary reuses freed slots. That is why the panel is sometimes alphabetical and sometimes not.
//
// The getter builds a brand-new list on every call, so sorting the returned list changes only what the caller sees:
// the game's own dictionary is untouched. Variable names are dictionary keys, so they are unique and the order is
// fully determined. Other readers of ActiveVariables (the debug overlay, and the code that registers variable-changed
// listeners) only iterate it, so for them the order is irrelevant.
[UsedImplicitly]
[HarmonyPatch(typeof(GameVariableManager), nameof(GameVariableManager.ActiveVariables), MethodType.Getter)]
public static class UserListSortActiveVariablesPatcher
{
    [UsedImplicitly]
    public static void Postfix(List<GameVariable> __result)
    {
        if (__result == null || __result.Count < 2)
        {
            return;
        }

        __result.Sort((x, y) => string.Compare(x?.Name, y?.Name, StringComparison.OrdinalIgnoreCase));
    }
}
