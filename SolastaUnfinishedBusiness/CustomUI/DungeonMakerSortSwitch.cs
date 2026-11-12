using System;
using SolastaUnfinishedBusiness.Patches;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.CustomUI;

// The Default / Alphabetical switch on the three Dungeon Maker screens that list things (issue #42): the campaign
// editor, the variable picker and the Cheats > Campaign Variables panel. All three flip the same saved setting
// (Settings.SortDungeonMakerListsAlphabetically, also in Mod UI > Dungeon Maker), so the choice made on one screen
// is the choice on the others. What the setting does is in Patches/UserListSortPatcher.cs.
//
// The switch is a copy of a button the screen already has, put first in that button's own row, so it gets the
// screen's look and the row's layout places it. Its text says which order is shown now; a click changes the order
// and has the screen draw its list again.
internal static class DungeonMakerSortSwitch
{
    private const string SwitchName = "FbSortSwitch";

    private static string _lastProblem = string.Empty;

    // Adds the switch to a screen if it isn't there yet, and brings its text up to date. Called each time the
    // screen is shown, because the setting may have been changed on another screen or in the mod's menu.
    // sourcePath: the button to copy, from the screen's root. redraw: has the screen fill its list again.
    internal static void Ensure(Transform screen, string sourcePath, Action redraw)
    {
        var source = screen ? screen.Find(sourcePath) : null;

        if (!source || !source.GetComponent<Button>())
        {
            var problem = $"Dungeon Maker sort switch: no button at '{sourcePath}' on {(screen ? screen.name : "?")}.";

            // a game update that moved the button: say so once, and leave the screen as it is
            if (problem != _lastProblem)
            {
                _lastProblem = problem;
                Main.Error(problem);
            }

            return;
        }

        var row = source.parent;
        var existing = row.Find(SwitchName);
        var button = existing ? existing.GetComponent<Button>() : Add(row, source.gameObject);

        // a fresh event each time: the redraw action belongs to this showing of the screen
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() =>
        {
            Main.Settings.SortDungeonMakerListsAlphabetically = !Main.Settings.SortDungeonMakerListsAlphabetically;
            SetText(button);

            try
            {
                redraw();
            }
            catch (Exception e)
            {
                Main.Error(e);
            }
        });

        SetText(button);
    }

    // A copy of one of the screen's own buttons. It is copied under an inactive holder so none of the copy's
    // components start up before they are changed (as the mod's other copied buttons are).
    private static Button Add(Transform row, GameObject source)
    {
        var holder = new GameObject("FbHolder");

        holder.SetActive(false);

        var copy = Object.Instantiate(source, holder.transform, false);

        copy.name = SwitchName;

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

        var button = copy.GetComponent<Button>();

        button.interactable = true;

        // the original may be faded or blocked at this moment (the editor's buttons are, until something changes)
        if (copy.TryGetComponent<CanvasGroup>(out var group))
        {
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
        }

        copy.transform.SetParent(row, false);
        copy.transform.SetAsFirstSibling();
        copy.SetActive(true);
        Object.Destroy(holder);

        return button;
    }

    private static void SetText(Button button)
    {
        var label = button.transform.Find("Label")?.GetComponent<GuiLabel>();

        if (label)
        {
            label.Text = Gui.Localize(UserListSorter.Alphabetical
                ? "ModUi/&DungeonMakerSortAlphabetical"
                : "ModUi/&DungeonMakerSortDefault");
        }
    }
}
