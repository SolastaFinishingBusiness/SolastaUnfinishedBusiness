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
// Dungeon Maker tools): the twelve content lists of the campaign editor, the variable picker, and the
// Cheats > Campaign Variables panel. Each of the three screens gets a bar on a row of its own, above its list and
// as wide as it, with, from left to right (user direction, 2026-11-13: the bar "should be at the very top", and
// "the sort settings should also be on the right of the clear"),
//
//   a search field     only rows whose name or title contains the text are shown
//   a clear button     empties the search field
//   "Sort:" and a      what the list is ordered by: the campaign's own order, the name, or a second key
//   dropdown           (the title for content, the type for variables), set apart from Clear by a gap
//   a direction arrow  up for ascending, down for descending (a copy of the editor's move-up button)
//
// and, right below the sort controls, a second row of the same kind for the CATEGORY SORT (user direction through
// Codex, 2026-11-13: "The sort options should use the same style as the main sort, but be located right below the
// dropdown. The category specific sort will be priority, with the main sort as the secondary."):
//
//   "<list>:"          the list it applies to ("Monsters:")
//   a dropdown         None, or one of that list's own keys (hit points, challenge rating, cost, ...)
//   a direction arrow  its own direction
//
// A list is ordered by its category key first, and rows with equal keys by the main sort (with its direction),
// then by the campaign's order. Each list keeps its own category sort (user's choice, 2026-11-13: "A sort per
// list"); in the campaign editor the row shows the list that was opened last, and every open list is ordered by its
// own. Numbers compare as numbers; an entry without a value for the key comes last in both directions.
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

    // two rows: the search and main sort, and the category sort under it
    private const float BarHeight = (2 * ControlHeight) + Spacing;

    // between the list and the bar
    private const float Gap = 6f;

    // the rows of each table in the order the game bound them, i.e. the order of the list
    private static readonly ConditionalWeakTable<Transform, List<Transform>> BoundRows = new();

    // the search text of each screen, by the key the screen passes
    private static readonly Dictionary<string, string> Searches = [];

    private static string _lastProblem = string.Empty;

    // each screen's way to bring its bar up to date (the editor's when another list is opened)
    private static readonly Dictionary<string, Action> Refreshers = [];

    // the editor's two arrow pictures, for the direction button
    private static Sprite _upArrow;
    private static Sprite _downArrow;

    internal static Sort Kind => (Sort)Mathf.Clamp(Main.Settings.DungeonMakerListSort, 0, 2);

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

    // Brings a screen's bar up to date, e.g. the category row after another list was opened in the editor.
    internal static void RefreshBar(string screen)
    {
        if (Refreshers.TryGetValue(screen, out var refresh))
        {
            refresh();
        }
    }

    // A list's category sort: the option's id (null for None) and whether it is descending.
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
        bool categoryDescending = false)
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

            hidSelected |= !shown && isSelected != null && isSelected(row.Transform);
        }

        return hidSelected;
    }

    #endregion

    #region The bar

    // Adds the bar to a screen if it isn't there yet and brings it up to date. Called each time the screen is
    // shown: the sort may have been changed on another screen, and the search starts empty.
    //   screen      the key of this screen's search text
    //   listPath    the scroll view that holds the list, from the screen's root; the bar takes a strip at its foot
    //   sourcePath  a button of the screen to copy for the bar's two buttons, from the screen's root
    //   secondTerm  the sort dropdown's choice for the second key ("Title", "Type")
    //   changed     the view changed: apply it to the screen's tables
    //   category    the list the category row applies to now, with its options, or null when there is none
    internal static void Ensure(
        Transform root, string screen, string listPath, string sourcePath, string secondTerm, Action changed,
        Func<CategoryView> category)
    {
        Searches[screen] = string.Empty;

        var list = root ? root.Find(listPath) as RectTransform : null;
        var source = root ? root.Find(sourcePath) : null;

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
            bar = AddBar(list, source.gameObject);
        }

        var sort = FindDeep(bar, SortName)?.GetComponent<GuiDropdown>();
        var direction = FindDeep(bar, DirectionName)?.GetComponent<Button>();
        var search = FindDeep(bar, SearchName)?.GetComponent<TMP_InputField>();
        var clear = FindDeep(bar, ClearName)?.GetComponent<Button>();
        var sortLabel = FindDeep(bar, SortLabelName)?.GetComponent<GuiLabel>();
        var categoryLabel = FindDeep(bar, CategoryLabelName)?.GetComponent<GuiLabel>();
        var categoryDropdown = FindDeep(bar, CategoryName)?.GetComponent<GuiDropdown>();
        var categoryDirection = FindDeep(bar, CategoryDirectionName)?.GetComponent<Button>();

        // the list the category row shows now; its dropdown's choices are that list's options after None
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
            search.SetTextWithoutNotify(string.Empty);
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

        Refreshers[screen] = Refresh;
        Refresh();
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
    private static Transform AddBar(RectTransform list, GameObject sourceButton)
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

        // Each row places its controls in the order they are added. The first: search and clear, a gap, then the
        // main sort. The second, aligned to the right so its controls stand under the main sort's: the category
        // sort.
        var main = AddRow(bar.transform, MainRowName, TextAnchor.MiddleLeft);

        AddSearch(main);
        AddButton(main, sourceButton, ClearName, ClearWidth);
        AddGap(main);
        AddSortLabel(main, sourceButton, SortLabelName);
        AddSortDropdown(main, SortName);
        AddArrow(main, DirectionName);

        var second = AddRow(bar.transform, CategoryRowName, TextAnchor.MiddleRight);

        AddSortLabel(second, sourceButton, CategoryLabelName);
        AddSortDropdown(second, CategoryName);
        AddArrow(second, CategoryDirectionName);

        // anchored like the scroll view from side to side, and to its top edge, BarHeight tall
        rect.SetParent(list.parent, false);
        rect.SetSiblingIndex(list.GetSiblingIndex() + 1);
        rect.anchorMin = new Vector2(list.anchorMin.x, list.anchorMax.y);
        rect.anchorMax = new Vector2(list.anchorMax.x, list.anchorMax.y);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(list.offsetMin.x, list.offsetMax.y - BarHeight);
        rect.offsetMax = new Vector2(list.offsetMax.x, list.offsetMax.y);

        // the scroll view's top edge moves down by the bar and a gap; its bottom stays where it is
        list.offsetMax = new Vector2(list.offsetMax.x, list.offsetMax.y - BarHeight - Gap);

        Object.Destroy(holder);

        return bar.transform;
    }

    // one row of the bar, ControlHeight tall
    private static Transform AddRow(Transform bar, string name, TextAnchor alignment)
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
    private readonly bool _has;
    private readonly int _group;
    private readonly double? _number;
    private readonly string _text;

    private SortKey(int group, double? number, string text)
    {
        _has = true;
        _group = group;
        _number = number;
        _text = text;
    }

    internal static SortKey Missing => default;

    internal static SortKey Of(double number)
    {
        return new SortKey(0, number, null);
    }

    internal static SortKey Of(bool value)
    {
        return new SortKey(0, value ? 1 : 0, null);
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

// One key a list can be sorted by in its category row: a stable id (saved in the settings), its dropdown text
// and how to read it from a row.
internal sealed class CategoryOption(string id, string term, Func<Transform, SortKey> key)
{
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
