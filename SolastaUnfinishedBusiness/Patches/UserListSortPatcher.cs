using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    #region Category sort options

    // The keys each list can be sorted by in the category row (Codex's list of what the game's data holds,
    // 2026-11-13, each read here from the game's own classes). Ids are saved in the settings: don't rename them.
    // Counts are of entries (rows), not of the quantities in them.

    // the picker lists the campaign's variables like the editor's Variables list, and shares its category sort
    private const string PickerList = "Variables";

    // the cheat panel's variables are the running game's, with a scope and a current value
    private const string CheatsList = "CampaignVariables";

    // a key whose value the rows don't show: while it sorts the list, each row's overlay shows it (by default as
    // the key's own value: a number, a text, Yes or No)
    private static CategoryOption Option(
        string id, Func<Transform, SortKey> key, Func<Transform, string> display = null)
    {
        return new CategoryOption(id, "ModUi/&DungeonMakerCategory" + id, key, display ?? (r => key(r).Display()));
    }

    // a key the rows show already (a location's author, start levels, environment, campaign start): no overlay
    private static CategoryOption Shown(string id, Func<Transform, SortKey> key)
    {
        return new CategoryOption(id, "ModUi/&DungeonMakerCategory" + id, key, null);
    }

    private static SortKey Number(double? value)
    {
        return value.HasValue ? SortKey.Of(value.Value) : SortKey.Missing;
    }

    private static T As<T>(Transform row) where T : class
    {
        return ContentOf(row) as T;
    }

    // a game definition as players see it: its title, or its internal name when it has none
    private static string TitleOf(BaseDefinition definition)
    {
        if (!definition)
        {
            return null;
        }

        var title = definition.GuiPresentation?.Title;

        return string.IsNullOrEmpty(title) ? definition.name : Gui.Localize(title);
    }

    // a value of a variable, kept with values of its own type: numbers and flags as numbers, text as text
    private static SortKey ValueKey(GameVariableDefinitions.Type type, int number, bool flag, string text)
    {
        return type switch
        {
            GameVariableDefinitions.Type.Int => SortKey.Grouped((int)type, number, null),
            GameVariableDefinitions.Type.Bool => SortKey.Flag((int)type, flag),
            _ => SortKey.Grouped((int)type, null, text ?? string.Empty)
        };
    }

    private static readonly CategoryOption AuthorOption = Option("Author", r => SortKey.Of(ContentOf(r)?.Author));

    // the location rows show their author (UserLocationLine.authorLabel)
    private static readonly CategoryOption ShownAuthorOption =
        Shown("Author", r => SortKey.Of(ContentOf(r)?.Author));

    // the cost in the item's own coins, e.g. "1 pp 5 gp"
    private static readonly string[] CoinNames = ["pp", "gp", "ep", "sp", "cp"];

    private static string CostText(Transform row)
    {
        var costs = As<UserItem>(row)?.Costs;

        if (costs == null)
        {
            return "-";
        }

        var parts = costs
            .Take(CoinNames.Length)
            .Select((amount, coin) => amount != 0 ? $"{amount} {CoinNames[coin]}" : null)
            .Where(x => x != null)
            .ToList();

        return parts.Count == 0 ? "0" : string.Join(" ", parts);
    }

    // an ability score of a monster or NPC; the game stores the six in this order (AttributeDefinitions)
    private static CategoryOption Ability(string id, int index)
    {
        return Option(id, r => As<UserCharacter>(r)?.AbilityScores is { } scores && scores.Length > index
            ? SortKey.Of(scores[index])
            : SortKey.Missing);
    }

    // an item's cost in copper: Costs holds one amount per coin, in the order of EquipmentDefinitions.Currency
    // (platinum, gold, electrum, silver, copper)
    private static readonly int[] CopperPerCoin = [1000, 100, 50, 10, 1];

    private static SortKey CostKey(Transform row)
    {
        var costs = As<UserItem>(row)?.Costs;

        if (costs == null)
        {
            return SortKey.Missing;
        }

        double copper = 0;

        for (var i = 0; i < costs.Length && i < CopperPerCoin.Length; i++)
        {
            copper += (double)costs[i] * CopperPerCoin[i];
        }

        return SortKey.Of(copper);
    }

    private static readonly CategoryOption[] CharacterOptions =
    [
        Option("ChallengeRating", r => Number(As<UserCharacter>(r)?.ChallengeRating)),
        Option("HitPoints", r => Number(As<UserCharacter>(r)?.HitPoints)),
        Option("ArmorClass", r => Number(As<UserCharacter>(r)?.ArmorClass)),
        Ability("Strength", 0),
        Ability("Dexterity", 1),
        Ability("Constitution", 2),
        Ability("Intelligence", 3),
        Ability("Wisdom", 4),
        Ability("Charisma", 5),
        Option("BaseCreature", r => As<UserCharacter>(r) is { } character
            ? SortKey.Of(TitleOf(character.ReferenceMonsterDefinition) ?? character.ReferenceDefinition)
            : SortKey.Missing),
        AuthorOption
    ];

    private static readonly CategoryOption[] EncounterOptions =
    [
        Option("ChallengeRating", r => Number(As<UserEncounter>(r)?.ChallengeRating)),
        Option("MonsterCount", r => Number(As<UserEncounter>(r)?.MonsterOccurences?.Count)),
        AuthorOption
    ];

    private static readonly CategoryOption[] LocationOptions =
    [
        Shown("MinStartLevel", r => Number(As<UserLocation>(r)?.StartLevelMin)),
        Shown("MaxStartLevel", r => Number(As<UserLocation>(r)?.StartLevelMax)),
        Option("MapSize", r => As<UserLocation>(r) is { } location
                ? SortKey.Of((double)location.WidthCells * location.HeightCells)
                : SortKey.Missing,
            r => As<UserLocation>(r) is { } location ? $"{location.WidthCells} x {location.HeightCells}" : "-"),
        Shown("Environment", r => SortKey.Of(TitleOf(As<UserLocation>(r)?.Environment))),
        Option("VisualMood", r => SortKey.Of(TitleOf(As<UserLocation>(r)?.VisualMood))),
        // the campaign's starting location first when ascending (user, 2026-11-13: "those with Campaign start at the
        // very top for Ascending"); the rows show it with an icon
        Shown("CampaignStart", r => As<UserLocation>(r) is { } location
            ? SortKey.Of(location.CampaignStart ? 0 : 1)
            : SortKey.Missing),
        ShownAuthorOption
    ];

    // QuestCategory compares by the game's own order: main, secondary, destiny, hidden, faction
    private static readonly CategoryOption[] QuestOptions =
    [
        Option("QuestCategory", r => As<UserQuest>(r) is { } quest ? SortKey.Of((int)quest.Category) : SortKey.Missing,
            r => As<UserQuest>(r)?.Category.ToString() ?? "-"),
        Option("AutoStart", r => As<UserQuest>(r) is { } quest ? SortKey.Of(quest.AutoStart) : SortKey.Missing),
        Option("StepCount", r => Number(As<UserQuest>(r)?.AllQuestStepDescriptions?.Count)),
        AuthorOption
    ];

    private static readonly CategoryOption[] ItemOptions =
    [
        Option("BaseItem", r => As<UserItem>(r) is { } item
            ? SortKey.Of(TitleOf(item.ReferenceItemDefinition) ?? item.ReferenceDefinition)
            : SortKey.Missing),
        Option("Cost", CostKey, CostText),
        Option("Language", r => SortKey.Of(As<UserItem>(r)?.Language)),
        AuthorOption
    ];

    private static readonly CategoryOption[] MerchantOptions =
    [
        Option("Markup", r => Number(As<UserMerchantInventory>(r)?.OverchargePercent)),
        Option("Buyback", r => Number(As<UserMerchantInventory>(r)?.BuyBackPercent)),
        Option("IdentifyFee", r => Number(As<UserMerchantInventory>(r)?.IdentifyCostGp)),
        Option("StockEntries", r => Number(As<UserMerchantInventory>(r)?.StockUnits?.Count)),
        AuthorOption
    ];

    private static readonly CategoryOption[] VariableOptions =
    [
        Option("VariableType", r => As<UserVariable>(r) is { } variable
                ? SortKey.Of((int)variable.VariableType)
                : SortKey.Missing,
            r => As<UserVariable>(r)?.VariableType.ToString() ?? "-"),
        Option("StartingValue", r => As<UserVariable>(r) is { } variable
            ? ValueKey(variable.VariableType, variable.VariableDefaultValueInt, variable.VariableDefaultValueBool,
                variable.VariableDefaultValueString)
            : SortKey.Missing),
        AuthorOption
    ];

    private static readonly CategoryOption[] BiomeOptions =
    [
        Option("Terrain", r => SortKey.Of(As<UserBiome>(r)?.TerrainType)),
        Option("EncounterTable", r => SortKey.Of(As<UserBiome>(r)?.EncounterTable)),
        AuthorOption
    ];

    private static readonly CategoryOption[] DialogOptions =
    [
        Option("DialogStates", r => Number(As<UserDialog>(r)?.AllDialogStates?.Count)),
        AuthorOption
    ];

    private static readonly CategoryOption[] EncounterTableOptions =
    [
        Option("Entries", r => Number(As<UserEncounterTable>(r)?.EncounterOccurences?.Count)),
        AuthorOption
    ];

    private static readonly CategoryOption[] LootPackOptions =
    [
        Option("Entries", r => Number(As<UserLootPack>(r)?.ItemOccurences?.Count)),
        AuthorOption
    ];

    // the running game's variables: scope (the game's order) and current value
    private static readonly CategoryOption[] CheatOptions =
    [
        Option("Scope", r => VariableOf(r) is { } variable ? SortKey.Of((int)variable.Scope) : SortKey.Missing,
            r => VariableOf(r)?.Scope.ToString() ?? "-"),
        Option("CurrentValue", r => VariableOf(r) is { } variable
            ? ValueKey(variable.Type, variable.IntValue, variable.BoolValue, variable.StringValue)
            : SortKey.Missing)
    ];

    #endregion

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
                new ContentList("Biomes", BiomeOptions, x => x.userBiomesTable, x => x.moveBiomeUpButton, x => x.moveBiomeDownButton,
                    x => Clear(ref x.selectedBiome),
                    x => x.biomesGroupItem)
            },
            {
                "RefreshDialogs",
                new ContentList("Dialogs", DialogOptions, x => x.userDialogsTable, x => x.moveDialogUpButton, x => x.moveDialogDownButton,
                    x => Clear(ref x.selectedDialog),
                    x => x.dialogsGroupItem)
            },
            {
                "RefreshEncounters",
                new ContentList("Encounters", EncounterOptions, x => x.userEncountersTable, x => x.moveEncounterUpButton, x => x.moveEncounterDownButton,
                    x => Clear(ref x.selectedEncounter),
                    x => x.encountersGroupItem)
            },
            {
                "RefreshEncounterTables",
                new ContentList("EncounterTables", EncounterTableOptions, x => x.userEncounterTablesTable, x => x.moveEncounterTableUpButton,
                    x => x.moveEncounterTableDownButton, x => Clear(ref x.selectedEncounterTable),
                    x => x.encounterTablesGroupItem)
            },
            {
                "RefreshItems",
                new ContentList("Items", ItemOptions, x => x.userItemsTable, x => x.moveItemUpButton, x => x.moveItemDownButton,
                    x => Clear(ref x.selectedItem),
                    x => x.itemsGroupItem)
            },
            {
                "RefreshLocations",
                new ContentList("Locations", LocationOptions, x => x.userLocationsTable, x => x.moveLocationUpButton, x => x.moveLocationDownButton,
                    x => Clear(ref x.selectedLocation),
                    x => x.locationsGroupItem)
            },
            {
                "RefreshLootPacks",
                new ContentList("LootPacks", LootPackOptions, x => x.userLootPacksTable, x => x.moveLootPackUpButton, x => x.moveLootPackDownButton,
                    x => Clear(ref x.selectedLootPack),
                    x => x.lootPacksGroupItem)
            },
            {
                "RefreshMerchantInventories",
                new ContentList("MerchantInventories", MerchantOptions, x => x.userMerchantInventoriesTable, x => x.moveMerchantInventoryUpButton,
                    x => x.moveMerchantInventoryDownButton, x => Clear(ref x.selectedMerchantInventory),
                    x => x.merchantIventoriesGroupItem)
            },
            {
                "RefreshMonsters",
                new ContentList("Monsters", CharacterOptions, x => x.userMonstersTable, x => x.moveMonsterUpButton, x => x.moveMonsterDownButton,
                    x => Clear(ref x.selectedMonster),
                    x => x.monstersGroupItem)
            },
            {
                "RefreshNpcs",
                new ContentList("Npcs", CharacterOptions, x => x.userNpcsTable, x => x.moveNpcUpButton, x => x.moveNpcDownButton,
                    x => Clear(ref x.selectedNpc),
                    x => x.npcsGroupItem)
            },
            {
                "RefreshQuests",
                new ContentList("Quests", QuestOptions, x => x.userQuestsTable, x => x.moveQuestUpButton, x => x.moveQuestDownButton,
                    x => Clear(ref x.selectedQuest),
                    x => x.questsGroupItem)
            },
            {
                "RefreshVariables",
                new ContentList("Variables", VariableOptions, x => x.userVariablesTable, x => x.moveVariableUpButton, x => x.moveVariableDownButton,
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
            string id,
            CategoryOption[] options,
            Func<UserCampaignEditorScreen, Transform> table,
            Func<UserCampaignEditorScreen, Button> moveUp,
            Func<UserCampaignEditorScreen, Button> moveDown,
            Func<UserCampaignEditorScreen, bool> clearSelection,
            Func<UserCampaignEditorScreen, CampaignContentGroupItem> group)
        {
            internal readonly Func<UserCampaignEditorScreen, bool> ClearSelection = clearSelection;
            internal readonly Func<UserCampaignEditorScreen, CampaignContentGroupItem> Group = group;

            // the settings key of the list's category sort, and the keys it can be sorted by
            internal readonly string Id = id;
            internal readonly CategoryOption[] Options = options;
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
            var option = DungeonMakerListTools.ChosenOption(list.Id, list.Options, out var descending);
            var hidSelected = DungeonMakerListTools.Apply(
                list.Table(screen), Editor, ContentName, ContentTitle, IsSelected, option?.Key, descending,
                option?.Display);
            var group = list.Group(screen);

            // the list's category sort row is shown while its group is open
            if (group)
            {
                DungeonMakerListTools.ShowListSortRow(group.transform, !group.folded);
            }

            if ((hidSelected || (group && group.folded)) && list.ClearSelection(screen))
            {
                screen.Dirty = true;
            }

            // the move arrows swap neighbours in the campaign's order, which a category sort also changes
            if (DungeonMakerListTools.IsPlain(Editor) && option == null)
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

    //PATCH: a list's group in the campaign editor shows or hides its parts (UpdateState switches its item table and
    //buttons on or off by its folded flag, from every way of folding: the header, a shortcut icon, Fold All and
    //Unfold All, and the editor's start). The list's category sort row follows them.
    [HarmonyPatch(typeof(CampaignContentGroupItem), nameof(CampaignContentGroupItem.UpdateState))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CampaignContentGroupItem_UpdateState
    {
        [UsedImplicitly]
        public static void Postfix(CampaignContentGroupItem __instance)
        {
            DungeonMakerListTools.ShowListSortRow(__instance.transform, !__instance.folded);
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

            OrderGroups(screen);
        }

        // the twelve groups in the order the screen was built with, taken before the mod first moves any
        private static readonly ConditionalWeakTable<UserCampaignEditorScreen, List<Transform>> GameOrder = new();

        // The twelve lists' groups in the game's order or by the title on their header (the "Categories:" toggle).
        // The shortcut icons and the game reach the groups through its fields, not by their place, and a group's
        // separator follows its own fold state only (CampaignContentGroupItem.UpdateState), so moving them is safe.
        private static void OrderGroups(UserCampaignEditorScreen screen)
        {
            if (!GameOrder.TryGetValue(screen, out var groups))
            {
                groups = UserCampaignEditorScreen_RefreshLists.Lists.Values
                    .Select(x => x.Group(screen))
                    .Where(x => x)
                    .Select(x => x.transform)
                    .OrderBy(x => x.GetSiblingIndex())
                    .ToList();
                GameOrder.Add(screen, groups);
            }

            DungeonMakerListTools.OrderSections(groups, x =>
            {
                var header = x.GetComponent<CampaignContentGroupItem>()?.HeaderLabel;

                return header && header.TMP_Text ? header.TMP_Text.text : x.name;
            });
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

    //PATCH: the bar on the campaign editor, above the content lists (its Clear button is a copy of Import)
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
            void Changed()
            {
                // the rows already hold their items: the twelve lists are only ordered and filtered again, without
                // the game binding everything anew at every letter typed
                UserCampaignEditorScreen_FoldChanged.ApplyToAll(__instance);

                // back to the plain view: a refresh has the game work out the move arrows again
                if (DungeonMakerListTools.IsPlain(Editor))
                {
                    __instance.Dirty = true;
                }
            }

            const string SourceButton = "RightPanel/ExportGroup/ImportContentsButton";

            // the editor's bar has no category row: each list has its own, in its group (below)
            DungeonMakerListTools.Ensure(__instance.transform, Editor, "RightPanel/UserContentScrollview", SourceButton,
                "ModUi/&DungeonMakerSortTitle", Changed, null, sections: true);

            var source = __instance.transform.Find(SourceButton);

            foreach (var list in UserCampaignEditorScreen_RefreshLists.Lists.Values)
            {
                var group = list.Group(__instance);

                DungeonMakerListTools.EnsureListSortRow(
                    group ? group.transform : null, source ? source.gameObject : null, list.Id, list.Options, Changed);
            }

            // shows each row with its list
            UserCampaignEditorScreen_FoldChanged.ApplyToAll(__instance);
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
            var option = DungeonMakerListTools.ChosenOption(PickerList, VariableOptions, out var descending);

            if (!DungeonMakerListTools.Apply(
                    picker.userVariablesTable, Picker, ContentName, ContentTitle, IsSelected, option?.Key,
                    descending, option?.Display))
            {
                return;
            }

            picker.selectedVariable = -1;
            picker.Dirty = true;
        }
    }

    //PATCH: the bar on the variable picker, above its list (its Clear button is a copy of Cancel)
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
                () => UserVariableSelectionModal_EnumerateLines.ApplyTo(__instance),
                () => new CategoryView(PickerList, Gui.Localize("ModUi/&DungeonMakerCategoryVariables"),
                    VariableOptions));
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

            var option = DungeonMakerListTools.ChosenOption(CheatsList, CheatOptions, out var descending);

            DungeonMakerListTools.Apply(
                table, Cheats, VariableName, VariableType, null, option?.Key, descending, option?.Display);
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
        }
    }

    //PATCH: the bar on the Cheats > Campaign Variables panel, above its list (its Clear button is a copy of Close)
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
                () => CampaignVariablesModal_EnumerateVariableLines.ApplyTo(__instance),
                () => new CategoryView(CheatsList, Gui.Localize("ModUi/&DungeonMakerCategoryVariables"),
                    CheatOptions));
        }
    }
}
