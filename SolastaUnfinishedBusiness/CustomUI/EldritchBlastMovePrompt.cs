using System.Collections.Generic;
using SolastaUnfinishedBusiness.Models;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static SolastaUnfinishedBusiness.CustomUI.ReactionRequestEldritchBlastMove;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
///     The controls of the Eldritch Blast move prompt (issue #37), built into the game's reaction item when it shows
///     a <see cref="ReactionRequestEldritchBlastMove" />:
///     <list type="bullet">
///         <item>
///             a slider with the stops 0, 5 and 10 feet for each invocation that takes part in the beam, copied from
///             the game's settings screen;
///         </item>
///         <item>a button that swaps the order, when both take part;</item>
///         <item>Submit and Skip, and below them Submit for All and Skip for All.</item>
///     </list>
///     The game's own React and Pass buttons are hidden while this shows. Every button answers the request through
///     the game's command service with the whole answer packed into the sub-option number, which is how it reaches
///     every computer.
///     <para>
///         Reaction items are reused for later, unrelated reactions, so everything here lives under one object that
///         is destroyed whenever the item is bound to anything else, or unbound (<see cref="Remove" />).
///     </para>
/// </summary>
internal static class EldritchBlastMovePrompt
{
    private const string RootName = "FbEldritchBlastMoveControls";

    // the active invocation icon's tint in the Invocations popup, used for the slider's filled part
    private static readonly Color Accent = new(0.961f, 0.706f, 0.525f, 1f);
    private static readonly Color Track = new(0.16f, 0.17f, 0.18f, 1f);
    private static readonly Color Knob = new(0.93f, 0.93f, 0.93f, 1f);

    /// <summary>Builds the controls into <paramref name="item" /> for <paramref name="request" />.</summary>
    internal static void Show(CharacterReactionItem item, ReactionRequestEldritchBlastMove request)
    {
        Remove(item);

        var stockButtons = item.ReactButton.transform.parent;
        var root = new GameObject(RootName, typeof(RectTransform));

        root.transform.SetParent(item.transform, false);
        root.transform.SetSiblingIndex(stockButtons.GetSiblingIndex());

        var rootLayout = root.AddComponent<VerticalLayoutGroup>();

        rootLayout.spacing = 6f;
        rootLayout.padding = new RectOffset(10, 10, 4, 8);
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        var state = new State { Item = item, Request = request, Root = root };
        var labelSource = item.transform.Find("CharacterName");

        if (request.HasPush)
        {
            state.Push = AddSliderRow(root.transform, labelSource, PushTitle, state);
        }

        if (request.HasPull)
        {
            state.Pull = AddSliderRow(root.transform, labelSource, PullTitle, state);
        }

        if (request.HasPush && request.HasPull)
        {
            var row = AddRow(root.transform);

            state.OrderButton = AddButton(row, item.ReactButton.gameObject, string.Empty, () =>
            {
                state.PushFirst = !state.PushFirst;
                Refresh(state);
            });
        }

        var first = AddRow(root.transform);

        state.Submit = AddButton(first, item.ReactButton.gameObject,
            Gui.Localize("Reaction/&EldritchBlastMoveSubmit"), () => Answer(state, ButtonSubmit));
        AddButton(first, item.PassButton.gameObject,
            Gui.Localize("Reaction/&EldritchBlastMoveSkip"), () => Answer(state, ButtonSkip));

        var second = AddRow(root.transform);

        state.SubmitForAll = AddButton(second, item.ReactButton.gameObject,
            Gui.Localize("Reaction/&EldritchBlastMoveSubmitForAll"), () => Answer(state, ButtonSubmitForAll));
        AddButton(second, item.PassButton.gameObject,
            Gui.Localize("Reaction/&EldritchBlastMoveSkipForAll"), () => Answer(state, ButtonSkipForAll));

        stockButtons.gameObject.SetActive(false);
        Refresh(state);
        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)item.transform);
    }

    /// <summary>Takes the controls out of <paramref name="item" /> and shows the game's own buttons again.</summary>
    internal static void Remove(CharacterReactionItem item)
    {
        var root = item.transform.Find(RootName);

        if (!root)
        {
            return;
        }

        // DestroyImmediate, so the item's layout no longer counts it in the same frame
        Object.DestroyImmediate(root.gameObject);
        item.ReactButton.transform.parent.gameObject.SetActive(true);
    }

    private static int Cells(Slider slider)
    {
        return slider ? Mathf.RoundToInt(slider.value) : 0;
    }

    // Sends the answer. Every button is a "validated" answer: which button it was is part of the number.
    // It follows what the game's own React button does, step by step (CharacterReactionItem.OnReactCb, then
    // ReactionModal.OnReact), with the packed number in place of the selected sub-option: mark the item as answered,
    // send the command, and let the modal check whether anything is still waiting. Without the first and the last
    // step the modal would stay up, empty, and its timer could answer the same request a second time.
    private static void Answer(State state, int button)
    {
        var request = state.Request;
        var item = state.Item;

        if (!state.Root || !item || item.CurrentState != CharacterReactionItem.ReactionState.Pending)
        {
            return;
        }

        var packed = Pack(Cells(state.Push?.Slider), Cells(state.Pull?.Slider), state.PushFirst, button);

        // one answer only: the controls go at once, as the game's own buttons do
        state.Root.SetActive(false);
        item.CurrentState = CharacterReactionItem.ReactionState.Reacted;

        if (!request.Processed)
        {
            ServiceRepository.GetService<ICommandService>()?.ProcessReactionRequest(request, true, packed);
        }

        var modal = Gui.GuiService?.GetScreen<ReactionModal>();

        if (modal)
        {
            modal.CheckPanelRelevance();
        }
    }

    private static void Refresh(State state)
    {
        foreach (var row in new[] { state.Push, state.Pull })
        {
            if (row != null)
            {
                row.Value.text = Gui.FormatDistance(Cells(row.Slider));
            }
        }

        if (state.OrderButton)
        {
            SetButtonText(state.OrderButton, Gui.Format(
                "Reaction/&EldritchBlastMoveOrder",
                state.PushFirst ? PushTitle : PullTitle,
                state.PushFirst ? PullTitle : PushTitle));
        }

        // nothing to submit when neither moves: only the two Skip buttons can be pressed
        var anything = Cells(state.Push?.Slider) + Cells(state.Pull?.Slider) > 0;

        SetEnabled(state.Submit, anything);
        SetEnabled(state.SubmitForAll, anything);
    }

    private static void SetEnabled(Button button, bool enabled)
    {
        if (!button)
        {
            return;
        }

        button.interactable = enabled;

        var group = button.GetComponent<CanvasGroup>();

        if (!group)
        {
            group = button.gameObject.AddComponent<CanvasGroup>();
        }

        group.alpha = enabled ? 1f : 0.35f;
        group.blocksRaycasts = enabled;
    }

    private static Transform AddRow(Transform parent)
    {
        var row = new GameObject("Row", typeof(RectTransform));

        row.transform.SetParent(parent, false);

        var layout = row.AddComponent<HorizontalLayoutGroup>();

        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        return row.transform;
    }

    // A name, a slider with three stops, and the chosen distance.
    private static SliderRow AddSliderRow(Transform parent, Transform labelSource, string title, State state)
    {
        var row = AddRow(parent);
        var name = AddLabel(row, labelSource, title, 150f, TextAlignmentOptions.MidlineLeft);

        name.enableWordWrapping = false;
        name.overflowMode = TextOverflowModes.Ellipsis;

        var sliderObject = CreateSlider(row);

        var sliderLayout = sliderObject.GetComponent<LayoutElement>();

        if (!sliderLayout)
        {
            sliderLayout = sliderObject.AddComponent<LayoutElement>();
        }

        sliderLayout.minWidth = 110f;
        sliderLayout.preferredWidth = 130f;
        sliderLayout.flexibleWidth = 1f;
        sliderLayout.minHeight = 24f;
        sliderLayout.preferredHeight = 24f;

        var slider = sliderObject.GetComponent<Slider>();

        slider.minValue = 0f;
        slider.maxValue = EldritchBlastMoveContext.MaxCells;
        slider.wholeNumbers = true;
        slider.value = EldritchBlastMoveContext.MaxCells;
        slider.onValueChanged.AddListener(_ => Refresh(state));

        var value = AddLabel(row, labelSource, string.Empty, 60f, TextAlignmentOptions.MidlineRight);

        value.enableWordWrapping = false;

        return new SliderRow { Slider = slider, Value = value };
    }

    // The slider of the game's own settings screen (a gauge and a knob), so the prompt looks like the rest of the
    // game. Each settings tab holds the prefab of a whole slider row; the brightness row of the colour calibration
    // screen is another copy of it. Only the row's "Slider" child is kept. If neither can be found, a plain Unity
    // slider with flat colours is used, so the prompt always works.
    private static GameObject CreateSlider(Transform parent)
    {
        var source = FindGameSlider();

        if (!source)
        {
            var plain = DefaultControls.CreateSlider(new DefaultControls.Resources());

            plain.name = "Slider";
            plain.transform.SetParent(parent, false);
            Paint(plain.transform, "Background", Track);
            Paint(plain.transform, "Fill Area/Fill", Accent);
            Paint(plain.transform, "Handle Slide Area/Handle", Knob);

            return plain;
        }

        // copied under an inactive holder so none of the copy's components start up before they are changed
        var holder = new GameObject("FbHolder");

        holder.SetActive(false);

        var copy = Object.Instantiate(source.gameObject, holder.transform, false);

        copy.name = "Slider";

        // this component ties the slider to a row of the settings screen
        foreach (var control in copy.GetComponentsInChildren<GuiSettingControl>(true))
        {
            Object.DestroyImmediate(control);
        }

        // a fresh event: the original's is wired in the prefab to the settings row that owned it
        copy.GetComponent<Slider>().onValueChanged = new Slider.SliderEvent();

        copy.transform.SetParent(parent, false);
        copy.SetActive(true);
        Object.Destroy(holder);

        return copy;
    }

    private static Slider FindGameSlider()
    {
        // includes objects that aren't in the scene or are inactive, such as the settings screen while it is closed
        foreach (var panel in Resources.FindObjectsOfTypeAll<SettingsTabPanel>())
        {
            var prefab = panel ? panel.settingSliderPrefab : null;
            var item = prefab ? prefab.GetComponent<SettingSliderItem>() : null;

            if (item && item.Slider)
            {
                return item.Slider;
            }
        }

        var calibration = Gui.GuiService?.GetScreen<ColorCalibrationModal>();

        return calibration && calibration.brightnessSlider ? calibration.brightnessSlider.Slider : null;
    }

    private static void Paint(Transform parent, string path, Color color)
    {
        var image = parent.Find(path)?.GetComponent<Image>();

        if (image)
        {
            image.sprite = null;
            image.color = color;
        }
    }

    // A text in the game's own font, copied from the item's character name.
    private static TMP_Text AddLabel(
        Transform parent, Transform source, string text, float width, TextAlignmentOptions alignment)
    {
        var copy = Object.Instantiate(source.gameObject, parent, false);

        copy.name = "Label";
        copy.SetActive(true);

        var label = copy.GetComponent<GuiLabel>();

        label.Text = text;

        var tmp = label.TMP_Text;

        tmp.alignment = alignment;
        tmp.fontSize = 16f;

        var layout = copy.GetComponent<LayoutElement>();

        if (!layout)
        {
            layout = copy.AddComponent<LayoutElement>();
        }

        layout.minWidth = width;
        layout.preferredWidth = width;
        layout.flexibleWidth = 0f;
        layout.minHeight = 24f;
        layout.preferredHeight = 24f;

        return tmp;
    }

    // A copy of one of the item's own buttons with a new text and click. It is copied under an inactive holder so
    // none of the copy's components start up before they are changed (as the enemy-turn speed-up button is).
    private static Button AddButton(Transform parent, GameObject source, string text, UnityEngine.Events.UnityAction click)
    {
        var holder = new GameObject("FbHolder");

        holder.SetActive(false);

        var copy = Object.Instantiate(source, holder.transform, false);

        copy.name = "Button";

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

        // the copied tooltips describe the game's React and Pass
        foreach (var tooltip in copy.GetComponentsInChildren<GuiTooltip>(true))
        {
            tooltip.Clear();
        }

        // the resource cost shown on the React button doesn't apply
        var cost = copy.transform.Find("ResourceCostGroup");

        if (cost)
        {
            cost.gameObject.SetActive(false);
        }

        var button = copy.GetComponent<Button>();

        // a fresh event: the original's clicks are wired in the prefab to the item's own React and Pass
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(click);
        button.interactable = true;

        var layout = copy.GetComponent<LayoutElement>();

        if (!layout)
        {
            layout = copy.AddComponent<LayoutElement>();
        }

        layout.flexibleWidth = 1f;
        layout.minHeight = 34f;
        layout.preferredHeight = 34f;

        copy.transform.SetParent(parent, false);
        copy.SetActive(true);
        Object.Destroy(holder);

        SetButtonText(button, text);

        return button;
    }

    private static void SetButtonText(Button button, string text)
    {
        var label = button.transform.Find("Label")?.GetComponent<GuiLabel>();

        if (label)
        {
            label.Text = text;
        }
    }

    private sealed class SliderRow
    {
        internal Slider Slider;
        internal TMP_Text Value;
    }

    // what one shown prompt needs to remember between clicks; it lives only in the controls' own listeners
    private sealed class State
    {
        internal CharacterReactionItem Item;
        internal Button OrderButton;
        internal SliderRow Pull;
        internal SliderRow Push;
        internal bool PushFirst = true;
        internal ReactionRequestEldritchBlastMove Request;
        internal GameObject Root;
        internal Button Submit;
        internal Button SubmitForAll;
    }
}
