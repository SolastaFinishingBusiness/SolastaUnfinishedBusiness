using System;
using System.Linq;
using System.Runtime.CompilerServices;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

// Hints shown above a text field of the Dungeon Maker while its author types (issue #42; user direction,
// 2026-11-16: "Give an alert for potential typos in the dungeon maker editor, similar to the information alert for
// when the author uses a marked variable that the mod uses. Additionally, please ensure that the alert would look
// like a tooltip above when the author is typing in the variable name").
//
// WHAT IS SAID (the texts come from DungeonMakerNamesContext, which holds the rules):
//   - a variable's name that the mod uses: what the mod does with it and, for a name the mod writes, the name it
//     will write to instead when the campaign writes that one itself;
//   - the text a Variable Operation writes into Dialog_Trigger, Roll_Trigger, Teleport_Node or
//     Teleport_PartyNode, when nothing goes by that name: that nothing does, with the closest names.
//
// WHERE (each is a text field of the game's own screens; the hooks are in DungeonMakerCommandsPatcher):
//   1. the name field of the window that creates or renames a campaign variable (UserContentEditionModal,
//      internalNameInputField): name hint;
//   2. a gadget's parameters in the location editor (GadgetParameterLine): the variable field (variableField) of
//      a Variable Operation or Variable Activator: name hint; the text value field (stringField) of a Variable
//      Operation: missing-name warning, looked up among the gadgets of that location, where the gadget will
//      write it;
//   3. the parameters of an action or condition in the dialogue and quest editors (UserParameterItem): the
//      variable field: name hint; the text value field of a Variable Operation action: missing-name warning,
//      looked up in every location of the campaign, as such an action can run in any of them.
//
// HOW IT LOOKS AND BEHAVES. The game's own tooltips can't be used for this: its tooltip service only ever shows
// the tooltip of whatever is under the mouse pointer, where the pointer is (GuiManager.UpdateTooltip; a forced
// tooltip is moved to the screen's tooltip dock). So this is a panel of the mod's own, made to look like one:
// it takes the background picture, the font and the text colour from the game's tooltip panel prefab
// (GuiTooltipClassDefinition.TooltipPanelPrefab), falling back to a dark box with the field's own font.
//   - It sits above the field, centred on it, and below it only when there is no room above; it never lies over
//     the field, and it follows the field when the list it is in scrolls. In the dialogue and quest editors it
//     sits below the field instead (user, 2026-11-16: "Move it down beneath the textbox for the dialogue
//     editor"): above, it covered the row above it (the variable field and its Select button).
//   - It is shown only while the field has the keyboard focus and has something to say, is worked out again at
//     every key, and goes when the field loses the focus, is hidden, or the text is fine.
//   - It takes no clicks (nothing in it is a raycast target), and it changes nothing of the campaign.
//   - While the text of a command variable is typed, the list of gadget names to choose from takes its place
//     (DungeonMakerNameSuggestions); the missing-name warning shows only when that list has nothing to offer.
// The panel pieces (NewPanel, NewLabel, StyleLikeTooltip, Place) are shared with that list and with the commands
// list (DungeonMakerCommandsPopup).
//
// COLOURED NAMES (user, 2026-11-16: "mark red the variables that would explicitly clash with variables used by
// the mod for automation? Then, mark a special colour for variables that have a special effect (like
// `Teleport_Node`)"). Wherever the editor shows a campaign variable's name, the name is drawn in a colour of its
// own when the mod uses it (NameColor):
//   - red: a name the mod itself writes, so a value the campaign keeps there is overwritten (the clock read-outs
//     and counters, every <command>_Status, the mod's own clock-skip variables);
//   - blue: a name that does something when the campaign writes it (the commands such as Teleport_Node, the
//     requests such as Torches_Lit or Clock_SkipHours, the counter switches Clock_Count*).
// Where: the rows of the campaign editor's variable list and of the variable picker (UserVariableLine), and the
// variable fields of a gadget's parameters and of a dialogue's or quest's action or condition. A name the mod
// doesn't use keeps the game's colour, which is put back when the game reuses a row or field for another name.
internal static class DungeonMakerNameHints
{
    private const string PanelName = "FbNameHint";
    private const float MaxWidth = 520f;
    private const float PaddingX = 14f;
    private const float PaddingY = 10f;
    private const float Gap = 6f;
    private const float Margin = 4f;

    // the text colour of a warning; a hint uses the tooltip's own
    private static readonly Color WarningColor = new(0.95f, 0.72f, 0.3f);

    // the colours of the names the mod uses (see COLOURED NAMES at the top)
    private static readonly Color WrittenByModColor = new(1f, 0.42f, 0.38f);
    private static readonly Color CommandColor = new(0.45f, 0.78f, 1f);

    // each text's own colour, as the game left it, from the first time it is coloured
    private static readonly ConditionalWeakTable<TMP_Text, object> GameColors = new();

    // the suggestions list is drawn above the hint, and the commands list's own window below both
    internal const int HintOrder = 32000;

    private static GameObject _panel;
    private static RectTransform _rect;
    private static TextMeshProUGUI _label;
    private static Color _textColor = Color.white;
    // who shows the panel now (a field's hint, or a row's of the variable lists) and what it is placed on
    private static MonoBehaviour _owner;
    private static RectTransform _target;

    // the tooltip prefab's glow layer (TooltipPanelSimple's first child)
    private const string ShadowLayerName = "Shadow";

    // whether the field being hinted wants the panel below it rather than above
    private static bool _below;
    private static readonly Vector3[] Corners = new Vector3[4];

    #region Which fields

    // UserContentEditionModal.Show: the window that asks for the name of a new piece of campaign content, or
    // renames one. Only a variable's name matters here.
    internal static void ForContentModal(UserContentEditionModal modal)
    {
        if (!modal)
        {
            return;
        }

        DungeonMakerFieldHint.Attach(modal.internalNameInputField, false, text =>
            modal.UserContent is UserVariable
                ? DungeonMakerNamesContext.NameHint(text, modal.UserCampaign, null)
                : null);
    }

    // GadgetParameterLine.Bind: one parameter of the gadget selected in the location editor. The lines are
    // reused for other parameters and gadgets, so what a field is for is asked of the line each time.
    internal static void ForGadgetLine(GadgetParameterLine line)
    {
        if (!line)
        {
            return;
        }

        bool IsVariable()
        {
            return line.UserGadgetParameterValue?.GadgetParameterDescription?.Type ==
                   GadgetBlueprintDefinitions.Type.Variable;
        }

        DungeonMakerFieldHint.Attach(line.variableField, false, text =>
                IsVariable() ? DungeonMakerNamesContext.NameHint(text, line.UserCampaign, line.UserLocation) : null,
            colorName: IsVariable);

        // The variable this gadget writes, when this line is its text value: its "variable" parameter. Null for
        // any other line, so the field then says and offers nothing.
        string WrittenVariable()
        {
            return line.UserGadgetParameterValue?.GadgetParameterDescription?.Type !=
                   GadgetBlueprintDefinitions.Type.VariableValueString
                ? null
                : line.UserGadget?.ParameterValues?
                    .FirstOrDefault(x =>
                        x?.GadgetParameterDescription?.Type == GadgetBlueprintDefinitions.Type.Variable)?
                    .StringValue;
        }

        // the names of this location: that is where the gadget will write
        DungeonMakerFieldHint.Attach(line.stringField, true,
            text => DungeonMakerNamesContext.ValueHint(
                WrittenVariable(), text, line.UserCampaign, line.UserLocation),
            suggest: text => Suggestions(WrittenVariable(), text, line.UserCampaign, line.UserLocation));
    }

    // The gadget names to offer for this text, when the variable is one of the four commands that take a name
    // (the same lookup as the missing-name warning, DungeonMakerNamesContext.ValueHint); null otherwise.
    private static System.Collections.Generic.List<string> Suggestions(
        string variable, string text, UserCampaign campaign, UserLocation location)
    {
        return DungeonMakerNamesContext.RoleOf(variable, out _) != DungeonMakerNamesContext.Role.Command
            ? null
            : DungeonMakerNamesContext.Suggest(
                DungeonMakerNamesContext.Targets(variable, campaign, location), text,
                DungeonMakerNameSuggestions.MaxRows);
    }

    // UserParameterItem.CommondBind: one parameter of an action or a condition in the dialogue and quest editors
    internal static void ForParameterItem(UserParameterItem item)
    {
        if (!item)
        {
            return;
        }

        bool IsVariable()
        {
            return item.parameterType is UserParameterDefinitions.Type.VariableOperationVariable
                or UserParameterDefinitions.Type.VariableTestVariable;
        }

        // below the field here: the dialogue and quest editors stack their parameter rows tightly, and a hint
        // above covered the row above (see the top of the file)
        DungeonMakerFieldHint.Attach(item.variableField, false, text =>
                IsVariable() ? DungeonMakerNamesContext.NameHint(text, item.UserCampaign, null) : null,
            true, IsVariable);

        // a Variable Operation action keeps its variable in its first text (see DungeonMakerNamesContext)
        string WrittenVariable()
        {
            return item.parameterType == UserParameterDefinitions.Type.VariableOperationValueString
                ? item.functorParametersDescription?.StringParameter
                : null;
        }

        // the names of every location: such an action can run in any of them
        DungeonMakerFieldHint.Attach(item.stringField, true,
            text => DungeonMakerNamesContext.ValueHint(WrittenVariable(), text, item.UserCampaign, null),
            true,
            suggest: text => Suggestions(WrittenVariable(), text, item.UserCampaign, null));
    }

    // UserVariableLine.BindSpecific: a row of the campaign editor's variable list or of the variable picker was
    // given a variable (rows are reused as the list scrolls and changes)
    internal static void ForVariableLine(UserVariableLine line)
    {
        if (!line || !line.internalNameLabel)
        {
            return;
        }

        ColorName(line.internalNameLabel.TMP_Text, line.UserVariable?.InternalName);
    }

    // UserContentLine.Bind of a UserVariableLine: the row says what the mod does with its variable's name while
    // the pointer is over it (user, 2026-11-16: "can you modify the variable selector to allow hovering over mod
    // commands to show the hint as well"). The text is the one a field shows while that name is typed
    // (DungeonMakerNamesContext.NameHint), so a name the mod writes also says where it is written with the
    // campaign as it is. Rows are reused as the list scrolls, so the row asks for its variable when the pointer
    // comes in, and a name the mod doesn't use shows nothing. The campaign editor's variable list uses the same
    // rows, so it gets the hover too.
    internal static void ForVariableLineHover(UserVariableLine line, UserCampaign campaign)
    {
        if (!line)
        {
            return;
        }

        if (!line.TryGetComponent<DungeonMakerRowHint>(out var hover))
        {
            hover = line.gameObject.AddComponent<DungeonMakerRowHint>();
        }

        hover.Line = line;
        hover.Campaign = campaign;
    }

    // The colour a variable's name is drawn in, when the mod uses that name (see COLOURED NAMES at the top).
    internal static Color? NameColor(string name)
    {
        return DungeonMakerNamesContext.RoleOf((name ?? string.Empty).Trim(), out _) switch
        {
            DungeonMakerNamesContext.Role.Readout or DungeonMakerNamesContext.Role.Counter
                or DungeonMakerNamesContext.Role.Status or DungeonMakerNamesContext.Role.Internal =>
                WrittenByModColor,
            DungeonMakerNamesContext.Role.Command or DungeonMakerNamesContext.Role.Request
                or DungeonMakerNamesContext.Role.Switch => CommandColor,
            _ => null
        };
    }

    // Draws this text in its name's colour, or in the colour the game gave it when the name isn't one of the
    // mod's (name null: the text isn't a name at all, and gets the game's colour back). The game's colour is kept
    // the first time a text is coloured: the game never sets these colours again when it reuses the text.
    internal static void ColorName(TMP_Text text, string name)
    {
        if (!text)
        {
            return;
        }

        var wanted = name == null ? null : NameColor(name);

        if (!GameColors.TryGetValue(text, out var kept))
        {
            if (wanted == null)
            {
                return;
            }

            kept = text.color;
            GameColors.Add(text, kept);
        }

        text.color = wanted ?? (Color)kept;
    }

    #endregion

    #region The panel

    internal static void Show(DungeonMakerFieldHint owner, TMP_InputField field, string text, bool warning,
        bool below = false)
    {
        if (field)
        {
            Show(owner, field.transform as RectTransform, field.textComponent, text, warning, below);
        }
    }

    // The panel on any element: target is what it is placed against, font a text whose font it takes until the
    // tooltip's look is found. owner: who hides it again (Hide with the same owner); the panel also goes when the
    // owner is switched off or destroyed.
    internal static void Show(MonoBehaviour owner, RectTransform target, TMP_Text font, string text, bool warning,
        bool below)
    {
        var nearest = target ? target.GetComponentInParent<Canvas>() : null;
        var canvas = nearest ? nearest.rootCanvas : null;

        if (!canvas)
        {
            return;
        }

        // switched off in Mod UI > Dungeon Maker, each kind on its own (user, 2026-11-17: "Hide information about
        // campaign commands" and "Hide error hints"): warning is true only for the warning that nothing goes by
        // the name written into a command (the command-value fields), false for what a name does (typing a
        // variable's name, pointing at a variable row). Every hint comes through here, so a switched-off kind
        // never shows, and one still up from before the switch goes.
        if (warning ? Main.Settings.DisableDungeonMakerErrorHints : Main.Settings.DisableDungeonMakerCommandHints)
        {
            if (_panel)
            {
                _owner = null;
                _target = null;
                _panel.SetActive(false);
            }

            return;
        }

        EnsurePanel(canvas.transform, font);

        _owner = owner;
        _target = target;
        _below = below;
        _label.color = warning ? WarningColor : _textColor;
        _label.text = text;

        // as wide as the text needs, up to MaxWidth, and as high as it then wraps
        var wanted = _label.GetPreferredValues(text, MaxWidth, 0f);
        var width = Mathf.Min(wanted.x, MaxWidth);

        wanted = _label.GetPreferredValues(text, width, 0f);
        _label.rectTransform.sizeDelta = new Vector2(width, wanted.y);
        _rect.sizeDelta = new Vector2(width + (2 * PaddingX), wanted.y + (2 * PaddingY));

        _panel.SetActive(true);
        _rect.SetAsLastSibling();
        Place(_rect, target, _below);
    }

    internal static void Hide(MonoBehaviour owner)
    {
        if (_panel && ReferenceEquals(_owner, owner))
        {
            _owner = null;
            _target = null;
            _panel.SetActive(false);
        }
    }

    // Above the field, centred on it and kept inside the screen; below it when there is no room above. Worked out
    // in the canvas's own space, so the canvas's scale and render mode don't matter.
    internal static void Place(RectTransform panel, RectTransform target, bool below)
    {
        if (!panel || panel.parent is not RectTransform canvas || !target)
        {
            return;
        }

        target.GetWorldCorners(Corners);

        // corners: bottom left, top left, top right, bottom right
        var top = canvas.InverseTransformPoint((Corners[1] + Corners[2]) / 2f);
        var bottom = canvas.InverseTransformPoint((Corners[0] + Corners[3]) / 2f);
        var size = panel.sizeDelta;
        var area = canvas.rect;
        var roomAbove = top.y + Gap + size.y <= area.yMax - Margin;
        var roomBelow = bottom.y - Gap - size.y >= area.yMin + Margin;

        // the side the field asks for, unless only the other one has room
        var above = below ? !roomBelow && roomAbove : roomAbove;
        var half = size.x / 2f;
        var x = area.width > size.x + (2 * Margin)
            ? Mathf.Clamp(top.x, area.xMin + half + Margin, area.xMax - half - Margin)
            : top.x;

        panel.pivot = new Vector2(0.5f, above ? 0f : 1f);
        panel.localPosition = new Vector3(x, above ? top.y + Gap : bottom.y - Gap, 0f);
    }

    private static void EnsurePanel(Transform canvas, TMP_Text fieldText)
    {
        if (_panel)
        {
            if (_rect.parent != canvas)
            {
                _rect.SetParent(canvas, false);
            }

            return;
        }

        _rect = NewPanel(canvas, PanelName, HintOrder, false);
        _panel = _rect.gameObject;
        _label = NewLabel(_rect, fieldText);
        _label.rectTransform.anchorMin = _label.rectTransform.anchorMax =
            _label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _label.rectTransform.anchoredPosition = Vector2.zero;
        _textColor = StyleLikeTooltip(_rect, _label) ?? _textColor;
        _panel.AddComponent<DungeonMakerHintFollower>();
        _panel.SetActive(false);
    }

    // A panel of the mod's own on this canvas, drawn above the editor's screens and windows whatever their order
    // in the canvas: a dark box, to be dressed with StyleLikeTooltip. clickable: it takes clicks (the suggestions
    // list and the commands list); the hint doesn't, so that it never gets between the author and the editor.
    internal static RectTransform NewPanel(Transform canvas, string name, int order, bool clickable)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Canvas));
        var rect = panel.GetComponent<RectTransform>();

        rect.SetParent(canvas, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.localScale = Vector3.one;

        var sorting = panel.GetComponent<Canvas>();

        sorting.overrideSorting = true;
        sorting.sortingOrder = order;

        var background = panel.GetComponent<Image>();

        background.raycastTarget = clickable;
        background.color = new Color(0.06f, 0.06f, 0.08f, 0.96f);

        if (clickable)
        {
            // a canvas that sorts itself needs its own raycaster for its children to be clicked
            panel.AddComponent<GraphicRaycaster>();
        }

        return rect;
    }

    // a text for such a panel: the field's font until StyleLikeTooltip gives it the tooltip's
    internal static TextMeshProUGUI NewLabel(RectTransform parent, TMP_Text fieldText)
    {
        var textObject = new GameObject("Text", typeof(RectTransform));

        textObject.GetComponent<RectTransform>().SetParent(parent, false);

        var label = textObject.AddComponent<TextMeshProUGUI>();

        label.raycastTarget = false;
        label.enableWordWrapping = true;
        label.richText = true;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.fontSize = 18f;

        if (fieldText)
        {
            label.font = fieldText.font;
            label.fontSize = Mathf.Max(16f, fieldText.fontSize);
        }

        return label;
    }

    // Takes the look of the game's tooltip.
    //
    // The background: the game's tooltip panel prefab (TooltipPanelSimple for the default class) draws it in
    // layers, its direct children in this order: Shadow (sprite TooltipShadow, a soft glow around it),
    // BackgroundBlur (no sprite; material BlurredBackground, shader Custom/UI/BlurredBackground: the panel's
    // fill) and Frame (sprite Frame1Pixel, its border), each a stretched Image (read from the prefab, 2026-11-16;
    // Codex's review of 50f0b519 for the materials). Each layer that draws something, a sprite or a material of
    // its own, is copied under the hint's panel, in the same order and with the same anchors, sprite, material and
    // colour. The panel's own dark box stays under them: the blur shader samples a texture the game fills for its
    // own tooltips (_BlurredBackground), which this editor canvas may not have, so the box keeps the hint readable
    // whatever the blur draws. (The first version took only the prefab's first Image with a sprite, the Shadow:
    // the hint was a near-invisible glow, seen in game on 02a462d3; the second skipped the sprite-less
    // BackgroundBlur and cleared the box, leaving only a border, found by Codex's review of 50f0b519.)
    //
    // The text: the panel prefab holds no text of its own (its rows are added when a tooltip is shown), so the
    // font, material and colour come from the class's description row (the feature prefab with a
    // TooltipFeatureDescription, whose label is the tooltip's body text). The first version found no text in the
    // panel prefab and so kept the input field's font, the editor's serif capitals.
    //
    // Whatever is missing keeps the plain look: a dark box and the field's font.
    //
    // panel: the panel to dress (the hint's, the suggestions list's, the commands list's window); label: a text to
    // give the tooltip's font, or null; shadow: whether the prefab's Shadow layer (the soft glow around it) is
    // copied too (user, 2026-11-16, of the suggestions list: "Remove the shadow around the suggestions as well").
    // Returns the tooltip's text colour, or null when it could not be taken.
    internal static Color? StyleLikeTooltip(RectTransform panel, TMP_Text label, bool shadow = true)
    {
        Color? textColor = null;

        try
        {
            if (ServiceRepository.GetService<ITooltipService>() is not GuiManager
                {
                    tooltipClassDefinitions: { } classes
                })
            {
                return null;
            }

            var definition = classes.TryGetValue(GuiManager.DefaultTooltipClass, out var standard) && standard
                ? standard
                : classes.Values.FirstOrDefault(x => x && x.TooltipPanelPrefab);

            if (!definition)
            {
                return null;
            }

            var prefab = definition.TooltipPanelPrefab;
            var layers = 0;

            if (prefab)
            {
                foreach (Transform child in prefab.transform)
                {
                    // only the picture layers; the features table and anything else of the prefab stay out
                    if (child.GetComponent<Image>() is not { } picture ||
                        (!shadow && child.name == ShadowLayerName) ||
                        (!picture.sprite && picture.material == picture.defaultMaterial) ||
                        child.GetComponentsInChildren<Component>(true).Any(x =>
                            x is not RectTransform and not CanvasRenderer and not Image))
                    {
                        continue;
                    }

                    var copy = UnityEngine.Object.Instantiate(child.gameObject, panel, false);

                    copy.name = child.name;
                    copy.GetComponent<Image>().raycastTarget = false;
                    copy.transform.SetSiblingIndex(layers++);
                }
            }

            var text = definition.TooltipFeatures?
                .Select(x => x.featurePrefab ? x.featurePrefab.GetComponent<TooltipFeatureDescription>() : null)
                .Where(x => x)
                .Select(x => x.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.font))
                .FirstOrDefault(x => x);

            if (text)
            {
                if (label)
                {
                    label.font = text.font;
                    label.fontSharedMaterial = text.fontSharedMaterial;
                    label.fontSize = text.fontSize;
                    label.fontStyle = text.fontStyle;
                }

                textColor = text.color;
            }

            Main.Info($"DM name hints: tooltip look for '{panel.name}' taken from " +
                      $"'{(prefab ? prefab.name : "?")}' ({layers} background layers) and font " +
                      $"'{(text ? text.font.name : "the field's")}'.");
        }
        catch (Exception e)
        {
            Main.Info($"DM name hints: the game's tooltip look could not be taken, a plain one is used: {e.Message}");
        }

        return textColor;
    }

    // keeps the panel on its field or row while it is shown, and takes it away when that goes
    private sealed class DungeonMakerHintFollower : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (!_owner || !_owner.isActiveAndEnabled || !_target || !_target.gameObject.activeInHierarchy)
            {
                _owner = null;
                _target = null;
                gameObject.SetActive(false);

                return;
            }

            Place(_rect, _target, _below);
        }
    }

    #endregion
}

// Sits on a text field of the editor and shows a hint above it while the field has the keyboard focus (see
// DungeonMakerNameHints). It only listens to the field's own events; the field and what it edits are not changed.
internal sealed class DungeonMakerFieldHint : MonoBehaviour
{
    private bool _focused;

    // the text to show for what is typed, or null for nothing; asked again at every key
    private Func<string, string> _hint;
    private bool _warning;
    private bool _below;

    // a suggestion is being put in (its own "value changed" is not an edit), and the list stays closed after one
    // until the text is edited again
    private bool _picked;
    private bool _closedByPick;

    // whether the field's text is a variable's name to colour (asked each time, as the field is reused)
    private Func<bool> _colorName;

    // the gadget names to offer for what is typed, or null when the field is not a command's target
    private Func<string, System.Collections.Generic.List<string>> _suggest;

    // after a suggestion was clicked: put the caret at the end once the field has the focus again (the field
    // selects its whole text when it gets the focus)
    private bool _caretToEnd;

    internal bool Below => _below;

    // The field has the keyboard focus. The field's own isFocused only turns true a frame after it is selected
    // (it starts editing in its next LateUpdate), so the selection event counts too.
    internal bool HasFocus => _focused || (Field && Field.isFocused);

    internal TMP_InputField Field { get; private set; }

    // Puts a hint on this field, or gives the field's existing one its new meaning (the editor reuses its
    // parameter lines for other parameters).
    internal static void Attach(TMP_InputField field, bool warning, Func<string, string> hint, bool below = false,
        Func<bool> colorName = null, Func<string, System.Collections.Generic.List<string>> suggest = null)
    {
        if (!field)
        {
            return;
        }

        var component = field.GetComponent<DungeonMakerFieldHint>();

        if (!component)
        {
            component = field.gameObject.AddComponent<DungeonMakerFieldHint>();
            component.Field = field;
            field.onValueChanged.AddListener(component.OnChanged);
            field.onSelect.AddListener(component.OnSelected);
            field.onDeselect.AddListener(component.OnDeselected);
        }

        component._hint = hint;
        component._warning = warning;
        component._below = below;
        component._colorName = colorName;
        component._suggest = suggest;
        component.UpdateColor();

        if (component._focused)
        {
            component.Refresh();
        }
    }

    private void OnChanged(string text)
    {
        // any edit but the chosen name itself opens the list again
        if (!_picked)
        {
            _closedByPick = false;
        }

        UpdateColor();
        Refresh();
    }

    // the name's colour, whether the field has the focus or not; any other text of the field keeps the game's
    private void UpdateColor()
    {
        if (!Field)
        {
            return;
        }

        bool isName;

        try
        {
            isName = _colorName != null && _colorName();
        }
        catch (Exception)
        {
            isName = false;
        }

        DungeonMakerNameHints.ColorName(Field.textComponent, isName ? Field.text ?? string.Empty : null);
    }

    private void OnSelected(string text)
    {
        _focused = true;
        _closedByPick = false;
        Refresh();
    }

    private void OnDeselected(string text)
    {
        _focused = false;
        DungeonMakerNameHints.Hide(this);

        // A click on the suggestions list never gets here: the input module's deselect is skipped for it
        // (DungeonMakerNameSuggestions.KeepsFocus), so the list shares the field's focus. Any other loss of
        // the focus closes the list.
        DungeonMakerNameSuggestions.Hide(this);
    }

    private void OnDisable()
    {
        _focused = false;
        _caretToEnd = false;
        DungeonMakerNameHints.Hide(this);
        DungeonMakerNameSuggestions.Hide(this);
    }

    // A suggestion was chosen: the field gets the name as if it had been typed (the text's setter raises the
    // field's "value changed" event, and the game takes the value from the field's text in its own handlers, on
    // that event or when the field is left), then the focus back if a click took it, with the caret at the end.
    internal void Apply(string name)
    {
        if (!Field)
        {
            return;
        }

        // A chosen name closes the list until the author edits the text again: the name's own "value changed"
        // would otherwise open it at once on every name that starts the same (seen in game on 9f49bbd6).
        // Both flags are set before the text: its setter raises "value changed" at once, which refreshes the
        // list (Codex's review of 269d166a: with the flag set after, picking Node reopened the list on NodeEast).
        _picked = true;
        _closedByPick = true;
        Field.text = name;
        _picked = false;

        if (Field.isFocused)
        {
            // MoveTextEnd places the caret by the label's character count (TMP_InputField.MoveTextEnd reads
            // m_TextComponent.textInfo.characterCount), which is still the old text's until the label is redrawn:
            // after "ro" it left the caret after the name's second letter (seen in game on 9f49bbd6). The label is
            // redrawn first.
            Field.ForceLabelUpdate();
            Field.MoveTextEnd(false);
        }
        else
        {
            _caretToEnd = true;
            Field.ActivateInputField();
        }
    }

    private void LateUpdate()
    {
        if (_caretToEnd && Field && Field.isFocused)
        {
            _caretToEnd = false;
            Field.ForceLabelUpdate();
            Field.MoveTextEnd(false);
        }
    }

    private void Refresh()
    {
        string text = null;
        var active = Field && (_focused || Field.isFocused);

        // Suggestions first: while a command's target is typed, the names to pick from take the place of any
        // hint. The one name that is already typed in full is no suggestion. With nothing to offer, the
        // missing-name warning below gets its turn, so the two are never shown together.
        if (active && _suggest != null && !_closedByPick)
        {
            System.Collections.Generic.List<string> names = null;
            var typed = (Field.text ?? string.Empty).Trim();

            try
            {
                names = _suggest(typed);
            }
            catch (Exception e)
            {
                Main.Info($"DM name suggestions: {e.Message}");
            }

            if (names is { Count: 1 } && string.Equals(names[0], typed, StringComparison.OrdinalIgnoreCase))
            {
                names = null;
            }

            if (names is { Count: > 0 })
            {
                DungeonMakerNameHints.Hide(this);
                DungeonMakerNameSuggestions.Show(this, names);

                return;
            }
        }

        DungeonMakerNameSuggestions.Hide(this);

        if (active && _hint != null)
        {
            try
            {
                text = _hint((Field.text ?? string.Empty).Trim());
            }
            catch (Exception e)
            {
                // a hint must never get in the way of typing
                Main.Info($"DM name hints: {e.Message}");
            }
        }

        if (string.IsNullOrEmpty(text))
        {
            DungeonMakerNameHints.Hide(this);
        }
        else
        {
            DungeonMakerNameHints.Show(this, Field, text, _warning, _below);
        }
    }
}

// Sits on a row of the variable picker or of the campaign editor's variable list, and shows the name's hint (see
// DungeonMakerNameHints.ForVariableLineHover) under the row while the pointer is over it. The pointer events reach
// it from the row's own graphics (Unity sends "pointer entered" to the object hit and each of its parents). It
// changes nothing of the row or the campaign, and takes no clicks.
internal sealed class DungeonMakerRowHint : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    internal UserVariableLine Line;
    internal UserCampaign Campaign;

    public void OnPointerEnter(PointerEventData eventData)
    {
        var name = Line ? Line.UserVariable?.InternalName : null;
        var text = string.IsNullOrEmpty(name) ? null : DungeonMakerNamesContext.NameHint(name, Campaign, null);

        if (text == null)
        {
            DungeonMakerNameHints.Hide(this);

            return;
        }

        var label = Line.internalNameLabel ? Line.internalNameLabel.TMP_Text : null;

        DungeonMakerNameHints.Show(this, (RectTransform)transform, label, text, false, true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        DungeonMakerNameHints.Hide(this);
    }

    private void OnDisable()
    {
        DungeonMakerNameHints.Hide(this);
    }
}
