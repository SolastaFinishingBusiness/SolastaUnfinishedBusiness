using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

// The update message's progress bar (UpdateContext's progress message), made only from the game's own UI elements. It
// is a copy of the game's timer
// bar from the multiplayer request window (MultiplayerAnswerModal: its TimeoutGroup frame and the TimeoutGauge Image in
// it, which the game fills through Image.fillAmount), with the percentage centred on it in a copy of the message's own
// text label, so it has the game's art and font.
// It sits at the bottom of the message's text, in room the text leaves for it: the text ends with ReservedLines lines
// holding only a no-break space, so the game's MessageModal.Show sizes the message with that room (it measures the
// label's preferred height once, when the message is shown). The game uses one MessageModal for every message, so the
// bar is removed as soon as the message showing is no longer the progress message (UpdateContext.RefreshProgressMessage).
internal static class UpdateProgressBar
{
    // lines at the end of the message text kept empty for the bar
    internal const int ReservedLines = 2;

    // the bar's height, in lines of the message text, centred in the reserved lines
    private const float HeightInLines = 1.25f;

    // the percentage's colour over the bar's light fill: near black, a little warm, like the game's dark panels
    private static readonly Color DarkColour = new Color32(32, 28, 22, 255);

    private static GameObject _bar;
    private static Image _fill;
    private static GuiLabel _label;
    private static TextMeshProUGUI _text;
    private static Color _lightColour = Color.white;

    // the lines that make the room, added to the end of the message text
    internal static string Room => string.Concat(Enumerable.Repeat("\n ", ReservedLines));

    // the game's timer bar to copy, if its window exists (it is one of the game's screens, loaded with the GUI)
    private static Image Template
    {
        get
        {
            var answerModal = Gui.GuiService?.GetScreen<MultiplayerAnswerModal>();

            return answerModal ? answerModal.timeoutGauge : null;
        }
    }

    internal static bool CanShow => Template && Template.transform.parent;

    // puts a bar into the message now showing, at the bottom of its text; false when the game's bar can't be copied
    internal static bool Attach(MessageModal modal)
    {
        Remove();

        var template = Template;

        if (!template || !template.transform.parent || !modal || !modal.contentLabel)
        {
            return false;
        }

        // copied under an inactive holder so none of the copy's components start up before they are changed
        var holder = new GameObject("FbUpdateProgressHolder");

        holder.SetActive(false);

        var bar = Object.Instantiate(template.transform.parent.gameObject, holder.transform, false);

        bar.name = "FbUpdateProgressBar";

        // the gauge is the copied frame's child with the template's name
        var fillTransform = bar.transform.Find(template.name);

        _fill = fillTransform ? fillTransform.GetComponent<Image>() : null;

        if (!_fill)
        {
            Object.Destroy(holder);

            return false;
        }

        // filled from the left, like a progress bar (the game's timer drains it)
        _fill.type = Image.Type.Filled;
        _fill.fillMethod = Image.FillMethod.Horizontal;
        _fill.fillOrigin = (int)Image.OriginHorizontal.Left;
        _fill.fillAmount = 0f;

        // the percentage: a copy of the message's own label, centred on the bar
        var source = modal.contentLabel;
        var label = Object.Instantiate(source.gameObject, bar.transform, false);

        label.name = "FbUpdateProgressPercent";
        _label = label.GetComponent<GuiLabel>();

        // in the reserved lines at the bottom of the message's label, as wide as the label's text area
        var lineHeight = source.TMP_Text ? source.TMP_Text.fontSize * 1.2f : 24f;
        var height = lineHeight * HeightInLines;
        var rect = (RectTransform)bar.transform;

        bar.transform.SetParent(source.transform, false);
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.sizeDelta = new Vector2(0f, height);
        rect.anchoredPosition = new Vector2(0f, (ReservedLines * lineHeight - height) / 2f);

        // no layout element of the copy may resize it inside the label
        foreach (var layoutElement in bar.GetComponentsInChildren<LayoutElement>(true))
        {
            layoutElement.ignoreLayout = true;
        }

        bar.SetActive(true);
        Object.Destroy(holder);
        _bar = bar;

        // only now, once active: GuiLabel.Awake runs GuiStyleEnforcer.ApplyStyles, which writes the message style's
        // text settings (its top-left alignment among them) over the copy, so settings made before activation would be
        // lost and the percentage would sit at the bar's left end
        CentreLabel(label);

        Main.Info($"Update progress bar: label {((RectTransform)source.transform).rect.size}, " +
                  $"font {(source.TMP_Text ? source.TMP_Text.fontSize : 0f)}, bar height {height}");

        return true;
    }

    // the percentage across the whole bar, centred, on one line. Its colour follows what is under it (Set): the
    // message's light text colour while the bar is less than half full (the text sits over the dark empty part), a
    // dark colour once the fill reaches it. One colour can't do both: in game (b9f3d5b1) the light text with a thin
    // outline nearly vanished on the full bar, and with a thick outline (141b52e7) it read dark on the empty part
    private static void CentreLabel(GameObject label)
    {
        var rect = (RectTransform)label.transform;

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        var text = label.GetComponent<TextMeshProUGUI>();

        if (!text)
        {
            return;
        }

        text.alignment = TextAlignmentOptions.Center;
        text.margin = Vector4.zero;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        _text = text;
        _lightColour = text.color;
    }

    // fraction 0..1 of the bar filled, and the text on it
    internal static void Set(float fraction, string text)
    {
        if (!_bar)
        {
            return;
        }

        _fill.fillAmount = Mathf.Clamp01(fraction);

        if (_label && _label.Text != text)
        {
            _label.Text = text;
        }

        if (_text)
        {
            _text.color = fraction >= 0.5f ? DarkColour : _lightColour;
        }
    }

    internal static void Remove()
    {
        if (_bar)
        {
            Object.Destroy(_bar);
        }

        _bar = null;
        _fill = null;
        _label = null;
        _text = null;
    }
}
