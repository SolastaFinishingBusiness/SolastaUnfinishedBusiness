using SolastaUnfinishedBusiness.Models;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
///     The campaign editor's controls for a campaign's "requires the mod" choice (<see cref="CampaignRequirement" />):
///     a toggle on the editor screen, and a question on the author's first explicit save of a campaign that has
///     variables named like the mod's campaign commands and no recorded choice yet.
/// </summary>
internal static class CampaignRequirementUi
{
    private const string ToggleName = "FbRequiresModHeader";
    private const string DontAskName = "FbRequiresModDontAsk";
    private const string MapHeaderPath = "LeftPanel/CampaignMapGroup/HasCampaignMapHeader";

    internal const string ToggleTitle = "UI/&CampaignRequiresModTitle";
    internal const string ToggleDescription = "UI/&CampaignRequiresModDescription";
    internal const string PromptTitle = "Message/&CampaignRequiresModPromptTitle";
    internal const string PromptDescription = "Message/&CampaignRequiresModPromptDescription";
    internal const string DontAskTitle = "UI/&CampaignRequiresModDontAsk";

    private static Toggle _toggle;

    // set while the question is open, and while the save it led to runs, so that save doesn't ask again
    private static bool _asking;

    // The editor's "has a campaign map" row (a toggle and its label) is copied for both controls, so they look and
    // behave like the editor's own. Returns the copy, or null when the editor's layout isn't the expected one.
    private static GameObject CopyMapHeader(UserCampaignEditorScreen screen, Transform parent, string name)
    {
        var source = screen.transform.Find(MapHeaderPath);

        if (!source || !source.GetComponentInChildren<Toggle>(true))
        {
            return null;
        }

        var copy = Object.Instantiate(source.gameObject, parent, false);

        copy.name = name;

        var toggle = copy.GetComponentInChildren<Toggle>(true);

        // Instantiate also copies the editor's persistent listener (OnCampaignMapToggleCb); a new event drops it.
        toggle.onValueChanged = new Toggle.ToggleEvent();

        return copy;
    }

    private static void SetTexts(GameObject header, string title, string tooltip)
    {
        foreach (var label in header.GetComponentsInChildren<GuiLabel>(true))
        {
            label.Text = title;
        }

        foreach (var guiTooltip in header.GetComponentsInChildren<GuiTooltip>(true))
        {
            guiTooltip.Content = tooltip;
        }
    }

    internal static void OnEditorShown(UserCampaignEditorScreen screen)
    {
        if (!_toggle)
        {
            var mapHeader = screen.transform.Find(MapHeaderPath);
            var header = mapHeader ? CopyMapHeader(screen, mapHeader.parent, ToggleName) : null;

            if (!header)
            {
                Main.Error("Campaign editor layout not as expected; the Requires toggle is not shown.");
                return;
            }

            // Same row as the campaign map toggle, which takes the left third: this one starts after it.
            var rect = (RectTransform)header.transform;

            rect.anchorMin = new Vector2(0.36f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.offsetMin = new Vector2(0f, rect.offsetMin.y);
            rect.offsetMax = new Vector2(0f, rect.offsetMax.y);

            SetTexts(header, ToggleTitle, ToggleDescription);
            _toggle = header.GetComponentInChildren<Toggle>(true);
            _toggle.onValueChanged.AddListener(value => OnToggled(screen, value));
        }

        Refresh(screen);
    }

    // The editor's current campaign is the head of its undo list, so the toggle reads it again on every refresh
    // (undo and redo replace it with a clone that carries its own choice).
    internal static void Refresh(UserCampaignEditorScreen screen)
    {
        if (_toggle)
        {
            _toggle.SetIsOnWithoutNotify(CampaignRequirement.Get(screen.UserCampaign) == true);
        }
    }

    private static void OnToggled(UserCampaignEditorScreen screen, bool value)
    {
        if (!screen || screen.UserCampaign == null)
        {
            return;
        }

        // Records an undo step and marks the campaign modified, as the editor's own controls do.
        screen.NotifyBeforeModification();
        CampaignRequirement.Set(screen.UserCampaign, value);
        screen.RefreshNow();
    }

    // UserCampaignEditorScreen.OnSaveCb, before it runs. Returns false when the question was opened instead; the
    // answer then records the choice and runs the save again.
    internal static bool BeforeSave(UserCampaignEditorScreen screen)
    {
        if (_asking || !CampaignRequirement.ShouldPromptOnAuthorSave(screen.UserCampaign))
        {
            return true;
        }

        _asking = true;

        Gui.GuiService.ShowMessage(
            MessageModal.Severity.Informative1,
            PromptTitle,
            // Two blank lines leave room under the text for the row added by AddDontAsk.
            Gui.Localize(PromptDescription) + "\n\n",
            "Message/&MessageYesTitle",
            "Message/&MessageNoTitle",
            () => Answer(screen, true),
            () => Answer(screen, false));

        AddDontAsk(screen);

        return false;
    }

    private static Toggle _dontAsk;
    private static GameObject _dontAskHeader;

    // The game's message window has only its two buttons, so a copy of the editor's toggle row is put in the
    // window's bottom-left corner for this question and removed with the answer.
    private static void AddDontAsk(UserCampaignEditorScreen screen)
    {
        var modal = Gui.GuiService.GetScreen<MessageModal>();
        var panel = modal ? modal.transform.Find("MessagePanel") : null;
        var header = panel ? CopyMapHeader(screen, panel, DontAskName) : null;

        if (!header)
        {
            return;
        }

        var rect = (RectTransform)header.transform;

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = new Vector2(0.6f, 0f);
        rect.pivot = Vector2.zero;
        rect.anchoredPosition = new Vector2(24f, 10f);
        rect.sizeDelta = new Vector2(0f, 24f);

        SetTexts(header, DontAskTitle, string.Empty);
        _dontAskHeader = header;
        _dontAsk = header.GetComponentInChildren<Toggle>(true);
        _dontAsk.SetIsOnWithoutNotify(false);
    }

    private static void Answer(UserCampaignEditorScreen screen, bool required)
    {
        if (_dontAsk && _dontAsk.isOn)
        {
            CampaignRequirement.SuppressPrompt = true;
        }

        if (_dontAskHeader)
        {
            Object.Destroy(_dontAskHeader);
        }

        _dontAsk = null;
        _dontAskHeader = null;

        try
        {
            if (!screen || screen.UserCampaign == null)
            {
                return;
            }

            CampaignRequirement.Set(screen.UserCampaign, required);
            Refresh(screen);
            screen.OnSaveCb();
        }
        finally
        {
            _asking = false;
        }
    }
}
