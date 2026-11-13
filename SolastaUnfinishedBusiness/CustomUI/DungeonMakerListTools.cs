using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

// Sorting and searching the Dungeon Maker's lists (issue #42; the alphabetical order comes from BlackguardRogue's
// Dungeon Maker tools): the twelve content lists of the campaign editor, the variable picker, the
// Cheats > Campaign Variables panel, and the location editor's gadget, prop and room palettes (which replace the
// former "Enable assets sorting on Dungeon Maker editor" setting; the palettes' bar has two rows, as a palette is
// only 432 units wide: the sort, then the search, see BlueprintPaletteSortPatcher).
//
// The campaign editor's and the palettes' bars also have a CATEGORY ORDER toggle (on the editor's sort row, on a
// row of its own in a palette) (user, 2026-11-13: "a toggle
// for sorting the categories themselves ... between default and alphabetical", for both): "Categories: Default"
// keeps the sections (the editor's twelve lists, a palette's categories) in the game's order, "Categories: A-Z"
// orders them by the title on their header. One saved choice for all of them
// (Settings.DungeonMakerCategoriesAlphabetical); no direction, as a short list of headers gains little from Z-A. Each of the three screens gets a bar on a row of its own, above its list and
// as wide as it, with, from left to right (user directions, 2026-11-13: the bar "should be at the very top"; later
// "Search should be on the right, and sort on the left"),
//
//   "Sort:" and a      what the list is ordered by: the campaign's own order, the name, or a second key
//   dropdown           (the title for content, the type for variables)
//   a direction arrow  up for ascending, down for descending (a copy of the editor's move-up button)
//   a search field     set apart from the sort by a gap; only rows whose name or title contains the text are shown
//   a clear button     empties the search field
//
// and a CATEGORY SORT for each list (user direction through Codex, 2026-11-13: "The category specific sort will be
// priority, with the main sort as the secondary"; the user's choices: a sort per list, with its own arrow):
//
//   "Sort:"            in the campaign editor, a row inside each list's foldable group, directly under its header
//   a dropdown         and above its items (user, 2026-11-13: "[Quests category expanded] / Sort: [Default] [arrow]
//   a direction arrow  / {... Actual quest items...} / [New quest button]"); shown while the group is open. The
//                      variable picker and the cheat panel, which have one list and no headers, have it as a second
//                      row of the bar, under the main sort, as "Variables:".
//
// The dropdown holds Default, which every list starts on and which orders it by the main sort alone (user, same day:
// "an option for Default (and start with default), where the category just follows the main"), then the list's
// own keys (hit points, challenge rating, cost, ...). A list is ordered by its category key first, in that
// direction, and rows with equal keys by the main sort, in its direction, then by the campaign's order. Numbers
// compare as numbers; an entry without a value for the key comes last in both directions.
//
// While a list has a category sort whose field its rows don't show (all but a location's author, start levels,
// environment and campaign start), each row carries a small overlay at its right end with that row's value (user,
// 2026-11-13: "a temporary overlay for each entry that shows the value of the field while that particular sort is
// active"). It goes when the list is back on Default.
//
// The sort and its direction are one saved choice for the three screens (Settings.DungeonMakerListSort and
// DungeonMakerListSortDescending). The search text belongs to one screen and is emptied when it is shown.
//
// IT ONLY CHANGES WHAT IS SHOWN. The first version of the alphabetical order sorted the campaign's own lists, so the
// order was saved with the campaign, and auto-start quests, which the game starts in list order, entered the
// journal in another order. Here the lists are never touched:
//
//   - All three screens fill their table the same way: row object i is bound to item i of the list.
//   - Right after that (Bound), the row objects are remembered in that order, which is the list's own order.
//   - Apply then puts the row objects in the wanted order among themselves (Transform.SetSiblingIndex), which is
//     the order the table's layout draws them in, and switches off the rows the search leaves out.
//   - Every row keeps the item it was bound to, and the game finds out what a click means from the row's item
//     (list.IndexOf(line.UserContent)), not from the row's position. So select, edit, duplicate, delete and "pick"
//     act on the item that was clicked, and the selection survives a change of sort or direction.
//   - A search that leaves the selected row out clears the selection, so no button acts on an item that isn't
//     shown. In the campaign editor a list that is folded away loses its selection too; see UserListSortPatcher.
//   - On its next refresh the game binds row object i to item i again, whichever object that now is, and switches
//     it on; the view is then applied again.
//
// Names compare without regard to case; rows that compare equal keep the list's order, ascending or descending.
internal static class DungeonMakerListTools
{
    internal enum Sort
    {
        // the order of the campaign's own list
        Default,

        // the internal name
        Name,

        // the screen's second key: the title for content, the type for variables
        Second
    }

    private const string BarName = "FbListTools";
    private const string SortName = "Sort";
    private const string DirectionName = "Direction";
    private const string SearchName = "Search";
    private const string ClearName = "Clear";
    private const string SortLabelName = "SortLabel";
    private const string GapName = "Gap";
    private const string MainRowName = "MainRow";
    private const string CategoryRowName = "CategoryRow";
    private const string CategoryLabelName = "CategoryLabel";
    private const string CategoryName = "Category";
    private const string CategoryDirectionName = "CategoryDirection";
    private const string SearchRowName = "SearchRow";
    private const string CategoryOrderName = "CategoryOrder";
    private const string CategoryOrderRowName = "CategoryOrderRow";

    // the category order toggle's room around its text
    private const float ButtonTextPadding = 28f;
    private const string ListSortRowName = "FbListSort";
    private const string OverlayName = "FbSortValue";

    // the per-list sort row: indented from the group's left edge, under the header's fold arrow
    private const int ListSortIndent = 24;

    // the value overlay on a row: its width, and its gap from the row's right end
    private const float OverlayWidth = 180f;
    private const float OverlayMargin = 8f;

    // the bar's controls, in the units of the game's 1920 x 1080 interface; the search field takes what the other
    // controls leave of the bar's width
    // the sort dropdown: room for its longest choice ("По умолчанию") and its own open arrow
    private const float SortWidth = 180f;

    // the space that sets the sort controls apart from the search and Clear (user direction, 2026-11-13: "Add a
    // distinct padding between the search clear and the sort"), on top of the usual spacing either side
    private const float GroupGap = 28f;
    private const float SearchMinWidth = 160f;
    private const float ClearWidth = 90f;
    private const float ControlHeight = 34f;
    private const float Spacing = 6f;

    // between the list and the bar
    private const float Gap = 6f;

    // between a palette's bar and its blueprints (user, 2026-11-13: "In the palette screen, add a padding between the
    // search row and the items"): the palettes' category headers start right at the top of their list
    private const float PaletteGap = 18f;

    // the rows of each table in the order the game bound them, i.e. the order of the list
    private static readonly ConditionalWeakTable<Transform, List<Transform>> BoundRows = new();

    // the search text of each screen, by the key the screen passes
    private static readonly Dictionary<string, string> Searches = [];

    private static string _lastProblem = string.Empty;

    // the editor's two arrow pictures, for the direction button
    private static Sprite _upArrow;
    private static Sprite _downArrow;

    internal static Sort Kind => (Sort)Mathf.Clamp(Main.Settings.DungeonMakerListSort, 0, 2);

    // the sections themselves in title order rather than the game's
    internal static bool CategoriesAlphabetical => Main.Settings.DungeonMakerCategoriesAlphabetical;

    private static bool Descending => Main.Settings.DungeonMakerListSortDescending;

    internal static string Search(string screen)
    {
        return Searches.TryGetValue(screen, out var text) ? text : string.Empty;
    }

    // empties a screen's search before the game fills its list for a new showing (Ensure empties the field)
    internal static void ClearSearch(string screen)
    {
        Searches[screen] = string.Empty;
    }

    // A list's category sort: the option's id (null for Default, the main sort alone) and whether it is
    // descending. A list with nothing saved is on Default.
    internal static (string Id, bool Descending) CategoryChoice(string list)
    {
        foreach (var entry in (Main.Settings.DungeonMakerCategorySorts ?? string.Empty).Split(';'))
        {
            var equals = entry.IndexOf('=');

            if (equals <= 0 || entry.Substring(0, equals) != list)
            {
                continue;
            }

            var rest = entry.Substring(equals + 1);
            var colon = rest.LastIndexOf(':');

            return colon < 0
                ? (rest, false)
                : (rest.Substring(0, colon), rest.Substring(colon + 1) == "1");
        }

        return (null, false);
    }

    // the option a list is sorted by now, from its own options, or null
    internal static CategoryOption ChosenOption(string list, IReadOnlyList<CategoryOption> options, out bool descending)
    {
        var (id, isDescending) = CategoryChoice(list);

        descending = isDescending;

        return id == null ? null : options?.FirstOrDefault(x => x.Id == id);
    }

    private static void SetCategoryChoice(string list, string id, bool descending)
    {
        var entries = (Main.Settings.DungeonMakerCategorySorts ?? string.Empty)
            .Split(';')
            .Where(x => x.Length > 0 && !x.StartsWith(list + "=", StringComparison.Ordinal))
            .ToList();

        if (id != null)
        {
            entries.Add($"{list}={id}:{(descending ? 1 : 0)}");
        }

        Main.Settings.DungeonMakerCategorySorts = string.Join(";", entries);
    }

    // the list is shown exactly as the game shows it: its own order, top to bottom, nothing left out
    internal static bool IsPlain(string screen)
    {
        return Kind == Sort.Default && !Descending && Search(screen).Length == 0;
    }

    #region The view

    // The game has just filled this table: remember its bound rows in the order they are in now. isBound says
    // whether a child of the table is a row the game bound to an item just now (the screens keep unused pooled
    // rows there too, and those may still hold an old item; see UserListSortPatcher.IsBoundRow).
    internal static void Bound(Transform table, Func<Transform, bool> isBound)
    {
        if (!table)
        {
            return;
        }

        var rows = BoundRows.GetOrCreateValue(table);

        rows.Clear();

        for (var i = 0; i < table.childCount; i++)
        {
            var child = table.GetChild(i);

            if (isBound(child))
            {
                rows.Add(child);
            }
        }
    }

    // Orders and filters the rows remembered for this table. name and second give a row's two sort keys; a row
    // is shown when its name or its second key contains the screen's search text. category, if given, is the
    // list's category key, which orders the rows before the main sort does. Returns true when the search left out
    // a row that isSelected says is the selected one: the screen still holds that selection, and its buttons
    // (edit, delete, validate) would act on an item that can't be seen. Whether to clear it is the caller's
    // decision.
    internal static bool Apply(
        Transform table, string screen, Func<Transform, string> name, Func<Transform, string> second,
        Func<Transform, bool> isSelected = null, Func<Transform, SortKey> category = null,
        bool categoryDescending = false, Func<Transform, string> overlay = null)
    {
        if (!table || !BoundRows.TryGetValue(table, out var rows) || rows.Count == 0)
        {
            return false;
        }

        var hidSelected = false;

        var search = Search(screen);
        var items = new List<Row>();

        foreach (var row in rows)
        {
            // a row the game has taken away since (released to its pool)
            if (!row || row.parent != table)
            {
                continue;
            }

            items.Add(new Row
            {
                Transform = row,
                Index = items.Count,
                Name = name(row) ?? string.Empty,
                Second = second(row) ?? string.Empty,
                Category = category?.Invoke(row) ?? SortKey.Missing
            });
        }

        // One comparison, so the order is fully decided: the category key first (in its own direction), then the
        // main sort in its direction, and last the campaign's order, so rows that are equal on every key keep
        // their campaign order in both directions. The campaign's own order as the main sort, descending, is the
        // list from its end.
        var kind = Kind;
        var descending = Descending;

        items.Sort((a, b) =>
        {
            int result;

            if (category != null)
            {
                result = SortKey.Compare(a.Category, b.Category, categoryDescending);

                if (result != 0)
                {
                    return result;
                }
            }

            result = kind switch
            {
                Sort.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
                Sort.Second => StringComparer.OrdinalIgnoreCase.Compare(a.Second, b.Second) is var bySecond and not 0
                    ? bySecond
                    : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
                _ => a.Index.CompareTo(b.Index)
            };

            if (descending)
            {
                result = -result;
            }

            return result != 0 ? result : a.Index.CompareTo(b.Index);
        });

        var sorted = items;

        // The rows are the table's first children (the screens keep unused pooled rows after them), and they only
        // change places among themselves, so the game still finds a row object at each of the first positions.
        for (var i = 0; i < sorted.Count; i++)
        {
            var row = sorted[i];
            var shown = search.Length == 0 ||
                        row.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        row.Second.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

            if (row.Transform.GetSiblingIndex() != i)
            {
                row.Transform.SetSiblingIndex(i);
            }

            if (row.Transform.gameObject.activeSelf != shown)
            {
                row.Transform.gameObject.SetActive(shown);
            }

            // the category field's value, while the list is sorted by a field its rows don't show
            SetOverlay(row.Transform, category != null && overlay != null ? overlay(row.Transform) : null);

            hidSelected |= !shown && isSelected != null && isSelected(row.Transform);
        }

        return hidSelected;
    }

    #endregion

    // Puts a screen's sections in the chosen order: the game's (original, the order they had before the mod first
    // moved them) or by title. The sections change places among the places they hold, which must follow each other
    // (they do: the palette's bound categories are its table's first children, the editor's twelve groups the
    // content's). Returns the sections in the order they now stand.
    internal static List<Transform> OrderSections(IReadOnlyList<Transform> original, Func<Transform, string> title)
    {
        var sections = original.Where(x => x).ToList();

        if (sections.Count < 2)
        {
            return sections;
        }

        var ordered = CategoriesAlphabetical
            ? sections
                .Select((x, i) => (Section: x, Index: i, Title: title(x) ?? string.Empty))
                .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Index)
                .Select(x => x.Section)
                .ToList()
            : sections;

        var parent = sections[0].parent;
        var first = sections.Min(x => x.GetSiblingIndex());

        if (sections.Any(x => x.parent != parent) || sections.Max(x => x.GetSiblingIndex()) - first != sections.Count - 1)
        {
            Problem("Dungeon Maker list tools: the sections don't follow each other, so their order is left as it is.");

            return sections.OrderBy(x => x.GetSiblingIndex()).ToList();
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].GetSiblingIndex() != first + i)
            {
                ordered[i].SetSiblingIndex(first + i);
            }
        }

        return ordered;
    }

    #region The bar

    // Adds the bar to a screen if it isn't there yet and brings it up to date. Called each time the screen is
    // shown: the sort may have been changed on another screen, and the search starts empty.
    //   screen      the key of this screen's search text
    //   listPath    the scroll view that holds the list, from the screen's root; the bar takes a strip at its foot
    //   sourcePath  a button of the screen to copy for the bar's two buttons, from the screen's root
    //   secondTerm  the sort dropdown's choice for the second key ("Title", "Type")
    //   changed     the view changed: apply it to the screen's tables
    //   category    the list the bar's category row applies to, with its options; null for no such row (the
    //               campaign editor, whose lists have theirs in their groups, see EnsureListSortRow)
    //   stacked     the sort on a row of its own and the search under it, for a narrow screen (the palettes)
    //   keepSearch  keep the screen's search (the palettes are bound again by the game while they stay open)
    //   sourceRoot  where sourcePath starts, when the button to copy belongs to another screen
    //   sections    the screen has sections to order (the editor, the palettes): the category order toggle
    internal static void Ensure(
        Transform root, string screen, string listPath, string sourcePath, string secondTerm, Action changed,
        Func<CategoryView> category, bool stacked = false, bool keepSearch = false, Transform sourceRoot = null,
        bool sections = false)
    {
        if (!keepSearch || !Searches.ContainsKey(screen))
        {
            Searches[screen] = string.Empty;
        }

        var list = root ? root.Find(listPath) as RectTransform : null;
        var sourceFrom = sourceRoot ? sourceRoot : root;
        var source = sourceFrom ? sourceFrom.Find(sourcePath) : null;

        if (!list || !source || !source.GetComponent<Button>())
        {
            // a game update that moved them: say so once, and leave the screen as it is
            Problem(
                $"Dungeon Maker list tools: '{listPath}' or '{sourcePath}' not found on {(root ? root.name : "?")}.");

            return;
        }

        var bar = list.parent.Find(BarName);

        if (!bar)
        {
            bar = AddBar(list, source.gameObject, category != null, stacked, sections);
        }

        var sort = FindDeep(bar, SortName)?.GetComponent<GuiDropdown>();
        var direction = FindDeep(bar, DirectionName)?.GetComponent<Button>();
        var search = FindDeep(bar, SearchName)?.GetComponent<TMP_InputField>();
        var clear = FindDeep(bar, ClearName)?.GetComponent<Button>();
        var sortLabel = FindDeep(bar, SortLabelName)?.GetComponent<GuiLabel>();
        var categoryLabel = FindDeep(bar, CategoryLabelName)?.GetComponent<GuiLabel>();
        var categoryDropdown = FindDeep(bar, CategoryName)?.GetComponent<GuiDropdown>();
        var categoryDirection = FindDeep(bar, CategoryDirectionName)?.GetComponent<Button>();
        var categoryOrder = FindDeep(bar, CategoryOrderName)?.GetComponent<Button>();

        // the list the category row shows now; its dropdown's choices are Default, then that list's options
        CategoryView shown = null;

        // "Sort:" stands in front of the dropdown, which holds only the choices (user direction, 2026-11-13:
        // "split them so that the "Sort:" is outside the dropdown"). Set through the game label, which otherwise
        // writes its own text when it wakes up.
        if (sortLabel)
        {
            sortLabel.Text = Gui.Localize("ModUi/&DungeonMakerSortLabel");
        }

        // The sort is a dropdown (user direction, 2026-11-13: "Can the sort be a dropdown instead of a cycle
        // toggle?"): its three choices, in the order of the Sort values, with this screen's second key.
        if (sort)
        {
            sort.onValueChanged = new TMP_Dropdown.DropdownEvent();
            sort.ClearOptions();
            sort.AddOptions(new List<string>
            {
                Gui.Localize("ModUi/&DungeonMakerSortDefault"),
                Gui.Localize("ModUi/&DungeonMakerSortName"),
                Gui.Localize(secondTerm)
            });
        }

        void Refresh()
        {
            if (sort)
            {
                sort.SetValueWithoutNotify((int)Kind);
            }

            // the direction is an arrow (user direction, 2026-11-13: "Ascending and Descending should also just
            // be an arrow"); its tooltip says which
            SetArrow(direction, Descending);
            SetText(clear, "ModUi/&DungeonMakerSearchClear");
            SetText(categoryOrder,
                CategoriesAlphabetical
                    ? "ModUi/&DungeonMakerCategoryOrderAlphabetical"
                    : "ModUi/&DungeonMakerCategoryOrderDefault");
            FitButtonToText(categoryOrder);
            RefreshCategory();

            // Nothing to clear while the field is empty. The field's own text decides, not the search made from
            // it: spaces alone are no search, but they are in the field and Clear must be able to remove them.
            if (clear)
            {
                clear.interactable = search && !string.IsNullOrEmpty(search.text);
            }
        }

        void RefreshCategory()
        {
            try
            {
                shown = category?.Invoke();
            }
            catch (Exception e)
            {
                Main.Error(e);
                shown = null;
            }

            var (id, isDescending) = shown == null ? (null, false) : CategoryChoice(shown.ListId);
            var options = shown?.Options ?? [];
            var index = id == null ? -1 : options.ToList().FindIndex(x => x.Id == id);

            if (categoryLabel)
            {
                categoryLabel.Text = shown == null
                    ? Gui.Localize("ModUi/&DungeonMakerCategoryNoList")
                    : string.Format(Gui.Localize("ModUi/&DungeonMakerCategoryLabel"), shown.Title);
            }

            if (categoryDropdown)
            {
                var texts = new List<string> { Gui.Localize("ModUi/&DungeonMakerCategoryNone") };

                texts.AddRange(options.Select(x => Gui.Localize(x.Term)));
                categoryDropdown.ClearOptions();
                categoryDropdown.AddOptions(texts);
                categoryDropdown.SetValueWithoutNotify(index + 1);
                categoryDropdown.interactable = shown != null && options.Count > 0;
            }

            if (categoryDirection)
            {
                SetArrow(categoryDirection, isDescending);
                categoryDirection.interactable = index >= 0;
            }

            MatchLabels(sortLabel, categoryLabel);
        }

        void Changed()
        {
            Refresh();

            try
            {
                changed();
            }
            catch (Exception e)
            {
                Main.Error(e);
            }
        }

        // fresh events each time: "changed" belongs to this showing of the screen
        if (sort)
        {
            sort.onValueChanged.AddListener(value =>
            {
                Main.Settings.DungeonMakerListSort = Mathf.Clamp(value, 0, 2);
                Changed();
            });
        }

        if (categoryDropdown)
        {
            categoryDropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            categoryDropdown.onValueChanged.AddListener(value =>
            {
                if (shown == null)
                {
                    return;
                }

                var options = shown.Options;
                var id = value >= 1 && value <= options.Count ? options[value - 1].Id : null;

                SetCategoryChoice(shown.ListId, id, CategoryChoice(shown.ListId).Descending);
                Changed();
            });
        }

        if (categoryOrder)
        {
            categoryOrder.onClick = new Button.ButtonClickedEvent();
            categoryOrder.onClick.AddListener(() =>
            {
                Main.Settings.DungeonMakerCategoriesAlphabetical = !Main.Settings.DungeonMakerCategoriesAlphabetical;
                Changed();
            });
        }

        if (categoryDirection)
        {
            categoryDirection.onClick = new Button.ButtonClickedEvent();
            categoryDirection.onClick.AddListener(() =>
            {
                if (shown == null)
                {
                    return;
                }

                var (id, isDescending) = CategoryChoice(shown.ListId);

                if (id != null)
                {
                    SetCategoryChoice(shown.ListId, id, !isDescending);
                    Changed();
                }
            });
        }

        if (direction)
        {
            direction.onClick = new Button.ButtonClickedEvent();
            direction.onClick.AddListener(() =>
            {
                Main.Settings.DungeonMakerListSortDescending = !Main.Settings.DungeonMakerListSortDescending;
                Changed();
            });
        }

        if (search)
        {
            search.onValueChanged = new TMP_InputField.OnChangeEvent();
            search.SetTextWithoutNotify(Search(screen));
            search.onValueChanged.AddListener(text =>
            {
                Searches[screen] = (text ?? string.Empty).Trim();
                Changed();
            });
        }

        if (clear)
        {
            clear.onClick = new Button.ButtonClickedEvent();
            clear.onClick.AddListener(() =>
            {
                // setting the field's text raises its "value changed", which empties the search and applies it
                if (search)
                {
                    search.text = string.Empty;
                }
            });
        }

        Refresh();
    }

    // The campaign editor's category sort row for one list: "Sort:", a dropdown and an arrow, inside the list's
    // foldable group (CampaignContentGroupItem), right after its header. The group stacks its children with a
    // vertical layout (header, the item table, the buttons, a separator, from the prefab), and the game binds the
    // items by their place in the item table, which this row is not in. Made once per group, then brought up to
    // date and given fresh events each time the editor is shown.
    //   group        the list's group
    //   sourceButton a button of the screen whose label the "Sort:" text copies
    //   listId       the settings key of the list's category sort
    //   options      the keys it can be sorted by
    //   changed      the list's sort changed: apply it
    internal static void EnsureListSortRow(
        Transform group, GameObject sourceButton, string listId, IReadOnlyList<CategoryOption> options,
        Action changed)
    {
        if (!group || !sourceButton)
        {
            return;
        }

        var row = group.Find(ListSortRowName);

        if (!row)
        {
            // built under an inactive holder, like the bar, so the copies only wake up once they are set up
            var holder = new GameObject("FbHolder");

            holder.SetActive(false);
            row = AddRow(holder.transform, ListSortRowName, TextAnchor.MiddleLeft, ListSortIndent);
            AddSortLabel(row, sourceButton, SortLabelName);
            AddSortDropdown(row, CategoryName);
            AddArrow(row, CategoryDirectionName);
            row.SetParent(group, false);
            row.SetSiblingIndex(1);
            Object.Destroy(holder);
        }

        var label = row.Find(SortLabelName)?.GetComponent<GuiLabel>();
        var dropdown = row.Find(CategoryName)?.GetComponent<GuiDropdown>();
        var arrow = row.Find(CategoryDirectionName)?.GetComponent<Button>();

        if (label)
        {
            label.Text = Gui.Localize("ModUi/&DungeonMakerSortLabel");
            FitToText(label);
        }

        // As wide as the group's header. The group's other parts each carry the list's width themselves (986 units
        // in the prefab, HeaderGroup, the item table, ButtonsBar), so the group's layout doesn't give its children
        // a width, and a new row kept the 100 units a new object starts with: the dropdown and the arrow, which
        // have fixed widths, then left "Sort:" no room at all (seen in game on cd9aa39a, 2026-11-13).
        var header = group.Find("HeaderGroup") as RectTransform;
        var rowRect = (RectTransform)row;
        var width = header && header.rect.width > 0 ? header.rect.width : 986f;

        rowRect.sizeDelta = new Vector2(width, rowRect.sizeDelta.y);

        if (row.TryGetComponent<LayoutElement>(out var rowLayout))
        {
            rowLayout.minWidth = rowLayout.preferredWidth = width;
        }

        if (dropdown)
        {
            var texts = new List<string> { Gui.Localize("ModUi/&DungeonMakerCategoryNone") };

            texts.AddRange(options.Select(x => Gui.Localize(x.Term)));
            dropdown.onValueChanged = new TMP_Dropdown.DropdownEvent();
            dropdown.ClearOptions();
            dropdown.AddOptions(texts);
        }

        void RefreshRow()
        {
            var (id, descending) = CategoryChoice(listId);
            var index = id == null ? -1 : options.ToList().FindIndex(x => x.Id == id);

            if (dropdown)
            {
                dropdown.SetValueWithoutNotify(index + 1);
            }

            if (arrow)
            {
                SetArrow(arrow, descending);
                arrow.interactable = index >= 0;
            }
        }

        void Changed()
        {
            RefreshRow();

            try
            {
                changed();
            }
            catch (Exception e)
            {
                Main.Error(e);
            }
        }

        if (dropdown)
        {
            dropdown.onValueChanged.AddListener(value =>
            {
                var id = value >= 1 && value <= options.Count ? options[value - 1].Id : null;

                SetCategoryChoice(listId, id, CategoryChoice(listId).Descending);
                Changed();
            });
        }

        if (arrow)
        {
            arrow.onClick = new Button.ButtonClickedEvent();
            arrow.onClick.AddListener(() =>
            {
                var (id, descending) = CategoryChoice(listId);

                if (id != null)
                {
                    SetCategoryChoice(listId, id, !descending);
                    Changed();
                }
            });
        }

        RefreshRow();
    }

    // a button as wide as its label's text and some room around it, so a longer translation fits
    private static void FitButtonToText(Button button)
    {
        var text = button ? button.transform.Find("Label")?.GetComponent<GuiLabel>()?.TMP_Text : null;

        if (!text || !button.TryGetComponent<LayoutElement>(out var layout))
        {
            return;
        }

        layout.minWidth = layout.preferredWidth =
            Mathf.Ceil(text.GetPreferredValues(text.text).x) + ButtonTextPadding;
    }

    // a label as wide as its text, measured, not left to the layout
    private static void FitToText(GuiLabel label)
    {
        var text = label ? label.TMP_Text : null;

        if (!text || !text.TryGetComponent<LayoutElement>(out var layout))
        {
            return;
        }

        layout.minWidth = layout.preferredWidth = Mathf.Ceil(text.GetPreferredValues(text.text).x) + 4f;
    }

    // Shows text in the row's value overlay, made the first time it is needed; null hides it. The overlay is laid
    // over the row's right end (it takes no part in the row's own layout) on a dark backing, in the row's
    // smallest font, so it can be read whatever the row shows there.
    private static void SetOverlay(Transform row, string text)
    {
        var overlay = row.Find(OverlayName);

        if (text == null)
        {
            if (overlay && overlay.gameObject.activeSelf)
            {
                overlay.gameObject.SetActive(false);
            }

            return;
        }

        if (!overlay)
        {
            overlay = MakeOverlay(row);

            if (!overlay)
            {
                return;
            }
        }

        overlay.SetAsLastSibling();
        overlay.gameObject.SetActive(true);

        var label = overlay.GetComponentInChildren<TMP_Text>(true);

        if (label)
        {
            label.text = text;
        }
    }

    private static Transform MakeOverlay(Transform row)
    {
        // the row's own text style: its smallest label
        var source = row.GetComponentsInChildren<TMP_Text>(true)
            .Where(x => x.font && x.fontSize > 0)
            .OrderBy(x => x.fontSize)
            .FirstOrDefault();

        if (!source)
        {
            return null;
        }

        var overlay = new GameObject(OverlayName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        var rect = (RectTransform)overlay.transform;

        rect.SetParent(row, false);
        overlay.GetComponent<LayoutElement>().ignoreLayout = true;
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.sizeDelta = new Vector2(OverlayWidth, -OverlayMargin);
        rect.anchoredPosition = new Vector2(-OverlayMargin, 0f);

        var backing = overlay.GetComponent<Image>();

        backing.color = new Color(0.1f, 0.1f, 0.1f, 0.9f);
        backing.raycastTarget = false;

        var textObject = new GameObject("Text", typeof(RectTransform));
        var textRect = (RectTransform)textObject.transform;

        textRect.SetParent(rect, false);

        // The text component first, its rectangle after. A TextMeshProUGUI added at runtime with no text yet runs
        // TMP_Text.LoadDefaultSettings, which sets the RectTransform's sizeDelta to TextMeshPro's default text box
        // size (TMP_Settings.defaultTextMeshProUITextContainerSize, read in the IL). Set before, the margins were
        // replaced, the text box grew by that size on both sides of the backing, and the right-aligned value
        // ended past the row's edge, cut off by the list (seen in game on 629633f8, 2026-11-13).
        var label = textObject.AddComponent<TextMeshProUGUI>();

        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(OverlayMargin, 0f);
        textRect.offsetMax = new Vector2(-OverlayMargin, 0f);

        label.font = source.font;
        label.fontSize = source.fontSize;
        label.color = source.color;
        label.alignment = TextAlignmentOptions.MidlineRight;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
        label.raycastTarget = false;

        return rect;
    }

    // the editor's category sort row is shown with its list, i.e. while the group is open
    internal static void ShowListSortRow(Transform group, bool shown)
    {
        var row = group ? group.Find(ListSortRowName) : null;

        if (row && row.gameObject.activeSelf != shown)
        {
            row.gameObject.SetActive(shown);
        }
    }

    // The two labels ("Sort:" and "<list>:") are made as wide as the wider of them, so the two dropdowns and the
    // two arrows stand exactly under each other.
    private static void MatchLabels(GuiLabel first, GuiLabel second)
    {
        var a = first ? first.TMP_Text : null;
        var b = second ? second.TMP_Text : null;

        if (!a || !b)
        {
            return;
        }

        var width = Mathf.Ceil(Mathf.Max(a.GetPreferredValues(a.text).x, b.GetPreferredValues(b.text).x));

        foreach (var text in new[] { a, b })
        {
            if (text.TryGetComponent<LayoutElement>(out var layout))
            {
                layout.minWidth = layout.preferredWidth = width;
            }
        }
    }

    // a control of the bar, wherever it is in its rows
    private static Transform FindDeep(Transform parent, string name)
    {
        foreach (Transform child in parent)
        {
            if (child.name == name)
            {
                return child;
            }

            var found = FindDeep(child, name);

            if (found)
            {
                return found;
            }
        }

        return null;
    }

    // The bar: a row of its own above the list. It takes the top strip of the rectangle the list's scroll view
    // had, and the scroll view gives that strip up, so the bar is exactly as wide as the list and nothing else on
    // the screen moves. It holds two copies of one of the screen's buttons and a copy of a text field, and is
    // built under an inactive holder so none of the copies' components start up before they are changed (as the
    // mod's other copied controls are).
    private static Transform AddBar(
        RectTransform list, GameObject sourceButton, bool categoryRow, bool stacked, bool sections)
    {
        var holder = new GameObject("FbHolder");

        holder.SetActive(false);

        var bar = new GameObject(BarName, typeof(RectTransform));
        var rect = (RectTransform)bar.transform;

        rect.SetParent(holder.transform, false);

        var layout = bar.AddComponent<VerticalLayoutGroup>();

        layout.spacing = Spacing;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        // Each row places its controls in the order they are added. The first: the main sort on the left, a gap,
        // then the search field (taking the room that is left) and Clear on the right (user direction, 2026-11-13:
        // "Search should be on the right, and sort on the left"). The second, aligned to the left so its controls
        // stand under the main sort's: the category sort.
        var main = AddRow(bar.transform, MainRowName, TextAnchor.MiddleLeft);

        AddSortLabel(main, sourceButton, SortLabelName);
        AddSortDropdown(main, SortName);
        AddArrow(main, DirectionName);

        // The category order toggle, as wide as its text (FitButtonToText), with the other ordering controls
        // (user direction, 2026-11-13: "Move it to the sort row for the editor, but make it a new row by itself for
        // the palettes"): in the editor after the arrow, on the sort row; in a palette, too narrow for both, on a
        // row of its own under the sort.
        var rows = 1;

        if (sections)
        {
            var orderRow = stacked ? AddRow(bar.transform, CategoryOrderRowName, TextAnchor.MiddleLeft) : main;

            // on the editor's sort row, set apart from the arrow (user, 2026-11-13: "put a space between the sort
            // and the categories button")
            if (!stacked)
            {
                AddGap(main);
            }

            AddButton(orderRow, sourceButton, CategoryOrderName, ClearWidth);
            rows += stacked ? 1 : 0;
        }

        // stacked (a palette): the search and Clear on a row of their own, last
        var searchRow = stacked ? AddRow(bar.transform, SearchRowName, TextAnchor.MiddleLeft) : main;

        rows += stacked ? 1 : 0;

        if (!stacked)
        {
            AddGap(main);
        }

        AddSearch(searchRow);
        AddButton(searchRow, sourceButton, ClearName, ClearWidth);

        if (categoryRow)
        {
            var second = AddRow(bar.transform, CategoryRowName, TextAnchor.MiddleLeft);

            AddSortLabel(second, sourceButton, CategoryLabelName);
            AddSortDropdown(second, CategoryName);
            AddArrow(second, CategoryDirectionName);
        }

        rows += categoryRow ? 1 : 0;

        var height = (rows * ControlHeight) + ((rows - 1) * Spacing);

        // anchored like the scroll view from side to side, and to its top edge, as tall as its rows
        rect.SetParent(list.parent, false);
        rect.SetSiblingIndex(list.GetSiblingIndex() + 1);
        rect.anchorMin = new Vector2(list.anchorMin.x, list.anchorMax.y);
        rect.anchorMax = new Vector2(list.anchorMax.x, list.anchorMax.y);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(list.offsetMin.x, list.offsetMax.y - height);
        rect.offsetMax = new Vector2(list.offsetMax.x, list.offsetMax.y);

        // the scroll view's top edge moves down by the bar and a gap; its bottom stays where it is
        list.offsetMax = new Vector2(list.offsetMax.x, list.offsetMax.y - height - (stacked ? PaletteGap : Gap));

        Object.Destroy(holder);

        return bar.transform;
    }

    // one row of the bar, ControlHeight tall
    private static Transform AddRow(Transform bar, string name, TextAnchor alignment, int indent = 0)
    {
        var row = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));

        row.transform.SetParent(bar, false);

        var layout = row.AddComponent<HorizontalLayoutGroup>();

        layout.spacing = Spacing;
        layout.childAlignment = alignment;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        layout.padding = new RectOffset(indent, 0, 0, 0);

        var size = row.GetComponent<LayoutElement>();

        size.minHeight = size.preferredHeight = ControlHeight;
        size.flexibleHeight = 0f;

        return row.transform;
    }

    private static void AddButton(Transform bar, GameObject source, string name, float width)
    {
        var copy = Object.Instantiate(source, bar, false);

        copy.name = name;

        // the gamepad binding would press the copy together with the original; the setup component re-shows it
        foreach (var binding in copy.GetComponentsInChildren<GamepadBindingButton>(true))
        {
            binding.gameObject.SetActive(false);
            Object.DestroyImmediate(binding);
        }

        foreach (var setup in copy.GetComponentsInChildren<GuiButtonSetup>(true))
        {
            Object.DestroyImmediate(setup);
        }

        // the copied tooltip describes the original button
        foreach (var tooltip in copy.GetComponentsInChildren<GuiTooltip>(true))
        {
            tooltip.Clear();
        }

        // the original's icon, if it has one, means something else
        var icon = copy.transform.Find("Image");

        if (icon)
        {
            icon.gameObject.SetActive(false);
        }

        // the original may be faded or blocked at this moment
        if (copy.TryGetComponent<CanvasGroup>(out var group))
        {
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
        }

        copy.GetComponent<Button>().interactable = true;
        SetSize(copy, width, false);
        copy.SetActive(true);
    }

    // The search field is a copy of the campaign editor's "technical info" field: the game's own text field, with
    // its font, frame, caret and a placeholder text. The editor screen exists from the start of the game, shown
    // or not.
    private static void AddSearch(Transform bar)
    {
        var editor = Gui.GuiService?.GetScreen<UserCampaignEditorScreen>();
        var source = editor ? editor.transform.Find("LeftPanel/TechnicalInfoGroup/TextEditGroup") : null;

        if (!source || !source.GetComponent<TMP_InputField>())
        {
            Problem("Dungeon Maker list tools: the campaign editor's text field was not found, so no search field.");

            return;
        }

        var copy = Object.Instantiate(source.gameObject, bar, false);

        copy.name = SearchName;

        var field = copy.GetComponent<TMP_InputField>();

        // the original's events are wired in the prefab to the campaign's technical info
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.onEndEdit = new TMP_InputField.SubmitEvent();
        field.onSubmit = new TMP_InputField.SubmitEvent();
        field.onSelect = new TMP_InputField.SelectionEvent();
        field.onDeselect = new TMP_InputField.SelectionEvent();
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.characterLimit = 40;
        field.interactable = true;
        field.SetTextWithoutNotify(string.Empty);

        // Vertically centred. The original is a multi-line box: its text area and its placeholder label fill the
        // field with an even inset (6 and 8 units, from the prefab), but the labels keep the box's own alignment,
        // which in a one-line field 34 units tall put "Search..." off the middle (seen in game, 2026-11-13).
        // Setting the alignment here is not enough (tried in 9a8cd276, still off): the placeholder is a game label
        // (GuiLabel), and when a label first wakes up, GuiLabel.Awake runs GuiStyleEnforcer.ApplyStyles, whose
        // label style (GuiLabelStyleDefinition.Apply) sets the alignment again. The copy is made inactive, so it
        // wakes up only when the bar is put on screen, after this. MiddleAlignment sets the middle again once the
        // labels are awake, and each time they are switched on.
        if (field.textComponent)
        {
            field.textComponent.gameObject.AddComponent<MiddleAlignment>();
        }

        if (field.placeholder is TMP_Text)
        {
            field.placeholder.gameObject.AddComponent<MiddleAlignment>();
        }

        // the placeholder is a game label, which writes its own text when it starts up: set it through the label
        var placeholder = field.placeholder ? field.placeholder.GetComponent<GuiLabel>() : null;

        if (placeholder)
        {
            placeholder.Text = Gui.Localize("ModUi/&DungeonMakerSearch");
        }
        else if (field.placeholder is TMP_Text text)
        {
            text.text = Gui.Localize("ModUi/&DungeonMakerSearch");
        }

        SetSize(copy, SearchMinWidth, true);
        copy.SetActive(true);
    }

    // an empty, fixed-width space in the bar's row
    private static void AddGap(Transform bar)
    {
        var gap = new GameObject(GapName, typeof(RectTransform), typeof(LayoutElement));

        gap.transform.SetParent(bar, false);

        var layout = gap.GetComponent<LayoutElement>();

        layout.minWidth = layout.preferredWidth = GroupGap;
        layout.flexibleWidth = 0f;
    }

    // "Sort:" or the category row's "<list>:", in the look of the bar's button text: a copy of the source button's
    // label (a game label, GuiLabel, with its text style), right-aligned against its dropdown. Its width is set by
    // MatchLabels (both labels as wide as the wider text, so a longer word in another language, "Сортировка:",
    // takes the room it needs from the search field), and its text by Ensure.
    private static void AddSortLabel(Transform bar, GameObject sourceButton, string name)
    {
        var source = sourceButton.transform.Find("Label");

        if (!source || !source.GetComponent<GuiLabel>())
        {
            Problem("Dungeon Maker list tools: the button's label was not found, so no \"Sort:\" label.");

            return;
        }

        var copy = Object.Instantiate(source.gameObject, bar, false);

        copy.name = name;

        // the label style sets the alignment when the copy wakes up; MiddleAlignment sets it after that
        copy.AddComponent<MiddleAlignment>().Alignment = TextAlignmentOptions.MidlineRight;

        // the copied tooltip, if any, describes the original button
        foreach (var tooltip in copy.GetComponentsInChildren<GuiTooltip>(true))
        {
            tooltip.Clear();
        }

        if (!copy.TryGetComponent<LayoutElement>(out var layout))
        {
            layout = copy.AddComponent<LayoutElement>();
        }

        // until MatchLabels sets the width: the text's own preferred width
        layout.ignoreLayout = false;
        layout.minWidth = layout.preferredWidth = -1f;
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;
        copy.SetActive(true);
    }

    // A sort dropdown: the game's own dropdown (the prefab the mod's other dropdowns use, CustomDropDown). Its
    // choices are filled in by Ensure.
    private static void AddSortDropdown(Transform bar, string name)
    {
        var dropdown = CustomDropDown.MakeDropdown(name, bar);

        if (!dropdown)
        {
            Problem("Dungeon Maker list tools: the game's dropdown was not found, so no sort choice.");

            return;
        }

        SetSize(dropdown.gameObject, SortWidth, false);
        dropdown.gameObject.SetActive(true);
    }

    // The direction arrow: a copy of the campaign editor's "move up" button (the arrows beside Edit), whose arrow
    // picture is switched to the "move down" button's for descending. The editor screen exists from the start of
    // the game, shown or not.
    private static void AddArrow(Transform bar, string name)
    {
        var editor = Gui.GuiService?.GetScreen<UserCampaignEditorScreen>();
        var up = editor ? editor.moveQuestUpButton : null;
        var down = editor ? editor.moveQuestDownButton : null;

        _upArrow = ArrowOf(up)?.sprite;
        _downArrow = ArrowOf(down)?.sprite;

        if (!up || !_upArrow || !_downArrow)
        {
            Problem("Dungeon Maker list tools: the editor's move buttons were not found, so no direction arrow.");

            return;
        }

        var copy = Object.Instantiate(up.gameObject, bar, false);

        copy.name = name;

        // the copied tooltip describes moving a quest; SetArrow writes its own
        foreach (var tooltip in copy.GetComponentsInChildren<GuiTooltip>(true))
        {
            tooltip.Clear();
        }

        copy.GetComponent<Button>().interactable = true;

        if (!copy.TryGetComponent<LayoutElement>(out var layout))
        {
            layout = copy.AddComponent<LayoutElement>();
        }

        // square, as tall as the bar's other controls
        layout.ignoreLayout = false;
        layout.minWidth = layout.preferredWidth = ControlHeight;
        layout.minHeight = layout.preferredHeight = ControlHeight;
        layout.flexibleWidth = 0f;
        layout.flexibleHeight = 0f;
        copy.SetActive(true);
    }

    // the arrow picture of one of the editor's move buttons: its image other than the frame
    private static Image ArrowOf(Button button)
    {
        return button
            ? button.GetComponentsInChildren<Image>(true)
                .FirstOrDefault(x => x.gameObject != button.gameObject && x.name != "Frame")
            : null;
    }

    private static void SetArrow(Button button, bool descending)
    {
        if (!button)
        {
            return;
        }

        var arrow = ArrowOf(button);

        if (arrow)
        {
            arrow.sprite = descending ? _downArrow : _upArrow;
        }

        var tooltip = button.GetComponentInChildren<GuiTooltip>(true);

        if (tooltip)
        {
            tooltip.Content = Gui.Localize(
                descending ? "ModUi/&DungeonMakerSortDescending" : "ModUi/&DungeonMakerSortAscending");
        }
    }

    // a fixed width, or at least that width and all the room that is left
    private static void SetSize(GameObject control, float width, bool stretch)
    {
        if (!control.TryGetComponent<LayoutElement>(out var layout))
        {
            layout = control.AddComponent<LayoutElement>();
        }

        layout.ignoreLayout = false;
        layout.minWidth = layout.preferredWidth = width;
        layout.minHeight = layout.preferredHeight = ControlHeight;
        layout.flexibleWidth = stretch ? 1f : 0f;
        layout.flexibleHeight = 0f;
    }

    private static void SetText(Button button, string term)
    {
        var label = button ? button.transform.Find("Label")?.GetComponent<GuiLabel>() : null;

        if (label)
        {
            label.Text = Gui.Localize(term);
        }
    }

    private static void Problem(string message)
    {
        if (message == _lastProblem)
        {
            return;
        }

        _lastProblem = message;
        Main.Error(message);
    }

    #endregion

    private sealed class Row
    {
        internal SortKey Category;
        internal int Index;
        internal string Name;
        internal string Second;
        internal Transform Transform;
    }
}

// A row's value for a category sort key: a number, a text, or nothing. Numbers compare as numbers, texts without
// regard to case. A group, if given, compares first, so values of different kinds (a variable's Int and String
// starting values) are kept apart, like with like. An entry with no value comes last whichever the direction.
internal readonly struct SortKey
{
    private readonly bool _flag;
    private readonly bool _has;
    private readonly int _group;
    private readonly double? _number;
    private readonly string _text;

    private SortKey(int group, double? number, string text, bool flag = false)
    {
        _has = true;
        _group = group;
        _number = number;
        _text = text;
        _flag = flag;
    }

    // the value as the overlay shows it
    internal string Display()
    {
        if (!_has)
        {
            return "-";
        }

        if (_flag)
        {
            return Gui.Localize(_number > 0 ? "ModUi/&DungeonMakerCategoryYes" : "ModUi/&DungeonMakerCategoryNo");
        }

        return _number.HasValue
            ? _number.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
            : _text ?? string.Empty;
    }

    internal static SortKey Missing => default;

    internal static SortKey Of(double number)
    {
        return new SortKey(0, number, null);
    }

    internal static SortKey Of(bool value)
    {
        return Flag(0, value);
    }

    // a yes/no value: compares as 0 and 1, shown as Yes or No
    internal static SortKey Flag(int group, bool value)
    {
        return new SortKey(group, value ? 1 : 0, null, true);
    }

    internal static SortKey Of(string text)
    {
        return string.IsNullOrEmpty(text) ? Missing : new SortKey(0, null, text);
    }

    internal static SortKey Grouped(int group, double? number, string text)
    {
        return new SortKey(group, number, text);
    }

    internal static int Compare(SortKey a, SortKey b, bool descending)
    {
        if (a._has != b._has)
        {
            return a._has ? -1 : 1;
        }

        if (!a._has)
        {
            return 0;
        }

        var result = a._group.CompareTo(b._group);

        if (result == 0)
        {
            result = Nullable.Compare(a._number, b._number);
        }

        if (result == 0)
        {
            result = StringComparer.OrdinalIgnoreCase.Compare(a._text ?? string.Empty, b._text ?? string.Empty);
        }

        return descending ? -result : result;
    }
}

// One key a list can be sorted by in its category row: a stable id (saved in the settings), its dropdown text,
// how to read it from a row, and the text of the row's value overlay (null when the rows show the field already).
internal sealed class CategoryOption(
    string id, string term, Func<Transform, SortKey> key, Func<Transform, string> display)
{
    internal readonly Func<Transform, string> Display = display;
    internal readonly string Id = id;
    internal readonly Func<Transform, SortKey> Key = key;
    internal readonly string Term = term;
}

// The list a screen's category row applies to: its id (the settings key), its shown name and its options.
internal sealed class CategoryView(string listId, string title, IReadOnlyList<CategoryOption> options)
{
    internal readonly string ListId = listId;
    internal readonly IReadOnlyList<CategoryOption> Options = options;
    internal readonly string Title = title;
}

// Keeps a copied text label vertically centred (the search field's text and its "Search..." hint). A game label's
// style sets its alignment when the label wakes up (GuiLabel.Awake, GuiStyleEnforcer.ApplyStyles), which is after
// the copy is made. Unity wakes the components of an object in order, each with its OnEnable, and this one is added
// after the label's, so it comes after the style; Start, after every object's Awake, is the second chance.
internal sealed class MiddleAlignment : MonoBehaviour
{
    // set right after the component is added, before the label wakes up
    internal TextAlignmentOptions Alignment = TextAlignmentOptions.MidlineLeft;

    private void Start()
    {
        Apply();
    }

    private void OnEnable()
    {
        Apply();
    }

    private void Apply()
    {
        if (TryGetComponent<TMP_Text>(out var text))
        {
            text.alignment = Alignment;
        }
    }
}
