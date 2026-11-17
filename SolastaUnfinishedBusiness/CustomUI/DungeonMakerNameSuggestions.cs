using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

// The list of gadget names offered while the text of a command variable is typed in the Dungeon Maker, to pick
// one from (issue #42).
//
// WHERE: the text value of a Variable Operation that writes Dialog_Trigger, Roll_Trigger, Teleport_Node or
// Teleport_PartyNode: a gadget's parameter in the location editor (the names of that location) and an action's
// parameter in the dialogue and quest editors (the names of every location). Which names, and in which order, is
// DungeonMakerNamesContext.Suggest's business. The fields and their hints are DungeonMakerNameHints'; this file is only
// the list.
//
// THE LIST is a panel of the mod's own (the hint's panel pieces), with one row per name, at most MaxRows, placed
// like a hint. It is shown instead of the field's hint, never with it (DungeonMakerFieldHint.Refresh).
//
// CHOOSING WITH THE MOUSE, and the focus: the list and its field share the focus. A click outside a text field takes
// the focus from it: the game's input module (InputPointerModule, a StandaloneInputModule) deselects the selected
// object when the pointer goes down on something else, BEFORE it sends that something the pointer-down
// (ProcessMousePress calls PointerInputModule.DeselectIfSelectionChanged first; read in UnityEngine.UI's IL). Losing
// the focus ends the field's editing, and the game's fields save their text on that (the prefab wires
// TMP_InputField.onEndEdit to UserParameterItem.OnStringValueChangedCb); saving makes the editor rebuild the action's
// parameter rows (UserFunctorDescriptionItem.FunctorParameterModified -> Refresh -> ClearParameters, which deactivates
// every row), which would take the list down with its field before the row could get the click. So for a press on this
// list the deselect is skipped altogether (KeepsFocus, a prefix on DeselectIfSelectionChanged): the field keeps the
// focus and its editing, the row gets the pointer-down and puts its name in, exactly as typing would.
//
// CHOOSING WITH THE KEYS, only while the list is open. TMP_InputField handles its keys in KeyPressed, one call per
// key-down event. A prefix there (DungeonMakerCommandsPatcher) hands the event to HandleKey first, and only for the one
// field whose list is open: Up and Down move the highlight, Enter and Tab put the highlighted name in, Escape closes
// the list. A key used here is turned into an empty one before the field sees it (no key code and character 0, which
// both single-line and multi-line fields ignore), so Enter doesn't also end the editing or start a new line and the
// arrows don't move the caret. With nothing highlighted, Enter and Tab are the field's own, and with the list closed
// every key is.
internal static class DungeonMakerNameSuggestions
{
    internal const int MaxRows = 8;

    private const string PanelName = "FbNameSuggestions";
    private const float MinWidth = 220f;
    private const float MaxWidth = 520f;
    private const float PaddingX = 10f;
    private const float PaddingY = 6f;
    private const float RowPaddingX = 8f;
    private const float RowPaddingY = 3f;

    private static readonly Color RowColor = new(1f, 1f, 1f, 0f);
    private static readonly Color HighlightColor = new(1f, 1f, 1f, 0.16f);

    private static RectTransform _rect;
    private static Color _textColor = Color.white;
    private static readonly List<Row> Rows = [];
    private static readonly List<string> Names = [];
    private static DungeonMakerFieldHint _owner;

    // the highlighted row, by the mouse or the keys; -1 for none
    private static int _selected = -1;

    // Open, and not switched off. The setting is read here too, not only in Show:
    // switched off in the mod menu while a list is up, the list would otherwise keep taking the arrow keys, Enter,
    // Tab and Escape until the field refreshes. So the keys, the focus rule and the hover rule drop it at once, and
    // the Follower takes it off the screen on the next frame.
    private static bool IsOpen => _rect && _rect.gameObject.activeSelf && _owner &&
                                  Main.Settings.ShowDungeonMakerNameSuggestions;

    // the frame in which Escape closed the list (HandleKey), or -1
    private static int _escapeFrame = -1;

    // the frame in which Enter or Tab picked a name, and the field it went into (HandleKey)
    private static int _pickedFrame = -1;
    private static TMP_InputField _pickedField;

    // The game also turns Escape into its Cancel command, which reaches the editor on another path than the text
    // field's key event: the location editor answers it by deselecting the selected gadget, whether a text field has
    // the focus or not. Escape belongs to the list while it is open, so the editors leave Cancel alone then, and in
    // the frame the list's Escape closed it (the two paths run in the same frame, in no order this code relies on).
    internal static bool TakesCancel()
    {
        return IsOpen || _escapeFrame == Time.frameCount;
    }

    // Shows these names for this field, replacing whatever the list showed.
    internal static void Show(DungeonMakerFieldHint owner, List<string> names)
    {
        var field = owner ? owner.Field : null;
        var target = field ? field.transform as RectTransform : null;
        var nearest = target ? target.GetComponentInParent<Canvas>() : null;
        var canvas = nearest ? nearest.rootCanvas : null;

        // switched off in Mod UI > Dungeon Maker: the list never opens, so the keys stay the field's own
        if (!Main.Settings.ShowDungeonMakerNameSuggestions || !canvas || names == null || names.Count == 0)
        {
            Hide(owner);

            return;
        }

        EnsurePanel(canvas.transform, field.textComponent);

        // the same names as before keep their highlight (the list is worked out again at every key)
        var same = ReferenceEquals(_owner, owner) && Names.Count == names.Count;

        for (var i = 0; same && i < names.Count; i++)
        {
            same = Names[i] == names[i];
        }

        _owner = owner;
        Names.Clear();
        Names.AddRange(names.Count > MaxRows ? names.GetRange(0, MaxRows) : names);

        if (!same)
        {
            _selected = -1;
        }

        // as wide as the longest name needs, at least as wide as the field, up to MaxWidth
        var width = Mathf.Max(MinWidth, target.rect.width - (2 * PaddingX));
        var rowHeight = 0f;

        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            var used = i < Names.Count;

            row.gameObject.SetActive(used);

            if (!used)
            {
                continue;
            }

            row.Label.text = Names[i];

            var wanted = row.Label.GetPreferredValues(Names[i]);

            width = Mathf.Max(width, wanted.x + (2 * RowPaddingX));
            rowHeight = Mathf.Max(rowHeight, wanted.y + (2 * RowPaddingY));
        }

        width = Mathf.Min(width, MaxWidth);

        for (var i = 0; i < Names.Count; i++)
        {
            var rect = (RectTransform)Rows[i].transform;

            rect.sizeDelta = new Vector2(width, rowHeight);
            rect.anchoredPosition = new Vector2(0f, -PaddingY - (i * rowHeight));
            Rows[i].Label.rectTransform.sizeDelta = new Vector2(width - (2 * RowPaddingX), rowHeight);
        }

        _rect.sizeDelta = new Vector2(width + (2 * PaddingX), (Names.Count * rowHeight) + (2 * PaddingY));
        _rect.gameObject.SetActive(true);
        _rect.SetAsLastSibling();
        Highlight();
        DungeonMakerNameHints.Place(_rect, target, owner.Below);
    }

    internal static void Hide(DungeonMakerFieldHint owner)
    {
        if (_rect && ReferenceEquals(_owner, owner))
        {
            _owner = null;
            _selected = -1;
            _rect.gameObject.SetActive(false);
        }
    }

    // PointerInputModule.DeselectIfSelectionChanged, before it runs: false (skip the deselect) when the pointer is
    // going down on the open list, so the field keeps the focus (see the top of the file). Any other press is
    // left to the game.
    internal static bool KeepsFocus(GameObject pressed)
    {
        return !(IsOpen && pressed && pressed.transform.IsChildOf(_rect));
    }

    // Is the mouse pointer on this field's open list?
    internal static bool PointerOver(DungeonMakerFieldHint owner)
    {
        if (!IsOpen || !ReferenceEquals(_owner, owner))
        {
            return false;
        }

        var canvas = _rect.GetComponentInParent<Canvas>();
        var root = canvas ? canvas.rootCanvas : null;
        var camera = root && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;

        return RectTransformUtility.RectangleContainsScreenPoint(_rect, Input.mousePosition, camera);
    }

    // Is the mouse pointer on the open list, whichever field it belongs to? (Cursor_RefreshHover: the editor's map
    // must not take the wheel or a click meant for the list)
    internal static bool PointerOverOpenList()
    {
        return IsOpen && PointerOver(_owner);
    }

    // TMP_InputField.KeyPressed, before it runs, for every text field of the game: true when this key was used
    // for the list, and the field must not act on it. False at once for any field but the one whose list is open.
    // The location editor's value field is multi-line, so multi-line fields are handled too.
    internal static bool HandleKey(TMP_InputField field, Event key)
    {
        // Unity sends a pressed Enter or Tab as two key-down events in the same frame: the key itself, then one with
        // no key code that carries its character ('\n' or '\t'). The first picks the name and closes the list, so
        // the second would reach the field with the list closed and put a new line or a tab after the name in a
        // multi-line field (a single-line one drops both). It is taken here too.
        if (key is { keyCode: KeyCode.None, character: '\n' or '\r' or '\t' } &&
            _pickedFrame == Time.frameCount && ReferenceEquals(_pickedField, field))
        {
            return true;
        }

        if (!IsOpen || key == null || !ReferenceEquals(_owner.Field, field) || Names.Count == 0)
        {
            return false;
        }

        switch (key.keyCode)
        {
            case KeyCode.DownArrow:
                _selected = _selected + 1 >= Names.Count ? 0 : _selected + 1;
                Highlight();

                return true;
            case KeyCode.UpArrow:
                _selected = _selected <= 0 ? Names.Count - 1 : _selected - 1;
                Highlight();

                return true;
            case KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Tab when _selected >= 0:
                _pickedFrame = Time.frameCount;
                _pickedField = field;
                Apply(_selected);

                return true;
            case KeyCode.Escape:
                _escapeFrame = Time.frameCount;
                Hide(_owner);

                return true;
            default:
                return false;
        }
    }

    private static void Apply(int index)
    {
        var owner = _owner;

        if (!owner || index < 0 || index >= Names.Count)
        {
            return;
        }

        var name = Names[index];

        // closed first: the field's "value changed" works the list out again, and the name typed in full offers
        // nothing more
        Hide(owner);
        owner.Apply(name);
    }

    private static void Highlight()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Background.color = i == _selected ? HighlightColor : RowColor;
        }
    }

    private static void EnsurePanel(Transform canvas, TMP_Text fieldText)
    {
        if (_rect)
        {
            if (_rect.parent != canvas)
            {
                _rect.SetParent(canvas, false);
            }

            return;
        }

        // above the hint's panel; it takes clicks
        _rect = DungeonMakerNameHints.NewPanel(canvas, PanelName, DungeonMakerNameHints.HintOrder + 1, true);

        for (var i = 0; i < MaxRows; i++)
        {
            var rowObject = new GameObject($"Row{i}", typeof(RectTransform), typeof(Image));
            var rect = rowObject.GetComponent<RectTransform>();

            // rows hang from the panel's top, one under the other
            rect.SetParent(_rect, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 1f);

            var row = rowObject.AddComponent<Row>();

            row.Index = i;
            row.Background = rowObject.GetComponent<Image>();
            row.Background.color = RowColor;
            row.Background.raycastTarget = true;
            row.Label = DungeonMakerNameHints.NewLabel(rect, fieldText);
            row.Label.enableWordWrapping = false;

            // a gadget's name is shown as typed, never read as rich text (a name holding "<size=200>" would
            // otherwise change or hide its row)
            row.Label.richText = false;
            row.Label.overflowMode = TextOverflowModes.Ellipsis;
            row.Label.alignment = TextAlignmentOptions.MidlineLeft;
            row.Label.rectTransform.anchorMin = row.Label.rectTransform.anchorMax =
                row.Label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            row.Label.rectTransform.anchoredPosition = Vector2.zero;
            Rows.Add(row);
        }

        // the tooltip's background layers go under the rows; every row's text gets the tooltip's font
        // no glow around the list
        _textColor = DungeonMakerNameHints.StyleLikeTooltip(_rect, Rows[0].Label, false) ?? _textColor;

        foreach (var row in Rows)
        {
            row.transform.SetAsLastSibling();
            row.Label.font = Rows[0].Label.font;
            row.Label.fontSharedMaterial = Rows[0].Label.fontSharedMaterial;
            row.Label.fontSize = Rows[0].Label.fontSize;
            row.Label.fontStyle = Rows[0].Label.fontStyle;
            row.Label.color = _textColor;
        }

        _rect.gameObject.AddComponent<Follower>();

        // blocks the tooltips of the editor items under the list
        DungeonMakerNameHints.PutOnGuiLayer(_rect.gameObject);
        _rect.gameObject.SetActive(false);
    }

    // one name of the list: highlighted under the pointer, chosen when the pointer goes down on it
    private sealed class Row : MonoBehaviour, IPointerEnterHandler, IPointerDownHandler
    {
        internal Image Background;
        internal int Index;
        internal TextMeshProUGUI Label;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Left)
            {
                Apply(Index);
            }
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _selected = Index;
            Highlight();
        }
    }

    // Keeps the list on its field, and closes it when the field is gone, or is without the focus and the pointer
    // is not on the list (a click elsewhere, or the pointer leaving after a click on the list's edge).
    private sealed class Follower : MonoBehaviour
    {
        private void LateUpdate()
        {
            var owner = _owner;

            if (!owner || !owner.isActiveAndEnabled || !owner.Field ||
                !Main.Settings.ShowDungeonMakerNameSuggestions ||
                (!owner.HasFocus && !PointerOver(owner)))
            {
                _owner = null;
                _selected = -1;
                gameObject.SetActive(false);

                return;
            }

            DungeonMakerNameHints.Place(_rect, owner.Field.transform as RectTransform, owner.Below);
        }
    }
}
