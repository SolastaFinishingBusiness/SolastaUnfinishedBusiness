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
// Cheats > Campaign Variables panel. Each of the three screens gets a small bar with
//
//   a sort button      what the list is ordered by: the campaign's own order, the name, or a second key
//                      (the title for content, the type for variables); a click goes to the next one
//   a direction button ascending or descending
//   a search field     only rows whose name or title contains the text are shown
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
//     act on the item that was clicked, and the selection survives every change of the view.
//   - On its next refresh the game binds row object i to item i again, whichever object that now is, and switches
//     it on; the view is then applied again.
//
// Names compare without regard to case; rows that compare equal keep the list's order.
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

    // the bar's controls, in the units of the game's 1920 x 1080 interface
    private const float ButtonWidth = 150f;
    private const float SearchWidth = 220f;
    private const float ControlHeight = 34f;
    private const float Spacing = 6f;

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

    // The game has just filled this table: remember its bound rows in the order they are in now.
    internal static void Bound<TLine>(Transform table, Func<TLine, bool> isBound) where TLine : Component
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

            if (child.TryGetComponent<TLine>(out var line) && isBound(line))
            {
                rows.Add(child);
            }
        }
    }

    // Orders and filters the rows remembered for this table. name and second give a row's two sort keys; a row
    // is shown when its name or its second key contains the screen's search text.
    internal static void Apply<TLine>(
        Transform table, string screen, Func<TLine, string> name, Func<TLine, string> second) where TLine : Component
    {
        if (!table || !BoundRows.TryGetValue(table, out var rows) || rows.Count == 0)
        {
            return;
        }

        var search = Search(screen);
        var items = new List<Row>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];

            // a row the game has taken away since (released to its pool)
            if (!row || row.parent != table || !row.TryGetComponent<TLine>(out var line))
            {
                continue;
            }

            items.Add(new Row
            {
                Transform = row, Index = i, Name = name(line) ?? string.Empty, Second = second(line) ?? string.Empty
            });
        }

        // OrderBy is stable, and the rows start in list order, so equal keys keep that order
        IEnumerable<Row> ordered = Kind switch
        {
            Sort.Name => items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            Sort.Second => items
                .OrderBy(x => x.Second, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            _ => items
        };

        var sorted = ordered.ToList();

        if (Descending)
        {
            sorted.Reverse();
        }

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
        }
    }

    #endregion

    #region The bar

    // Adds the bar to a screen if it isn't there yet and brings it up to date. Called each time the screen is
    // shown: the sort may have been changed on another screen, and the search starts empty.
    //   screen      the key of this screen's search text
    //   sourcePath  a button of the screen to copy, from the screen's root; the bar goes first in that button's row
    //   secondTerm  the text of the sort button for the second key ("Sort: title", "Sort: type")
    //   changed     the view changed: apply it to the screen's tables
    internal static void Ensure(Transform root, string screen, string sourcePath, string secondTerm, Action changed)
    {
        Searches[screen] = string.Empty;

        var source = root ? root.Find(sourcePath) : null;

        if (!source || !source.GetComponent<Button>())
        {
            // a game update that moved the button: say so once, and leave the screen as it is
            Problem($"Dungeon Maker list tools: no button at '{sourcePath}' on {(root ? root.name : "?")}.");

            return;
        }

        var row = source.parent;
        var bar = row.Find(BarName);

        if (!bar)
        {
            bar = AddBar(row, source.gameObject);
        }

        var sort = bar.Find(SortName)?.GetComponent<Button>();
        var direction = bar.Find(DirectionName)?.GetComponent<Button>();
        var search = bar.Find(SearchName)?.GetComponent<TMP_InputField>();

        void Refresh()
        {
            SetText(sort, Kind switch
            {
                Sort.Name => "ModUi/&DungeonMakerSortName",
                Sort.Second => secondTerm,
                _ => "ModUi/&DungeonMakerSortDefault"
            });
            SetText(direction, Descending ? "ModUi/&DungeonMakerSortDescending" : "ModUi/&DungeonMakerSortAscending");
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

        Refresh();
    }

    // The bar: a row of its own, first in the row of the button it was made from, holding two copies of that button
    // and a copy of a text field. Built under an inactive holder so none of the copies' components start up before
    // they are changed (as the mod's other copied controls are).
    private static Transform AddBar(Transform row, GameObject sourceButton)
    {
        var holder = new GameObject("FbHolder");

        holder.SetActive(false);

        var bar = new GameObject(BarName, typeof(RectTransform));

        bar.transform.SetParent(holder.transform, false);

        var layout = bar.AddComponent<HorizontalLayoutGroup>();

        layout.spacing = Spacing;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;

        // the row the bar sits in is a layout group too: tell it how much room the bar needs, and no more
        var size = bar.AddComponent<LayoutElement>();

        size.minWidth = size.preferredWidth = (2 * ButtonWidth) + SearchWidth + (2 * Spacing);
        size.minHeight = size.preferredHeight = ControlHeight;
        size.flexibleWidth = 0f;

        AddButton(bar.transform, sourceButton, SortName);
        AddButton(bar.transform, sourceButton, DirectionName);
        AddSearch(bar.transform);

        bar.transform.SetParent(row, false);
        bar.transform.SetAsFirstSibling();
        Object.Destroy(holder);

        return bar.transform;
    }

    private static void AddButton(Transform bar, GameObject source, string name)
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
        SetSize(copy, ButtonWidth);
        copy.SetActive(true);
    }

    // The search field is a copy of the campaign editor's title field: the game's own text field, with its font,
    // frame and caret. The editor screen exists from the start of the game, shown or not.
    private static void AddSearch(Transform bar)
    {
        var editor = Gui.GuiService?.GetScreen<UserCampaignEditorScreen>();
        var source = editor ? editor.transform.Find("LeftPanel/TitleGroup/TitleInputField") : null;

        if (!source || !source.GetComponent<TMP_InputField>())
        {
            Problem("Dungeon Maker list tools: the campaign editor's title field was not found, so no search field.");

            return;
        }

        var copy = Object.Instantiate(source.gameObject, bar, false);

        copy.name = SearchName;

        var field = copy.GetComponent<TMP_InputField>();

        // the original's events are wired in the prefab to the campaign's title
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.onEndEdit = new TMP_InputField.SubmitEvent();
        field.onSubmit = new TMP_InputField.SubmitEvent();
        field.onSelect = new TMP_InputField.SelectionEvent();
        field.onDeselect = new TMP_InputField.SelectionEvent();
        field.lineType = TMP_InputField.LineType.SingleLine;
        field.characterLimit = 40;
        field.interactable = true;
        field.SetTextWithoutNotify(string.Empty);

        if (field.placeholder is TMP_Text placeholder)
        {
            placeholder.text = Gui.Localize("ModUi/&DungeonMakerSearch");
            placeholder.gameObject.SetActive(true);
        }

        SetSize(copy, SearchWidth);
        copy.SetActive(true);
    }

    private static void SetSize(GameObject control, float width)
    {
        if (!control.TryGetComponent<LayoutElement>(out var layout))
        {
            layout = control.AddComponent<LayoutElement>();
        }

        layout.ignoreLayout = false;
        layout.minWidth = layout.preferredWidth = width;
        layout.minHeight = layout.preferredHeight = ControlHeight;
        layout.flexibleWidth = 0f;
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
        internal int Index;
        internal string Name;
        internal string Second;
        internal Transform Transform;
    }
}
