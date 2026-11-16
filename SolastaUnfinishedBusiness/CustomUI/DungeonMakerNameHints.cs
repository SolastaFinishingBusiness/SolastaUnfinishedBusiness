using System;
using System.Linq;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
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
//     the field, and it follows the field when the list it is in scrolls.
//   - It is shown only while the field has the keyboard focus and has something to say, is worked out again at
//     every key, and goes when the field loses the focus, is hidden, or the text is fine.
//   - It takes no clicks (nothing in it is a raycast target), and it changes nothing of the campaign.
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

    private static GameObject _panel;
    private static RectTransform _rect;
    private static TextMeshProUGUI _label;
    private static Color _textColor = Color.white;
    private static DungeonMakerFieldHint _owner;
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

        DungeonMakerFieldHint.Attach(line.variableField, false, text =>
            line.UserGadgetParameterValue?.GadgetParameterDescription?.Type ==
            GadgetBlueprintDefinitions.Type.Variable
                ? DungeonMakerNamesContext.NameHint(text, line.UserCampaign, line.UserLocation)
                : null);

        DungeonMakerFieldHint.Attach(line.stringField, true, text =>
        {
            if (line.UserGadgetParameterValue?.GadgetParameterDescription?.Type !=
                GadgetBlueprintDefinitions.Type.VariableValueString)
            {
                return null;
            }

            // the variable this gadget writes: its "variable" parameter
            var variable = line.UserGadget?.ParameterValues?
                .FirstOrDefault(x =>
                    x?.GadgetParameterDescription?.Type == GadgetBlueprintDefinitions.Type.Variable)?
                .StringValue;

            return DungeonMakerNamesContext.ValueHint(variable, text, line.UserCampaign, line.UserLocation);
        });
    }

    // UserParameterItem.CommondBind: one parameter of an action or a condition in the dialogue and quest editors
    internal static void ForParameterItem(UserParameterItem item)
    {
        if (!item)
        {
            return;
        }

        DungeonMakerFieldHint.Attach(item.variableField, false, text =>
            item.parameterType is UserParameterDefinitions.Type.VariableOperationVariable
                or UserParameterDefinitions.Type.VariableTestVariable
                ? DungeonMakerNamesContext.NameHint(text, item.UserCampaign, null)
                : null);

        // a Variable Operation action keeps its variable in its first text (see DungeonMakerNamesContext)
        DungeonMakerFieldHint.Attach(item.stringField, true, text =>
            item.parameterType == UserParameterDefinitions.Type.VariableOperationValueString &&
            item.functorParametersDescription != null
                ? DungeonMakerNamesContext.ValueHint(
                    item.functorParametersDescription.StringParameter, text, item.UserCampaign, null)
                : null);
    }

    #endregion

    #region The panel

    internal static void Show(DungeonMakerFieldHint owner, TMP_InputField field, string text, bool warning)
    {
        var target = field ? field.transform as RectTransform : null;
        var nearest = target ? target.GetComponentInParent<Canvas>() : null;
        var canvas = nearest ? nearest.rootCanvas : null;

        if (!canvas)
        {
            return;
        }

        EnsurePanel(canvas.transform, field.textComponent);

        _owner = owner;
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
        Place(target);
    }

    internal static void Hide(DungeonMakerFieldHint owner)
    {
        if (_panel && ReferenceEquals(_owner, owner))
        {
            _owner = null;
            _panel.SetActive(false);
        }
    }

    // Above the field, centred on it and kept inside the screen; below it when there is no room above. Worked out
    // in the canvas's own space, so the canvas's scale and render mode don't matter.
    private static void Place(RectTransform target)
    {
        if (!_rect || _rect.parent is not RectTransform canvas || !target)
        {
            return;
        }

        target.GetWorldCorners(Corners);

        // corners: bottom left, top left, top right, bottom right
        var top = canvas.InverseTransformPoint((Corners[1] + Corners[2]) / 2f);
        var bottom = canvas.InverseTransformPoint((Corners[0] + Corners[3]) / 2f);
        var size = _rect.sizeDelta;
        var area = canvas.rect;
        var above = top.y + Gap + size.y <= area.yMax - Margin;
        var half = size.x / 2f;
        var x = area.width > size.x + (2 * Margin)
            ? Mathf.Clamp(top.x, area.xMin + half + Margin, area.xMax - half - Margin)
            : top.x;

        _rect.pivot = new Vector2(0.5f, above ? 0f : 1f);
        _rect.localPosition = new Vector3(x, above ? top.y + Gap : bottom.y - Gap, 0f);
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

        _panel = new GameObject(PanelName, typeof(RectTransform), typeof(Image), typeof(Canvas));
        _rect = _panel.GetComponent<RectTransform>();
        _rect.SetParent(canvas, false);
        _rect.anchorMin = _rect.anchorMax = new Vector2(0.5f, 0.5f);
        _rect.localScale = Vector3.one;

        // above every screen and window of the editor, whatever their order in the canvas
        var order = _panel.GetComponent<Canvas>();

        order.overrideSorting = true;
        order.sortingOrder = 32000;

        var background = _panel.GetComponent<Image>();

        background.raycastTarget = false;
        background.color = new Color(0.06f, 0.06f, 0.08f, 0.96f);

        var textObject = new GameObject("Text", typeof(RectTransform));
        var textRect = textObject.GetComponent<RectTransform>();

        textRect.SetParent(_rect, false);
        textRect.anchorMin = textRect.anchorMax = textRect.pivot = new Vector2(0.5f, 0.5f);
        textRect.anchoredPosition = Vector2.zero;

        _label = textObject.AddComponent<TextMeshProUGUI>();
        _label.raycastTarget = false;
        _label.enableWordWrapping = true;
        _label.richText = true;
        _label.alignment = TextAlignmentOptions.TopLeft;
        _label.fontSize = 18f;

        if (fieldText)
        {
            _label.font = fieldText.font;
            _label.fontSize = Mathf.Max(16f, fieldText.fontSize);
        }

        StyleLikeTooltip(background);
        _panel.AddComponent<DungeonMakerHintFollower>();
        _panel.SetActive(false);
    }

    // Takes the look of the game's tooltip.
    //
    // The background: the game's tooltip panel prefab (TooltipPanelSimple for the default class) draws it in
    // layers, its direct children in this order: Shadow (a soft glow around it), BackgroundBlur (the panel itself)
    // and Frame (its border), each a stretched Image (read from the prefab, 2026-11-16). Each of those layers is
    // copied under the hint's panel, in the same order and with the same anchors, sprite, material and colour, and
    // the panel's own plain box is then made transparent. (The first version took only the prefab's first Image
    // with a sprite, which is the Shadow: the hint was drawn as a near-invisible glow, seen in game on 02a462d3.)
    //
    // The text: the panel prefab holds no text of its own (its rows are added when a tooltip is shown), so the
    // font, material and colour come from the class's description row (the feature prefab with a
    // TooltipFeatureDescription, whose label is the tooltip's body text). The first version found no text in the
    // panel prefab and so kept the input field's font, the editor's serif capitals.
    //
    // Whatever is missing keeps the plain look: a dark box and the field's font.
    private static void StyleLikeTooltip(Image background)
    {
        try
        {
            if (ServiceRepository.GetService<ITooltipService>() is not GuiManager
                {
                    tooltipClassDefinitions: { } classes
                })
            {
                return;
            }

            var definition = classes.TryGetValue(GuiManager.DefaultTooltipClass, out var standard) && standard
                ? standard
                : classes.Values.FirstOrDefault(x => x && x.TooltipPanelPrefab);

            if (!definition)
            {
                return;
            }

            var prefab = definition.TooltipPanelPrefab;
            var layers = 0;

            if (prefab)
            {
                foreach (Transform child in prefab.transform)
                {
                    // only the picture layers; the features table and anything else of the prefab stay out
                    if (child.GetComponent<Image>() is not { } picture || !picture.sprite ||
                        child.GetComponentsInChildren<Component>(true).Any(x =>
                            x is not RectTransform and not CanvasRenderer and not Image))
                    {
                        continue;
                    }

                    var copy = UnityEngine.Object.Instantiate(child.gameObject, _rect, false);

                    copy.name = child.name;
                    copy.GetComponent<Image>().raycastTarget = false;
                    copy.transform.SetSiblingIndex(layers++);
                }
            }

            if (layers > 0)
            {
                background.color = Color.clear;
            }

            var text = definition.TooltipFeatures?
                .Select(x => x.featurePrefab ? x.featurePrefab.GetComponent<TooltipFeatureDescription>() : null)
                .Where(x => x)
                .Select(x => x.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(t => t.font))
                .FirstOrDefault(x => x);

            if (text)
            {
                _label.font = text.font;
                _label.fontSharedMaterial = text.fontSharedMaterial;
                _label.fontSize = text.fontSize;
                _label.fontStyle = text.fontStyle;
                _textColor = text.color;
            }

            Main.Info($"DM name hints: tooltip look taken from '{(prefab ? prefab.name : "?")}' ({layers} " +
                      $"background layers) and font '{(text ? text.font.name : "the field's")}'.");
        }
        catch (Exception e)
        {
            Main.Info($"DM name hints: the game's tooltip look could not be taken, a plain one is used: {e.Message}");
        }
    }

    // keeps the panel on its field while it is shown, and takes it away when the field goes
    private sealed class DungeonMakerHintFollower : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (!_owner || !_owner.isActiveAndEnabled || !_owner.Field)
            {
                _owner = null;
                gameObject.SetActive(false);

                return;
            }

            Place(_owner.Field.transform as RectTransform);
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

    internal TMP_InputField Field { get; private set; }

    // Puts a hint on this field, or gives the field's existing one its new meaning (the editor reuses its
    // parameter lines for other parameters).
    internal static void Attach(TMP_InputField field, bool warning, Func<string, string> hint)
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

        if (component._focused)
        {
            component.Refresh();
        }
    }

    private void OnChanged(string text)
    {
        Refresh();
    }

    private void OnSelected(string text)
    {
        _focused = true;
        Refresh();
    }

    private void OnDeselected(string text)
    {
        _focused = false;
        DungeonMakerNameHints.Hide(this);
    }

    private void OnDisable()
    {
        _focused = false;
        DungeonMakerNameHints.Hide(this);
    }

    private void Refresh()
    {
        string text = null;

        if (Field && (_focused || Field.isFocused) && _hint != null)
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
            DungeonMakerNameHints.Show(this, Field, text, _warning);
        }
    }
}
