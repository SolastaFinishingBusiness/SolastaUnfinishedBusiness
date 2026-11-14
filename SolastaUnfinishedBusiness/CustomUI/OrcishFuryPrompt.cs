using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.CustomUI;

/// <summary>
///     The controls of Orcish Fury's reaction (#62), built into the game's reaction item when it shows a
///     <see cref="ReactionRequestOrcishFury" /> (user, 2026-11-15: "Let the player hit any creature of their choice in
///     range. Make it a list selection (one button per enemy) or to pass to not do the attack"):
///     <list type="bullet">
///         <item>one button per creature a weapon can reach, with its name and distance, two to a row;</item>
///         <item>the game's own Pass button, kept as it is, which declines (and is what the timer presses).</item>
///     </list>
///     The game's React button is hidden: it would attack the first creature without saying which. A creature's
///     button answers the request through the game's command service with the creature's index as the sub-option,
///     which is how the choice reaches every computer.
///     <para>
///         Reaction items are reused for later, unrelated reactions, so everything here lives under one object that is
///         destroyed whenever the item is bound to anything else, or unbound (<see cref="Remove" />).
///     </para>
/// </summary>
internal static class OrcishFuryPrompt
{
    private const string RootName = "FbOrcishFuryTargets";

    /// <summary>Builds the creature buttons into <paramref name="item" /> for <paramref name="request" />.</summary>
    internal static void Show(CharacterReactionItem item, ReactionRequestOrcishFury request)
    {
        Remove(item);

        var stockButtons = item.ReactButton.transform.parent;
        var root = new GameObject(RootName, typeof(RectTransform));

        root.transform.SetParent(item.transform, false);
        root.transform.SetSiblingIndex(stockButtons.GetSiblingIndex());

        var rootLayout = root.AddComponent<VerticalLayoutGroup>();

        rootLayout.spacing = 6f;
        rootLayout.padding = new RectOffset(10, 10, 4, 4);
        rootLayout.childControlWidth = true;
        rootLayout.childControlHeight = true;
        rootLayout.childForceExpandWidth = true;
        rootLayout.childForceExpandHeight = false;

        Transform row = null;

        for (var index = 0; index < request.Targets.Count; index++)
        {
            if (index % 2 == 0)
            {
                row = EldritchBlastMovePrompt.AddRow(root.transform);
            }

            var target = request.Targets[index];
            var name = new GuiCharacter(target.Character).Name;
            var text = target.DistanceCells == 1
                ? Gui.Format("Reaction/&ReactionAttackOrcishFuryTargetCell", name)
                : Gui.Format("Reaction/&ReactionAttackOrcishFuryTargetCells", name, target.DistanceCells.ToString());
            var option = index;

            EldritchBlastMovePrompt.AddButton(row, item.ReactButton.gameObject, text, () => Answer(item, request, option));
        }

        // only React goes: Pass stays the game's own, with its own handling (and the timer's)
        item.ReactButton.gameObject.SetActive(false);
        LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)item.transform);
    }

    /// <summary>Takes the buttons out of <paramref name="item" /> and shows the game's React button again.</summary>
    internal static void Remove(CharacterReactionItem item)
    {
        var root = item.transform.Find(RootName);

        if (!root)
        {
            return;
        }

        // DestroyImmediate, so the item's layout no longer counts it in the same frame
        Object.DestroyImmediate(root.gameObject);
        item.ReactButton.gameObject.SetActive(true);
    }

    // Sends the answer for the creature at `option`. It follows what the game's own React button does
    // (CharacterReactionItem.OnReactCb, then ReactionModal.OnReact), with the creature's index as the sub-option: mark
    // the item as answered, send the command, and let the modal check whether anything is still waiting. Without the
    // first and the last step the modal would stay up, empty, and its timer could answer the request a second time.
    // (The same steps as the Eldritch Blast move prompt's answer.)
    private static void Answer(CharacterReactionItem item, ReactionRequestOrcishFury request, int option)
    {
        var root = item ? item.transform.Find(RootName) : null;

        if (!root || item.CurrentState != CharacterReactionItem.ReactionState.Pending)
        {
            return;
        }

        // one answer only: the buttons go at once, as the game's own do
        root.gameObject.SetActive(false);
        item.CurrentState = CharacterReactionItem.ReactionState.Reacted;

        if (!request.Processed)
        {
            ServiceRepository.GetService<ICommandService>()?.ProcessReactionRequest(request, true, option);
        }

        var modal = Gui.GuiService?.GetScreen<ReactionModal>();

        if (modal)
        {
            modal.CheckPanelRelevance();
        }
    }
}
