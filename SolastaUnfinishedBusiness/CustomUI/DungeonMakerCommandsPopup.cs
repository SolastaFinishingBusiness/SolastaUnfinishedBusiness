using System;
using System.Linq;
using System.Text;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

// The list of the mod's campaign commands, shown in the Dungeon Maker where a command is written (issue #42).
//
// User directions, 2026-11-16: "The in-game list should belong in a popup that would appear on the first time the
// user enters the section. The popup will have the option to Close or Hide This From Now On, with the latter
// writing a persistent setting that will prevent the popup from automatically appearing. However, there will be a
// button in the UI (near the close button) listed as a ? That will open the popup if clicked." and "The section
// I mentioned is the location in the dungeon editor where users get to use the command at all".
//
// WHERE: the editors in which a Variable Operation can be set up: the location editor (its gadgets) and the
// dialogue and quest editors (the actions of their lines, steps and outcomes). Not the campaign editor.
//   - Each of them gets a "?" button beside its own CLOSE button, which opens the list at any time. The button is
//     a copy of that CLOSE button (found by what it does: the button of the screen that calls the screen's
//     OnCloseCb), so it looks like the screen's own.
//   - The list opens by itself the first time one of these editors is shown in a game launch, unless the setting
//     DungeonMakerHideCommandsPopup is on. "Hide this from now on" turns that setting on and saves the settings at
//     once (Main.SaveSettingsNow); "Close" only closes. One setting and one "already shown" for all three.
//
// WHAT IT SHOWS: every name the mod uses, in groups (DungeonMakerNamesContext.Catalogue), each in the colour the
// editor draws it in (DungeonMakerNameHints.NameColor) with the text of its hint (DungeonMakerNamesContext.
// NameHint), which for a name the campaign writes itself says where the mod writes instead. The texts are the
// hints' own, so the list can't say something else than the hints do. Above them a line on what the two colours
// mean, below them where the full guide is.
//
// HOW IT IS BUILT: the game has no window that holds a long scrolling text with two buttons of the mod's choosing
// (its message window shows a short text and its own buttons), so this is a panel of the mod's own in the
// tooltip look (the hint's panel pieces): a dimmed layer over the screen that takes the clicks, a window with
// a title, a scrolling text (mouse wheel or drag) and two buttons copied from the screen's CLOSE button. It is
// worked with the mouse; it takes no keys, and closing it only hides it, so nothing is left holding the input.
internal static class DungeonMakerCommandsPopup
{
    private const string HelpButtonName = "FbCommandsHelp";
    private const string PopupName = "FbCommandsPopup";
    private const string CloseMethod = "OnCloseCb";
    private const string SaveMethod = "OnSaveCb";
    private const float Width = 860f;
    private const float MaxHeight = 720f;
    private const float Padding = 22f;
    private const float TitleHeight = 40f;
    private const float ButtonsHeight = 56f;
    private const string TitleColor = "#F0DAA0";

    // under the hint and the suggestions list, above the editor's own screens and windows
    private const int Order = DungeonMakerNameHints.HintOrder - 100;

    // the list has opened by itself in this game launch
    private static bool _shown;

    private static GameObject _dim;
    private static RectTransform _window;
    private static TextMeshProUGUI _title;
    private static TextMeshProUGUI _text;
    private static RectTransform _content;
    private static ScrollRect _scroll;

    // UserLocationEditorScreen, UserDialogEditorScreen and UserQuestEditorScreen, OnBeginShow: one of the editors
    // in which a command can be written is being shown.
    internal static void OnEditorShown(GuiScreen screen)
    {
        if (!screen)
        {
            return;
        }

        try
        {
            var close = FindCloseButton(screen);

            EnsureHelpButton(screen, close);

            if (_shown || Main.Settings.DungeonMakerHideCommandsPopup)
            {
                return;
            }

            _shown = true;
            Open(screen, close);
        }
        catch (Exception e)
        {
            // an editor must open whatever happens here
            Main.Error($"DM commands list: {e.Message}");
        }
    }

    // The screen's CLOSE button: the one that calls the screen's OnCloseCb (set in the game's prefab; none of
    // these screens keeps it in a field).
    private static Button FindCloseButton(GuiScreen screen)
    {
        return FindButton(screen, CloseMethod);
    }

    #region The ? button

    // The ? button (user, 2026-11-16: "The ? shouldn't be a red button, but a normal colour button"). It is a copy
    // of the screen's SAVE button, the editor's ordinary dark button, made square and set just right of CLOSE.
    // The first version copied CLOSE (red) and placed it from CLOSE's RectTransform, which is narrower than the red
    // box CLOSE draws, so the copy landed on CLOSE's right end (seen in game on 9f49bbd6). It is now placed from
    // the edges of what CLOSE draws (every graphic under it), in world space, so whatever CLOSE's anchors and
    // layout, the ? starts past its visible edge.
    private static void EnsureHelpButton(GuiScreen screen, Button close)
    {
        if (!close || close.transform.parent.Find(HelpButtonName))
        {
            return;
        }

        var look = FindButton(screen, SaveMethod) ?? close;
        var help = CopyButton(look, close.transform.parent, HelpButtonName, "?", () => Open(screen, close));

        if (!help)
        {
            return;
        }

        var rect = (RectTransform)help.transform;

        // placed by hand, whatever the row does with its other children
        if (!help.TryGetComponent<LayoutElement>(out var layout))
        {
            layout = help.gameObject.AddComponent<LayoutElement>();
        }

        layout.ignoreLayout = true;

        if (!DrawnBounds(close.transform, out var min, out var max))
        {
            return;
        }

        var parent = (RectTransform)rect.parent;
        var localMin = parent.InverseTransformPoint(min);
        var localMax = parent.InverseTransformPoint(max);
        var height = Mathf.Abs(localMax.y - localMin.y);

        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.sizeDelta = new Vector2(height, height);
        rect.localPosition = new Vector3(Mathf.Max(localMin.x, localMax.x) + HelpGap,
            (localMin.y + localMax.y) / 2f, 0f);
        rect.SetSiblingIndex(close.transform.GetSiblingIndex() + 1);

        // the copy's tooltip, if the button has one, says what the ? is for
        if (help.TryGetComponent<GuiTooltip>(out var tooltip))
        {
            tooltip.Content = Gui.Localize("ModUi/&DungeonMakerCommandsPopupTitle");
        }
    }

    // the space between CLOSE's drawn edge and the ?, in the row's units
    private const float HelpGap = 8f;

    // the screen's button wired in its prefab to this method of the screen
    private static Button FindButton(GuiScreen screen, string method)
    {
        return screen.GetComponentsInChildren<Button>(true).FirstOrDefault(button =>
            button.name != HelpButtonName &&
            Enumerable.Range(0, button.onClick.GetPersistentEventCount()).Any(i =>
                button.onClick.GetPersistentMethodName(i) == method &&
                ReferenceEquals(button.onClick.GetPersistentTarget(i), screen)));
    }

    // the world-space box around every graphic drawn under this object
    private static bool DrawnBounds(Transform root, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue, float.MaxValue, 0f);
        max = new Vector3(float.MinValue, float.MinValue, 0f);

        var found = false;
        var corners = new Vector3[4];

        foreach (var graphic in root.GetComponentsInChildren<Graphic>(false))
        {
            if (!graphic.enabled || graphic.color.a <= 0f)
            {
                continue;
            }

            graphic.rectTransform.GetWorldCorners(corners);

            foreach (var corner in corners)
            {
                min = Vector3.Min(min, corner);
                max = Vector3.Max(max, corner);
            }

            found = true;
        }

        return found;
    }

    // A copy of one of the game's buttons with the mod's text and action: the game's own actions (set in the
    // prefab) and its tooltip are taken off the copy.
    private static Button CopyButton(Button source, Transform parent, string name, string text, Action onClick)
    {
        if (!source)
        {
            return null;
        }

        var copy = Object.Instantiate(source.gameObject, parent, false);

        copy.name = name;

        var button = copy.GetComponent<Button>();

        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => onClick());
        button.interactable = true;

        foreach (var tooltip in copy.GetComponentsInChildren<GuiTooltip>(true))
        {
            tooltip.Clear();
        }

        // the game's label component sets its text itself when it wakes up, so the text goes through it
        var label = copy.GetComponentInChildren<GuiLabel>(true);

        if (label)
        {
            label.Text = text;
        }
        else if (copy.GetComponentInChildren<TMP_Text>(true) is { } plain)
        {
            plain.text = text;
        }

        // The label alone is laid out. The editors' SAVE button (read from the prefab, 2026-11-16) is a
        // HorizontalLayoutGroup, padding 10 left and right, centred, spacing 4, over its children Image (an icon
        // slot, the arrow on UNDO; on SAVE it draws nothing but is active, laid out, preferred width 16) and Label.
        // The empty slot and the spacing (16 + 4) put the label 10 units right of the button's middle, so the ?
        // was drawn off centre (user, on bac0aa92: "The ? Button is still not centered"). Every other laid-out
        // child of the copy is switched off; the backgrounds (LayoutElement.ignoreLayout set in the prefab) and the
        // inactive gamepad prompt stay as they are.
        var labelObject = label ? label.gameObject : copy.GetComponentInChildren<TMP_Text>(true)?.gameObject;

        foreach (Transform child in copy.transform)
        {
            if (labelObject && (child.gameObject == labelObject || labelObject.transform.IsChildOf(child)))
            {
                continue;
            }

            if (child.TryGetComponent<LayoutElement>(out var element) && !element.ignoreLayout)
            {
                child.gameObject.SetActive(false);
            }
        }

        copy.SetActive(true);

        return button;
    }

    #endregion

    #region The list

    private static void Open(GuiScreen screen, Button close)
    {
        var nearest = screen ? screen.GetComponentInParent<Canvas>() : null;
        var canvas = nearest ? nearest.rootCanvas : null;

        if (!canvas)
        {
            return;
        }

        EnsureWindow(canvas.transform, close);

        _title.text = Gui.Localize("ModUi/&DungeonMakerCommandsPopupTitle");
        _text.text = BuildText(screen);

        // as high as the text needs, up to MaxHeight and the screen; the text scrolls within
        var area = ((RectTransform)canvas.transform).rect;
        var textWidth = Width - (2 * Padding);
        var textHeight = _text.GetPreferredValues(_text.text, textWidth, 0f).y;
        var chrome = TitleHeight + ButtonsHeight + (3 * Padding);
        var height = Mathf.Min(textHeight + chrome, MaxHeight, area.height - 60f);

        _window.sizeDelta = new Vector2(Width, height);
        _content.sizeDelta = new Vector2(0f, textHeight);
        _text.rectTransform.sizeDelta = new Vector2(textWidth, textHeight);
        _scroll.verticalNormalizedPosition = 1f;

        _dim.SetActive(true);
        _dim.transform.SetAsLastSibling();
        _openOn = screen;
    }

    private static void Close()
    {
        if (_dim)
        {
            _dim.SetActive(false);
        }

        _openOn = null;
    }

    // the editor the list was opened over, while it is open
    private static GuiScreen _openOn;

    internal static bool IsOpen => _dim && _dim.activeSelf;

    // The editors' HandleInput, before it runs (Codex's review of 269d166a: the list only blocked the mouse, and
    // the editor behind it still acted on its keys: Delete, Undo, and Escape, which left the editor with the list
    // still on screen). While the list is open it takes the editor's commands: Cancel (Escape) closes the list,
    // every other command is swallowed. Returns false when the editor must not act on the command.
    internal static bool LetEditorHandle(InputCommands.Id command)
    {
        if (!IsOpen)
        {
            return true;
        }

        if (command == InputCommands.Id.Cancel)
        {
            Close();
        }

        return false;
    }

    // The editors' OnEndHide: the list goes with the editor it was opened over.
    internal static void OnEditorHidden(GuiScreen screen)
    {
        if (IsOpen && (!_openOn || ReferenceEquals(_openOn, screen)))
        {
            Close();
        }
    }

    // The list's text: the colours' meaning, then each group's title and names with what they do, then the guide.
    private static string BuildText(GuiScreen screen)
    {
        // the campaign and location being edited, for where the mod writes when the campaign uses a name itself
        var campaign = screen switch
        {
            UserLocationEditorScreen location => location.UserCampaign,
            UserEditableGraphEditorScreen graph => graph.UserCampaign,
            _ => null
        };
        var place = (screen as UserLocationEditorScreen)?.UserLocation;
        var text = new StringBuilder();

        text.Append(Gui.Localize("ModUi/&DungeonMakerCommandsPopupIntro")).Append('\n');
        text.Append(Colored(DungeonMakerNameHints.NameColor(DungeonMakerCommandsContext.TeleportNodeVariable),
            Gui.Localize("ModUi/&DungeonMakerCommandsLegendBlue"))).Append('\n');
        text.Append(Colored(DungeonMakerNameHints.NameColor("Clock_Hour"),
            Gui.Localize("ModUi/&DungeonMakerCommandsLegendRed"))).Append('\n');

        foreach (var (titleTerm, names) in DungeonMakerNamesContext.Catalogue())
        {
            text.Append("\n<b><color=").Append(TitleColor).Append('>')
                .Append(Gui.Localize(titleTerm)).Append("</color></b>\n");

            foreach (var name in names)
            {
                var hint = DungeonMakerNamesContext.NameHint(name, campaign, place) ?? string.Empty;

                // a name is written as it is, angle brackets included (Torch_<name>), not read as rich text
                text.Append("<b>").Append(Colored(DungeonMakerNameHints.NameColor(name), $"<noparse>{name}</noparse>"))
                    .Append("</b>  ")
                    .Append(hint.Replace("\n", " ")).Append('\n');
            }
        }

        text.Append('\n').Append(Gui.Localize("ModUi/&DungeonMakerCommandsPopupGuide"));

        return text.ToString();
    }

    private static string Colored(Color? color, string text)
    {
        return color == null ? text : $"<color=#{ColorUtility.ToHtmlStringRGB(color.Value)}>{text}</color>";
    }

    private static void EnsureWindow(Transform canvas, Button close)
    {
        if (_dim)
        {
            if (_dim.transform.parent != canvas)
            {
                _dim.transform.SetParent(canvas, false);
            }

            return;
        }

        // the dimmed layer: over the whole screen, taking the clicks so the editor behind is not worked by mistake
        var dim = DungeonMakerNameHints.NewPanel(canvas, PopupName, Order, true);

        _dim = dim.gameObject;
        dim.anchorMin = Vector2.zero;
        dim.anchorMax = Vector2.one;
        dim.offsetMin = dim.offsetMax = Vector2.zero;
        _dim.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

        // the window: a dark box in the tooltip's look, in the middle
        var windowObject = new GameObject("Window", typeof(RectTransform), typeof(Image));

        _window = windowObject.GetComponent<RectTransform>();
        _window.SetParent(dim, false);
        _window.anchorMin = _window.anchorMax = _window.pivot = new Vector2(0.5f, 0.5f);
        windowObject.GetComponent<Image>().color = new Color(0.06f, 0.06f, 0.08f, 0.98f);

        // title, along the top
        _title = DungeonMakerNameHints.NewLabel(_window, null);
        _title.alignment = TextAlignmentOptions.Midline;
        _title.enableWordWrapping = false;
        _title.rectTransform.anchorMin = new Vector2(0f, 1f);
        _title.rectTransform.anchorMax = new Vector2(1f, 1f);
        _title.rectTransform.pivot = new Vector2(0.5f, 1f);
        _title.rectTransform.offsetMin = new Vector2(Padding, -(Padding + TitleHeight));
        _title.rectTransform.offsetMax = new Vector2(-Padding, -Padding);

        // the scrolling text, between the title and the buttons
        var viewObject = new GameObject("View", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
        var view = viewObject.GetComponent<RectTransform>();

        view.SetParent(_window, false);
        view.anchorMin = Vector2.zero;
        view.anchorMax = Vector2.one;
        view.offsetMin = new Vector2(Padding, ButtonsHeight + Padding);
        view.offsetMax = new Vector2(-Padding, -(TitleHeight + (2 * Padding)));

        // see-through, but it is what the mouse wheel and a drag land on
        viewObject.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);

        var contentObject = new GameObject("Content", typeof(RectTransform));

        _content = contentObject.GetComponent<RectTransform>();
        _content.SetParent(view, false);
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;

        _text = DungeonMakerNameHints.NewLabel(_content, null);
        _text.rectTransform.anchorMin = _text.rectTransform.anchorMax = _text.rectTransform.pivot =
            new Vector2(0.5f, 1f);
        _text.rectTransform.anchoredPosition = Vector2.zero;

        _scroll = viewObject.AddComponent<ScrollRect>();
        _scroll.content = _content;
        _scroll.viewport = view;
        _scroll.horizontal = false;
        _scroll.vertical = true;
        _scroll.movementType = ScrollRect.MovementType.Clamped;
        _scroll.scrollSensitivity = 40f;

        // the tooltip's background layers under everything of the window, its font for both texts
        var color = DungeonMakerNameHints.StyleLikeTooltip(_window, _text);

        _title.font = _text.font;
        _title.fontSharedMaterial = _text.fontSharedMaterial;
        _title.fontSize = _text.fontSize * 1.3f;
        _title.color = ColorUtility.TryParseHtmlString(TitleColor, out var gold) ? gold : Color.white;
        _text.color = color ?? Color.white;
        _title.transform.SetAsLastSibling();
        view.SetAsLastSibling();

        // the two buttons, along the bottom: copies of the screen's CLOSE button, or plain ones without it
        var closeButton = MakeButton(close, "Close", Gui.Localize("ModUi/&DungeonMakerCommandsPopupClose"), Close);
        var hideButton = MakeButton(close, "Hide", Gui.Localize("ModUi/&DungeonMakerCommandsPopupHide"), () =>
        {
            Main.Settings.DungeonMakerHideCommandsPopup = true;
            Main.SaveSettingsNow();
            Close();
        });

        // side by side around the window's middle
        PlaceButton(closeButton, 180f, -190f);
        PlaceButton(hideButton, 320f, 10f);
        _dim.SetActive(false);
    }

    private static RectTransform MakeButton(Button source, string name, string text, Action onClick)
    {
        var copy = CopyButton(source, _window, name, text, onClick);

        if (copy)
        {
            if (copy.TryGetComponent<LayoutElement>(out var layout))
            {
                layout.ignoreLayout = true;
            }

            return (RectTransform)copy.transform;
        }

        // no button of the game's to copy: a plain box with the text
        var plain = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        var rect = plain.GetComponent<RectTransform>();

        rect.SetParent(_window, false);
        plain.GetComponent<Image>().color = new Color(0.22f, 0.22f, 0.26f, 1f);
        plain.GetComponent<Button>().onClick.AddListener(() => onClick());

        var label = DungeonMakerNameHints.NewLabel(rect, _text);

        label.font = _text.font;
        label.fontSharedMaterial = _text.fontSharedMaterial;
        label.text = text;
        label.alignment = TextAlignmentOptions.Midline;
        label.enableWordWrapping = false;
        label.rectTransform.anchorMin = Vector2.zero;
        label.rectTransform.anchorMax = Vector2.one;
        label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;

        return rect;
    }

    // along the window's bottom edge: width wide, its left edge at offset from the window's middle
    private static void PlaceButton(RectTransform rect, float width, float offset)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.sizeDelta = new Vector2(width, ButtonsHeight - 14f);
        rect.anchoredPosition = new Vector2(offset, Padding - 6f);
        rect.localScale = Vector3.one;
    }

    #endregion
}
