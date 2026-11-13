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
// Cheats > Campaign Variables panel. Each of the three screens gets a bar on a row of its own, under its list and
// as wide as it, with
//
//   a sort button      what the list is ordered by: the campaign's own order, the name, or a second key
//                      (the title for content, the type for variables); a click goes to the next one
//   a direction button ascending or descending
//   a search field     only rows whose name or title contains the text are shown
//   a clear button     empties the search field
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

    // the bar's controls, in the units of the game's 1920 x 1080 interface; the search field takes what the two
    // buttons leave of the bar's width
    private const float ButtonWidth = 150f;
    private const float SearchMinWidth = 160f;
    private const float ClearWidth = 90f;
    private const float ControlHeight = 34f;
    private const float Spacing = 6f;

    // between the list and the bar
    private const float Gap = 6f;

    // the rows of each table in the order the game bound them, i.e. the order of the list
    private static readonly ConditionalWeakTable<Transform, List<Transform>> BoundRows = new();

    // the search text of each screen, by the key the screen passes
    private static readonly Dictionary<string, string> Searches = [];

    private static string _lastProblem = string.Empty;

    internal static Sort Kind => (Sort)Mathf.Clamp(Main.Settings.DungeonMakerListSort, 0, 2);

    private static bool Descending => Main.Settings.DungeonMakerListSortDescending;

    internal static string Search(string screen)
    {
        return Searches.TryGetValue(screen, out var text) ? text : string.Empty;
    }

    // the list is shown exactly as the game shows it: its own order, top to bottom, nothing left out
    internal static bool IsPlain(string screen)
    {
        return Kind == Sort.Default && !Descending && Search(screen).Length == 0;
    }

    #region The view

    // The game has just filled this table: remember its bound rows in the order they are in now. isBound says
    // whether a child of the table is a row that holds an item (the screens keep unused pooled rows there too).
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
    // is shown when its name or its second key contains the screen's search text. Returns true when the search
    // left out a row that isSelected says is the selected one: the screen still holds that selection, and its
    // buttons (edit, delete, validate) would act on an item that can't be seen. Whether to clear it is the
    // caller's decision.
    internal static bool Apply(
        Transform table, string screen, Func<Transform, string> name, Func<Transform, string> second,
        Func<Transform, bool> isSelected = null)
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
                Transform = row, Name = name(row) ?? string.Empty, Second = second(row) ?? string.Empty
            });
        }

        // OrderBy and OrderByDescending are stable, and the rows start in list order, so rows with equal keys
        // keep that order in both directions. (Reversing an ascending result would turn the ties round too.)
        // Only the campaign's own order has no keys: descending, it is simply the list from its end.
        var descending = Descending;
        IEnumerable<Row> ordered = Kind switch
        {
            Sort.Name => descending
                ? items.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            Sort.Second => descending
                ? items
                    .OrderByDescending(x => x.Second, StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                : items
                    .OrderBy(x => x.Second, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => descending ? Enumerable.Reverse(items) : items
        };

        var sorted = ordered.ToList();

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
    //   secondTerm  the text of the sort button for the second key ("Sort: title", "Sort: type")
    //   changed     the view changed: apply it to the screen's tables
    internal static void Ensure(
        Transform root, string screen, string listPath, string sourcePath, string secondTerm, Action changed)
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

        var sort = bar.Find(SortName)?.GetComponent<Button>();
        var direction = bar.Find(DirectionName)?.GetComponent<Button>();
        var search = bar.Find(SearchName)?.GetComponent<TMP_InputField>();
        var clear = bar.Find(ClearName)?.GetComponent<Button>();

        void Refresh()
        {
            SetText(sort, Kind switch
            {
                Sort.Name => "ModUi/&DungeonMakerSortName",
                Sort.Second => secondTerm,
                _ => "ModUi/&DungeonMakerSortDefault"
            });
            SetText(direction, Descending ? "ModUi/&DungeonMakerSortDescending" : "ModUi/&DungeonMakerSortAscending");
            SetText(clear, "ModUi/&DungeonMakerSearchClear");

            // Nothing to clear while the field is empty. The field's own text decides, not the search made from
            // it: spaces alone are no search, but they are in the field and Clear must be able to remove them.
            if (clear)
            {
                clear.interactable = search && !string.IsNullOrEmpty(search.text);
            }
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
            sort.onClick = new Button.ButtonClickedEvent();
            sort.onClick.AddListener(() =>
            {
                Main.Settings.DungeonMakerListSort = ((int)Kind + 1) % 3;
                Changed();
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

        Refresh();
    }

    // The bar: a row of its own under the list. It takes the bottom strip of the rectangle the list's scroll view
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

        var layout = bar.AddComponent<HorizontalLayoutGroup>();

        layout.spacing = Spacing;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        AddButton(bar.transform, sourceButton, SortName, ButtonWidth);
        AddButton(bar.transform, sourceButton, DirectionName, ButtonWidth);
        AddSearch(bar.transform);
        AddButton(bar.transform, sourceButton, ClearName, ClearWidth);

        // anchored like the scroll view from side to side, and to its bottom edge, ControlHeight tall
        rect.SetParent(list.parent, false);
        rect.SetSiblingIndex(list.GetSiblingIndex() + 1);
        rect.anchorMin = new Vector2(list.anchorMin.x, list.anchorMin.y);
        rect.anchorMax = new Vector2(list.anchorMax.x, list.anchorMin.y);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.offsetMin = new Vector2(list.offsetMin.x, list.offsetMin.y);
        rect.offsetMax = new Vector2(list.offsetMax.x, list.offsetMin.y + ControlHeight);

        // the scroll view's bottom edge moves up by the bar and a gap; its top stays where it is
        list.offsetMin = new Vector2(list.offsetMin.x, list.offsetMin.y + ControlHeight + Gap);

        Object.Destroy(holder);

        return bar.transform;
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
        internal string Name;
        internal string Second;
        internal Transform Transform;
    }
}
