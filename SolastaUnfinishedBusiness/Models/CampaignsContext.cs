using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.Api;
using SolastaUnfinishedBusiness.Api.GameExtensions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using SolastaUnfinishedBusiness.Behaviors;
using SolastaUnfinishedBusiness.Builders;
using SolastaUnfinishedBusiness.Builders.Features;
using SolastaUnfinishedBusiness.Patches;
using TA;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.InputSystem;
using UnityEngine.Playables;
using UnityEngine.UI;
using static RuleDefinitions;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.GadgetBlueprints;
using static SolastaUnfinishedBusiness.Api.DatabaseHelper.ItemDefinitions;
using Object = UnityEngine.Object;

namespace SolastaUnfinishedBusiness.Models;

internal static class CampaignsContext
{
    internal static bool IsVttCameraEnabled;

    internal static readonly string[] HighContrastColorStrings =
    [
        "#FFFFFF", // use white to represent default color in mod UI
        "#FF4040", "#40C040", "#8080FF",
        "#00FFFF", "#FF40FF", "#FFFF00"
    ];

    internal static readonly Color[] HighContrastColors =
    [
        new(0.110f, 0.311f, 0.287f, 1.000f),
        Color.red, Color.green, Color.blue,
        Color.cyan, Color.magenta, Color.yellow
    ];

    internal static readonly string[] GridColorStrings =
    [
        "#000000", "#FFFFFF",
        "#FF4040", "#40C040", "#8080FF",
        "#00FFFF", "#FF40FF", "#FFFF00"
    ];

    internal static readonly Color[] GridColors =
    [
        Color.black, Color.white,
        Color.red, Color.green, Color.blue,
        Color.cyan, Color.magenta, Color.yellow
    ];

    internal static Color GetGridColor(bool isHighlighted)
    {
        var selectedColor = GridColors[Main.Settings.GridSelectedColor];

        return new Color(
            selectedColor.r,
            selectedColor.g,
            selectedColor.b,
            isHighlighted ? 1f : 0.2f
        );
    }

    private static readonly int[][][] FormationGridSetTemplates =
    [
        [
            [0, 0, 1, 1, 0], //
            [0, 0, 1, 1, 0], //
            [0, 0, 1, 1, 0], //
            [0, 0, 1, 1, 0], //
            [0, 0, 0, 0, 0] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [1, 0, 1, 0, 1], //
            [0, 1, 0, 1, 0], //
            [0, 0, 0, 0, 0] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 0, 0, 0, 0], //
            [0, 1, 1, 1, 0], //
            [0, 1, 0, 1, 0], //
            [1, 0, 0, 0, 1] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [1, 0, 0, 0, 1] //
        ],
        [
            [0, 0, 1, 0, 0], //
            [0, 0, 1, 0, 0], //
            [0, 1, 0, 1, 0], //
            [0, 1, 0, 1, 0], //
            [0, 1, 0, 1, 0] //
        ]
    ];

    internal const int GridSize = 5;

    private static readonly List<RectTransform> SpellLineTables = [];
    private static ItemPresentation EmpressGarbOriginalItemPresentation { get; set; }

    internal static void ToggleVttCamera()
    {
        var cameraService = ServiceRepository.GetService<ICameraService>();

        IsVttCameraEnabled = !IsVttCameraEnabled;
        cameraService.DebugCameraEnabled = IsVttCameraEnabled;
    }

    internal static IEnumerator SelectPosition(CharacterAction action, FeatureDefinitionPower power)
    {
        var character = action.ActingCharacter;

        // disable this feature in MP as we cannot offer selections during power execution
        if (Global.IsMultiplayer)
        {
            action.actionParams.Positions.SetRange(character.LocationPosition);

            yield break;
        }

        var implementationService = ServiceRepository.GetService<IRulesetImplementationService>();
        var rulesetCharacter = character.RulesetCharacter;
        var usablePower = PowerProvider.Get(power, rulesetCharacter);
        var actionParams = new CharacterActionParams(character, ActionDefinitions.Id.PowerNoCost)
        {
            RulesetEffect =
                implementationService.InstantiateEffectPower(rulesetCharacter, usablePower, true)
        };
        var cursorService = ServiceRepository.GetService<ICursorService>();

        ResetCamera();
        cursorService.ActivateCursor<CursorLocationSelectPosition>(actionParams);

        var position = int3.zero;

        while (cursorService.CurrentCursor is CursorLocationSelectPosition cursorLocationSelectPosition)
        {
            position = cursorLocationSelectPosition.hasValidPosition
                ? cursorLocationSelectPosition.HoveredLocation
                : actionParams.ActingCharacter.LocationPosition;

            yield return null;
        }

        action.actionParams.Positions.SetRange(position);
    }

    internal static void ResetCamera()
    {
        var viewLocationContextualManager =
            ServiceRepository.GetService<IViewLocationContextualService>() as ViewLocationContextualManager;

        if (!viewLocationContextualManager)
        {
            return;
        }

        if (viewLocationContextualManager.rangeAttackDirector.state == PlayState.Playing)
        {
            viewLocationContextualManager.rangeAttackDirector.Stop();
            viewLocationContextualManager.ContextualSequenceEnd?.Invoke();
        }

        // ReSharper disable once InvertIf
        if (viewLocationContextualManager.meleeAttackDirector.state == PlayState.Playing)
        {
            viewLocationContextualManager.meleeAttackDirector.Stop();
            viewLocationContextualManager.ContextualSequenceEnd?.Invoke();
        }
    }

    internal static void UpdateMovementGrid()
    {
        var cursorService = ServiceRepository.GetService<ICursorService>();

        if (cursorService.CurrentCursor is CursorLocationBattleFriendlyTurn currentCursor)
        {
            currentCursor.movementHelper.RefreshHover();
        }
    }

    // Converts continuous ratio into series of stepped values
    internal static float GetSteppedHealthRatio(float ratio)
    {
        return ratio switch
        {
            // Green
            >= 1f => 1f,
            // Green
            >= 0.5f => 0.75f,
            // Orange
            >= 0.25f => 0.5f,
            // Red
            > 0f => 0.25f,
            _ => ratio
        };
    }

    internal static void ModifyActionMaps()
    {
        var service = ServiceRepository.GetService<IInputService>();

        //copy `GamepadSelector` action from `CharacterEdition` map into `ModalListBrowse` - needed for save by location to be able to scroll through save location selector
        var map = service.InputActionAsset.FindActionMap("ModalListBrowse");
        var action = map.AddAction("GamepadSelector");
        var oldMap = service.InputActionAsset.FindActionMap("CharacterEdition").FindAction("GamepadSelector");

        foreach (var oldMapBinding in oldMap.bindings)
        {
            action.AddBinding(oldMapBinding);
        }
    }

    internal static void RefreshMetamagicOffering(MetaMagicSubPanel __instance)
    {
        __instance.relevantMetamagicOptions.Clear();

        foreach (var allElement in DatabaseRepository.GetDatabase<MetamagicOptionDefinition>().GetAllElements())
        {
            if (!allElement.GuiPresentation.Hidden)
            {
                __instance.relevantMetamagicOptions.Add(allElement);
            }
        }

        __instance.relevantMetamagicOptions.Sort(MetamagicContext.CompareMetamagic);

        Gui.ReleaseChildrenToPool(__instance.Table);

        while (__instance.Table.childCount < __instance.relevantMetamagicOptions.Count)
        {
            Gui.GetPrefabFromPool(__instance.ItemPrefab, __instance.Table);
        }
    }

    #region Casting panel rows

    // most boxes a row holds with multi-line spell offering; a spell list's own slot box (the Warlock's pact
    // slots) counts as one box
    private const int MultilineBoxesPerRow = 4;

    /// <summary>
    ///     Destroys the row tables made besides the panel's own spellRepertoireLinesTable. Their lines go back to
    ///     the prefab pool unbound, so none keeps listening to its repertoire.
    /// </summary>
    internal static void SpellSelectionPanelRowsUnbind()
    {
        foreach (var spellTable in SpellLineTables)
        {
            // NOTE: don't use spellTable?. which bypasses Unity object lifetime check
            if (!spellTable)
            {
                continue;
            }

            foreach (Transform child in spellTable)
            {
                var line = child.GetComponent<SpellRepertoireLine>();

                // Unbind is safe on an already unbound line: it only releases what is still bound
                if (line)
                {
                    line.Unbind();
                }
            }

            Gui.ReleaseChildrenToPool(spellTable);
            spellTable.SetParent(null);
            Object.Destroy(spellTable.gameObject);
        }

        SpellLineTables.Clear();
    }

    /// <summary>
    ///     Replaces the rows the game's SpellSelectionPanel.Bind made. The game puts every spell list on one row up
    ///     to a cutoff level and the levels above it on a second row for the last spell list only, and binds a
    ///     list's line even when it has nothing to show there. Here each visible spell list is asked which levels
    ///     its line offers on this panel and whether it shows its own slot box (OfferOf), and rows are made only
    ///     from those: no line without content, every offered level on exactly one line, and the slot box on the
    ///     list's first line only. With multi-line spell offering rows hold up to MultilineBoxesPerRow boxes;
    ///     without it the first row keeps the game's cutoff and each list's levels above it get a row of their own.
    /// </summary>
    internal static void SpellSelectionPanelRowsBind(
        SpellSelectionPanel __instance,
        GuiCharacter caster,
        SpellsByLevelBox.SpellCastEngagedHandler spellCastEngaged,
        ActionDefinitions.ActionType actionType,
        bool cantripOnly)
    {
        var spellRepertoireLines = __instance.spellRepertoireLines;
        var spellRepertoireLinesTable = __instance.spellRepertoireLinesTable;
        var spellRepertoireSecondaryLine = __instance.spellRepertoireSecondaryLine;

        // take down what the game's Bind made; its secondary line isn't used, as every row is made here
        foreach (var spellRepertoireLine in spellRepertoireLines)
        {
            spellRepertoireLine.Unbind();
        }

        spellRepertoireLines.Clear();
        Gui.ReleaseChildrenToPool(spellRepertoireLinesTable);
        SpellSelectionPanelRowsUnbind();
        spellRepertoireSecondaryLine.Unbind();
        spellRepertoireSecondaryLine.gameObject.SetActive(false);

        var holder = GetRowHolder(spellRepertoireLinesTable);
        var repertoires = GetPanelRepertoires(caster.RulesetCharacter);
        var offers = repertoires
            .Select(repertoire => OfferOf(__instance, caster, repertoire, actionType, cantripOnly))
            .ToList();
        var rows = Main.Settings.DisableMultilineSpellOffering
            ? CompactRows(caster.RulesetCharacter, offers)
            : MultilineRows(offers);

        // the extra row tables are copies of the panel's table made while it is empty, so they hold no lines of
        // their own; each is put first in the holder's vertical layout, so later rows stand above earlier ones
        var tables = new List<RectTransform> { spellRepertoireLinesTable };

        for (var i = 1; i < rows.Count; i++)
        {
            var table = Object.Instantiate(spellRepertoireLinesTable, holder);

            table.localScale = spellRepertoireLinesTable.localScale;
            table.SetAsFirstSibling();
            SpellLineTables.Add(table);
            tables.Add(table);
        }

        // the game shows a list's header only when the panel has more than one list
        var showHeader = repertoires.Count > 1;

        for (var i = 0; i < rows.Count; i++)
        {
            foreach (var segment in rows[i])
            {
                var line = Gui.GetPrefabFromPool(__instance.spellRepertoireLinePrefab, tables[i])
                    .GetComponent<SpellRepertoireLine>();

                line.gameObject.SetActive(true);
                line.Bind(
                    caster,
                    segment.Repertoire,
                    showHeader,
                    spellCastEngaged,
                    __instance.SlotAdvancementPanel,
                    actionType,
                    cantripOnly,
                    segment.MinLevel,
                    segment.MaxLevel,
                    segment.Primary);
                // the panel's controller navigation (SelectDefaultControlImpl) looks for spells in this list only
                spellRepertoireLines.Add(line);
            }
        }

        // the panel is as wide as its widest row, as the game makes it as wide as its one table
        var width = 0f;

        foreach (var table in tables)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(table);
            width = Math.Max(width, table.rect.width);
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(holder);
        __instance.RectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
    }

    // the vertical layout that stacks the row tables, made once around the panel's own table
    private static RectTransform GetRowHolder(RectTransform spellRepertoireLinesTable)
    {
        var parent = spellRepertoireLinesTable.parent;

        if (parent.GetComponent<VerticalLayoutGroup>())
        {
            return (RectTransform)parent;
        }

        GameObject spellLineHolder = new();

        var vertGroup = spellLineHolder.AddComponent<VerticalLayoutGroup>();

        vertGroup.spacing = 10;
        spellLineHolder.AddComponent<ContentSizeFitter>();
        spellLineHolder.transform.SetParent(parent, true);
        spellLineHolder.transform.SetAsFirstSibling();
        spellLineHolder.transform.localScale = Vector3.one;
        spellRepertoireLinesTable.SetParent(spellLineHolder.transform);

        return spellLineHolder.GetComponent<RectTransform>();
    }

    // the spell lists the panel shows: not those whose casting feature is hidden, nor those with the mod's empty
    // spell list (features that only add spells to the character, such as Touched)
    private static List<RulesetSpellRepertoire> GetPanelRepertoires(RulesetCharacter character)
    {
        return character.SpellRepertoires
            .Where(repertoire =>
                !repertoire.SpellCastingFeature.GuiPresentation.Hidden
                && repertoire.SpellCastingFeature.SpellListDefinition != SpellsContext.EmptySpellList)
            .ToList();
    }

    /// <summary>
    ///     What a spell list's line offers on this panel, read from the game's own line. Bound with the empty level
    ///     range 0 to -1, a line still gathers its spells per level (SpellRepertoireLine.relevantSpellsByLevel, with
    ///     the panel's action type filter and this mod's patches to it, such as Blast Reload) but binds no box; bound
    ///     as a primary line it shows the list's slot box if it has one (GuiCharacter.DisplayUniqueLevelSpellSlots,
    ///     with this mod's multiclass rules). A level is offered when the line would show at least one spell there;
    ///     spells the panel disables (leveled spells on a cantrip-only panel) still count, as the line shows them.
    /// </summary>
    private static PanelOffer OfferOf(
        SpellSelectionPanel panel,
        GuiCharacter caster,
        RulesetSpellRepertoire repertoire,
        ActionDefinitions.ActionType actionType,
        bool cantripOnly)
    {
        var probe = Gui.GetPrefabFromPool(panel.spellRepertoireLinePrefab, panel.spellRepertoireLinesTable)
            .GetComponent<SpellRepertoireLine>();

        probe.Bind(caster, repertoire, false, null, panel.SlotAdvancementPanel, actionType, cantripOnly, 0, -1,
            true);

        var offer = new PanelOffer(
            repertoire,
            probe.relevantSpellsByLevel.Keys.OrderBy(level => level).ToList(),
            probe.uniqueLevelSlotsGroup.gameObject.activeSelf);

        probe.Unbind();
        Gui.ReleaseChildrenToPool(panel.spellRepertoireLinesTable);

        return offer;
    }

    // multi-line spell offering: each list's boxes in order (its slot box before its levels) packed into rows of
    // MultilineBoxesPerRow boxes; a list that doesn't fit continues on a line of its own on the next row
    private static List<List<RowSegment>> MultilineRows(List<PanelOffer> offers)
    {
        var rows = new List<List<RowSegment>>();
        var row = new List<RowSegment>();
        var boxesOnRow = 0;

        foreach (var offer in offers)
        {
            var levelIndex = 0;
            var primary = true;
            // the slot box is the first box of the list's first line
            var slotBoxPending = offer.HasSlotBox;

            while (slotBoxPending || levelIndex < offer.Levels.Count)
            {
                if (boxesOnRow == MultilineBoxesPerRow)
                {
                    rows.Add(row);
                    row = [];
                    boxesOnRow = 0;
                }

                var slotBoxes = slotBoxPending ? 1 : 0;
                var take = Math.Min(MultilineBoxesPerRow - boxesOnRow - slotBoxes, offer.Levels.Count - levelIndex);

                row.Add(SegmentOf(offer, levelIndex, take, primary));
                boxesOnRow += slotBoxes + take;
                levelIndex += take;
                slotBoxPending = false;
                primary = false;
            }
        }

        if (row.Count > 0)
        {
            rows.Add(row);
        }

        return rows;
    }

    // without multi-line spell offering: the first row holds every list's levels up to the game's cutoff
    // (NativeCutoffLevel) and its slot box, then each list's levels above the cutoff get a row of their own
    private static List<List<RowSegment>> CompactRows(RulesetCharacter character, List<PanelOffer> offers)
    {
        var cutoff = NativeCutoffLevel(character, offers.Select(offer => offer.Repertoire));
        var rows = new List<List<RowSegment>>();
        var firstRow = new List<RowSegment>();
        var onFirstRow = new HashSet<RulesetSpellRepertoire>();

        foreach (var offer in offers)
        {
            var count = offer.Levels.Count(level => level <= cutoff);

            if (count == 0 && !offer.HasSlotBox)
            {
                continue;
            }

            firstRow.Add(SegmentOf(offer, 0, count, true));
            onFirstRow.Add(offer.Repertoire);
        }

        if (firstRow.Count > 0)
        {
            rows.Add(firstRow);
        }

        foreach (var offer in offers)
        {
            var start = offer.Levels.Count(level => level <= cutoff);
            var count = offer.Levels.Count - start;

            // a list with nothing on the first row shows its slot box here (it has none, or it would be there)
            if (count > 0)
            {
                rows.Add([SegmentOf(offer, start, count, !onFirstRow.Contains(offer.Repertoire))]);
            }
        }

        return rows;
    }

    // a line for count of the list's offered levels from levelIndex on; with none (a line for the slot box only)
    // it gets the empty range 0 to -1, which binds no level box
    private static RowSegment SegmentOf(PanelOffer offer, int levelIndex, int count, bool primary)
    {
        return count == 0
            ? new RowSegment(offer.Repertoire, 0, -1, primary)
            : new RowSegment(offer.Repertoire, offer.Levels[levelIndex], offer.Levels[levelIndex + count - 1],
                primary);
    }

    /// <summary>
    ///     The highest level the game's SpellSelectionPanel.Bind puts on its first row: 5, less one for each racial
    ///     spell list, and for every other list one if it has cantrips at its casting level and one if it has its
    ///     own slot box (UniqueLevelSlots). The casting level is the feature's own for a monster's list, otherwise
    ///     the character's level.
    /// </summary>
    private static int NativeCutoffLevel(
        RulesetCharacter character,
        IEnumerable<RulesetSpellRepertoire> repertoires)
    {
        var cutoff = 5;

        foreach (var feature in repertoires.Select(repertoire => repertoire.SpellCastingFeature))
        {
            if (feature.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Race)
            {
                cutoff--;
                continue;
            }

            var castingLevel = feature.SpellCastingOrigin == FeatureDefinitionCastSpell.CastingOrigin.Monster
                ? feature.SpellCastingLevel
                : character.TryGetAttributeValue(AttributeDefinitions.CharacterLevel);

            if (feature.HasCantrips(castingLevel))
            {
                cutoff--;
            }

            if (feature.UniqueLevelSlots)
            {
                cutoff--;
            }
        }

        return cutoff;
    }

    // what a spell list's line offers on the panel: its levels with at least one spell, in ascending order, and
    // whether it shows its own slot box
    private readonly struct PanelOffer(RulesetSpellRepertoire repertoire, List<int> levels, bool hasSlotBox)
    {
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
        internal List<int> Levels { get; } = levels;
        internal bool HasSlotBox { get; } = hasSlotBox;
    }

    // one line on a row: a spell list's levels MinLevel to MaxLevel; a primary line also shows the list's slot box
    private readonly struct RowSegment(RulesetSpellRepertoire repertoire, int minLevel, int maxLevel, bool primary)
    {
        internal RulesetSpellRepertoire Repertoire { get; } = repertoire;
        internal int MinLevel { get; } = minLevel;
        internal int MaxLevel { get; } = maxLevel;
        internal bool Primary { get; } = primary;
    }

    #endregion

    internal static void SetTeleporterGadgetActiveAnimation(WorldGadget worldGadget, bool visibility = false)
    {
        if (worldGadget.UserGadget == null)
        {
            return;
        }

        if (worldGadget.UserGadget.GadgetBlueprint == TeleporterIndividual)
        {
            var visualEffect = worldGadget.transform.FindChildRecursive("Vfx_Teleporter_Individual_Idle_01");

            // NOTE: don't use visualEffect?. which bypasses Unity object lifetime check
            if (visualEffect)
            {
                visualEffect.gameObject.SetActive(visibility);
            }
        }
        else if (worldGadget.UserGadget.GadgetBlueprint == TeleporterParty)
        {
            var visualEffect = worldGadget.transform.FindChildRecursive("Vfx_Teleporter_Party_Idle_01");

            // NOTE: don't use visualEffect?. which bypasses Unity object lifetime check
            if (visualEffect)
            {
                visualEffect.gameObject.SetActive(visibility);
            }
        }
    }

    private static bool IsGadgetExit(GadgetBlueprint gadgetBlueprint, bool onlyWithGizmos = false)
    {
        const int ExitsWithGizmos = 2;

        GadgetBlueprint[] gadgetExits =
        [
            VirtualExit, VirtualExitMultiple, Exit, ExitMultiple, TeleporterIndividual, TeleporterParty
        ];

        return Array.IndexOf(gadgetExits, gadgetBlueprint) >= (onlyWithGizmos ? ExitsWithGizmos : 0);
    }

    internal static void HideExitsAndTeleportersGizmosIfNotDiscovered(
        GameGadget __instance,
        int conditionIndex,
        bool state)
    {
        if (conditionIndex < 0 || conditionIndex >= __instance.conditionNames.Count)
        {
            return;
        }

        if (!__instance.CheckIsEnabled() || !__instance.IsTeleport())
        {
            return;
        }

        var service = ServiceRepository.GetService<IGameLocationService>();

        if (service == null)
        {
            return;
        }

        var worldGadget = service.WorldLocation.WorldSectors
            .SelectMany(ws => ws.WorldGadgets)
            .FirstOrDefault(wg => wg.GameGadget == __instance);

        if (!worldGadget)
        {
            return;
        }

        SetTeleporterGadgetActiveAnimation(worldGadget, state);
    }

    internal static void ComputeIsRevealedExtended(GameGadget __instance, ref bool __result)
    {
        var userGadget = Gui.GameLocation.UserLocation.UserRooms
            .SelectMany(a => a.UserGadgets)
            .FirstOrDefault(b => b.UniqueName == __instance.UniqueNameId);

        if (userGadget == null || !IsGadgetExit(userGadget.GadgetBlueprint))
        {
            return;
        }

        // reverts the revealed state and recalculates it
        __instance.revealed = false;
        __result = false;

        var referenceBoundingBox = __instance.ReferenceBoundingBox;
        var gridAccessor = GridAccessor.Default;

        // required for gadgets that are enabled from conditional states
        if (!referenceBoundingBox.IsValid)
        {
            __instance.revealed = true;
            __result = true;

            return;
        }

        foreach (var position in referenceBoundingBox.EnumerateAllPositionsWithin())
        {
            if (!gridAccessor.Visited(position))
            {
                continue;
            }

            var gameLocationService = ServiceRepository.GetService<IGameLocationService>();
            var worldGadgets = gameLocationService.WorldLocation.WorldSectors.SelectMany(ws => ws.WorldGadgets);
            var worldGadget = worldGadgets.FirstOrDefault(wg => wg.GameGadget == __instance);

            var isInvisible = __instance.IsInvisible();
            var isEnabled = __instance.CheckIsEnabled();

            if (worldGadget)
            {
                SetTeleporterGadgetActiveAnimation(worldGadget, isEnabled && !isInvisible);
            }

            __instance.revealed = true;
            __result = true;

            break;
        }
    }

    internal static void SetHighlightVisibilityExtended(WorldGadget __instance, ref bool visible)
    {
        if (IsGadgetExit(__instance.UserGadget.GadgetBlueprint, true))
        {
            return;
        }

        var activator = DatabaseHelper.GadgetDefinitions.Activator;
        var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
        var visibilityService = ServiceRepository.GetService<IGameLocationVisibilityService>();
        var feedbackPosition = __instance.GameGadget.FeedbackPosition;

        // activators aren't detected in their original position so we handle them in a different way
        if (!__instance.GadgetDefinition == activator)
        {
            var position = new int3((int)feedbackPosition.x, (int)feedbackPosition.y, (int)feedbackPosition.z);

            foreach (var gameLocationCharacter in characterService.PartyCharacters)
            {
                visible = visibilityService.IsCellPerceivedByCharacter(position, gameLocationCharacter);

                if (visible)
                {
                    return;
                }
            }

            return;
        }

        // scan activators surrounding cells
        for (var x = -1; x <= 1; x++)
        {
            for (var z = -1; z <= 1; z++)
            {
                // jump original position
                if (x == 0 && z == 0)
                {
                    continue;
                }

                var position = new int3(
                    (int)feedbackPosition.x + x, (int)feedbackPosition.y, (int)feedbackPosition.z + z);

                foreach (var gameLocationCharacter in characterService.PartyCharacters)
                {
                    visible = visibilityService.IsCellPerceivedByCharacter(position, gameLocationCharacter);

                    if (visible)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static void LoadRemoveBugVisualModels()
    {
        if (!Main.Settings.RemoveBugVisualModels)
        {
            return;
        }

        // Spiderlings, fire spider, kindred spirit spider, BadlandsSpider(normal, conjured and wildshape versions)
        const string ASSET_REFERENCE_SPIDER_1 = "362fc51df586d254ab182ef854396f82";
        //CrimsonSpiderling, PhaseSpider, SpectralSpider, CrimsonSpider, deep spider(normal, conjured and wildshape versions)
        const string ASSET_REFERENCE_SPIDER_2 = "40b5fe532a9a0814097acdb16c74e967";
        // spider queen
        const string ASSET_REFERENCE_SPIDER_3 = "8fc96b2a8c5fcc243b124d31c63df5d9";
        //Giant_Beetle, Small_Beetle, Redeemer_Zealot, Redeemer_Pilgrim
        const string ASSET_REFERENCE_BEETLE = "04dfcec8c8afb8642a80c1116de218d4";
        //Young_Remorhaz, Remorhaz
        const string ASSET_REFERENCE_REMORHAZ = "ded896e0c4ef46144904375ecadb1bb1";

        var brownBear = DatabaseHelper.MonsterDefinitions.BrownBear;
        var bearPrefab = new AssetReference("cc36634f504fa7049a4499a91749d7d5");

        var wolf = DatabaseHelper.MonsterDefinitions.Wolf;
        var wolfPrefab = new AssetReference("6e02c9bcfb5122042a533e7732182b1d");

        var ape = DatabaseHelper.MonsterDefinitions.Ape_MonsterDefinition;
        var apePrefab = new AssetReference("8f4589a9a294b444785fab045256a713");

        var dbMonsterDefinition = DatabaseRepository.GetDatabase<MonsterDefinition>();

        // check every monster for targeted prefab guid references
        foreach (var monster in dbMonsterDefinition)
        {
            // get monster asset reference for prefab guid comparison
            var value = monster.MonsterPresentation.malePrefabReference;

            switch (value.AssetGUID)
            {
                // swap bears for spiders
                case ASSET_REFERENCE_SPIDER_1:
                case ASSET_REFERENCE_SPIDER_2:
                case ASSET_REFERENCE_SPIDER_3:
                    monster.MonsterPresentation.malePrefabReference = bearPrefab;
                    monster.MonsterPresentation.femalePrefabReference = bearPrefab;
                    monster.GuiPresentation.spriteReference = brownBear.GuiPresentation.SpriteReference;
                    monster.bestiarySpriteReference = brownBear.BestiarySpriteReference;
                    monster.MonsterPresentation.monsterPresentationDefinitions = brownBear.MonsterPresentation
                        .MonsterPresentationDefinitions;
                    break;
                // swap apes for remorhaz
                case ASSET_REFERENCE_REMORHAZ:
                    monster.MonsterPresentation.malePrefabReference = apePrefab;
                    monster.MonsterPresentation.femalePrefabReference = apePrefab;
                    monster.GuiPresentation.spriteReference = ape.GuiPresentation.SpriteReference;
                    monster.bestiarySpriteReference = ape.BestiarySpriteReference;
                    monster.MonsterPresentation.monsterPresentationDefinitions = ape.MonsterPresentation
                        .MonsterPresentationDefinitions;
                    break;
                // swap wolves for beetles
                case ASSET_REFERENCE_BEETLE:
                    monster.MonsterPresentation.malePrefabReference = wolfPrefab;
                    monster.MonsterPresentation.femalePrefabReference = wolfPrefab;
                    monster.GuiPresentation.spriteReference = wolf.GuiPresentation.SpriteReference;
                    monster.bestiarySpriteReference = wolf.BestiarySpriteReference;
                    monster.MonsterPresentation.monsterPresentationDefinitions = wolf.MonsterPresentation
                        .MonsterPresentationDefinitions;

                    // changing beetle scale to suit replacement model
                    monster.MonsterPresentation.maleModelScale = 0.655f;
                    monster.MonsterPresentation.femaleModelScale = 0.655f;
                    break;
            }
        }
    }

    internal static void SwitchCrownOfTheMagister()
    {
        var crowns = new[]
        {
            CrownOfTheMagister, CrownOfTheMagister01, CrownOfTheMagister02, CrownOfTheMagister03,
            CrownOfTheMagister04, CrownOfTheMagister05, CrownOfTheMagister06, CrownOfTheMagister07,
            CrownOfTheMagister08, CrownOfTheMagister09, CrownOfTheMagister10, CrownOfTheMagister11,
            CrownOfTheMagister12
        };

        foreach (var itemPresentation in crowns.Select(x => x.ItemPresentation))
        {
            var maleBodyPartBehaviours = itemPresentation.GetBodyPartBehaviours(CreatureSex.Male);

            maleBodyPartBehaviours[0] = SettingsContext.GuiModManagerInstance.HideCrownOfMagister
                ? GraphicsCharacterDefinitions.BodyPartBehaviour.Shape
                : GraphicsCharacterDefinitions.BodyPartBehaviour.Armor;
        }
    }

    internal static void SwitchEmpressGarb()
    {
        EmpressGarbOriginalItemPresentation ??= Enchanted_ChainShirt_Empress_war_garb.ItemPresentation;

        switch (SettingsContext.GuiModManagerInstance.EmpressGarbAppearance)
        {
            case "Normal":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = EmpressGarbOriginalItemPresentation;
                break;

            case "Barbarian":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = BarbarianClothes.ItemPresentation;
                break;

            case "Druid":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = LeatherDruid.ItemPresentation;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.useArmorAddressableName = true;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.armorAddressableName = LeatherDruid.Name;
                break;

            case "ElvenChain":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = ElvenChain.ItemPresentation;
                break;

            case "SorcererOutfit":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = SorcererArmor.ItemPresentation;
                break;

            case "StuddedLeather":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = StuddedLeather.ItemPresentation;
                break;

            case "GreenMageArmor":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = GreenmageArmor.ItemPresentation;
                break;

            case "WizardOutfit":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = WizardClothes_Alternate.ItemPresentation;
                break;

            case "ScavengerOutfit1": // Ranger
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = ClothesScavenger_A.ItemPresentation;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.useArmorAddressableName = true;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.armorAddressableName = ClothesScavenger_A.Name;
                break;

            case "ScavengerOutfit2": // Rogue
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = ClothesScavenger_B.ItemPresentation;
                break;

            case "BardArmor":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = Bard_Armor.ItemPresentation;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.useArmorAddressableName = true;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.armorAddressableName = Bard_Armor.Name;
                break;

            case "WarlockArmor":
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = Warlock_Armor.ItemPresentation;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.useArmorAddressableName = true;
                Enchanted_ChainShirt_Empress_war_garb.ItemPresentation.armorAddressableName = Warlock_Armor.Name;
                break;
            default:
                Enchanted_ChainShirt_Empress_war_garb.itemPresentation = EmpressGarbOriginalItemPresentation;
                break;
        }
    }

    internal static FeatureDefinitionActionAffinity ActionAffinityFeatCrusherToggle { get; private set; }

    private static void LoadFeatCrusherToggle()
    {
        ActionAffinityFeatCrusherToggle = FeatureDefinitionActionAffinityBuilder
            .Create(DatabaseHelper.FeatureDefinitionActionAffinitys.ActionAffinitySorcererMetamagicToggle,
                "ActionAffinityFeatCrusherToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((ActionDefinitions.Id)ExtraActionId.FeatCrusherToggle)
            .AddToDB();
    }

    internal static FeatureDefinitionActionAffinity ActionAffinityPaladinSmiteToggle { get; private set; }

    private static void LoadPaladinSmiteToggle()
    {
        ActionAffinityPaladinSmiteToggle = FeatureDefinitionActionAffinityBuilder
            .Create(DatabaseHelper.FeatureDefinitionActionAffinitys.ActionAffinitySorcererMetamagicToggle,
                "ActionAffinityPaladinSmiteToggle")
            .SetGuiPresentationNoContent(true)
            .SetAuthorizedActions((ActionDefinitions.Id)ExtraActionId.PaladinSmiteToggle)
            .AddToDB();
    }

    internal static void ResetFormationGrid(int selectedSet)
    {
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                Main.Settings.FormationGridSets[selectedSet][y][x] = FormationGridSetTemplates[selectedSet][y][x];
            }
        }
    }

    internal static void ResetAllFormationGrids()
    {
        for (var i = 0; i < FormationGridSetTemplates.Length; i++)
        {
            ResetFormationGrid(i);
        }

        Main.Settings.FormationGridSelectedSet = 1;
    }

    private static void LoadFormationGrid()
    {
        if (Main.Settings.FormationGridSelectedSet < 0)
        {
            ResetAllFormationGrids();
        }
        else
        {
            FillDefinitionFromFormationGrid();
        }
    }

#if false
    private static void FillFormationGridFromDefinition(int selectedSet)
    {
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                Main.Settings.FormationGridSets[selectedSet][y][x] = 0;
            }
        }

        foreach (var position in DatabaseHelper.FormationDefinitions.Column2.FormationPositions)
        {
            Main.Settings.FormationGridSets[selectedSet][-position.z][position.x + 2] = 1;
        }
    }
#endif

    internal static void SetFormationGrid(int set)
    {
        Main.Settings.FormationGridSelectedSet = set;
        FillDefinitionFromFormationGrid();
    }

    internal static void FillDefinitionFromFormationGrid()
    {
        var position = 0;
        var selectedSet = Main.Settings.FormationGridSelectedSet;

        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                if (Main.Settings.FormationGridSets[selectedSet][y][x] == 1)
                {
                    DatabaseHelper.FormationDefinitions.Column2.FormationPositions[position++] = new int3(x - 2, 0, -y);
                }
            }
        }

        if (UnityModManagerUIPatcher.ModManagerUI.IsOpen)
        {
            return;
        }

        Gui.GuiService.ShowAlert(
            Gui.Format("ModUi/&FormationSelected", (Main.Settings.FormationGridSelectedSet + 1).ToString()),
            Gui.ColorAlert);
    }

    internal static void Load()
    {
        InventoryManagementContext.Load();
        SwitchCrownOfTheMagister();
        SwitchEmpressGarb();
        LoadRemoveBugVisualModels();
        LoadFeatCrusherToggle();
        LoadPaladinSmiteToggle();
        LoadFormationGrid();
    }

    internal static class GameHud
    {
        internal static void ShowAll([NotNull] GameLocationBaseScreen gameLocationBaseScreen)
        {
            var initiativeOrPartyPanel = GetInitiativeOrPartyPanel();
            var timeAndNavigationPanel = GetTimeAndNavigationPanel();
            var guiConsoleScreen = Gui.GuiService.GetScreen<GuiConsoleScreen>();
            var anyVisible = guiConsoleScreen.Visible || gameLocationBaseScreen.CharacterControlPanel.Visible;

            if (!anyVisible)
            {
                if (initiativeOrPartyPanel)
                {
                    anyVisible = initiativeOrPartyPanel.Visible;
                }
            }

            if (!anyVisible)
            {
                if (timeAndNavigationPanel)
                {
                    anyVisible = timeAndNavigationPanel.Visible;
                }
            }

            ShowCharacterControlPanel(gameLocationBaseScreen, anyVisible);
            TogglePanelVisibility(guiConsoleScreen, anyVisible);
            TogglePanelVisibility(initiativeOrPartyPanel);
            TogglePanelVisibility(timeAndNavigationPanel, anyVisible);

            return;

            [CanBeNull]
            GuiPanel GetInitiativeOrPartyPanel()
            {
                return gameLocationBaseScreen switch
                {
                    GameLocationScreenExploration gameLocationScreenExploration => gameLocationScreenExploration
                        .partyControlPanel,
                    GameLocationScreenBattle gameLocationScreenBattle => gameLocationScreenBattle.initiativeTable,
                    _ => null
                };
            }

            [CanBeNull]
            TimeAndNavigationPanel GetTimeAndNavigationPanel()
            {
                return gameLocationBaseScreen switch
                {
                    GameLocationScreenExploration gameLocationScreenExploration => gameLocationScreenExploration
                        .timeAndNavigationPanel,
                    GameLocationScreenBattle gameLocationScreenBattle =>
                        gameLocationScreenBattle.timeAndNavigationPanel,
                    _ => null
                };
            }
        }

        private static void ShowCharacterControlPanel([NotNull] GameLocationBaseScreen gameLocationBaseScreen,
            bool forceHide = false)
        {
            var characterControlPanel = gameLocationBaseScreen.CharacterControlPanel;

            if (characterControlPanel.Visible || forceHide)
            {
                characterControlPanel.Hide();
                characterControlPanel.Unbind();
            }
            else
            {
                var gameLocationSelectionService = ServiceRepository.GetService<IGameLocationSelectionService>();

                if (gameLocationSelectionService.SelectedCharacters.Count <= 0)
                {
                    return;
                }

                characterControlPanel.Bind(gameLocationSelectionService.SelectedCharacters[0],
                    gameLocationBaseScreen.ActionTooltipDock);
                characterControlPanel.Show();
            }
        }

        private static void TogglePanelVisibility(GuiPanel guiPanel, bool forceHide = false)
        {
            if (!guiPanel)
            {
                return;
            }

            if (guiPanel.Visible || forceHide)
            {
                guiPanel.Hide();
            }
            else
            {
                guiPanel.Show();
            }
        }

        internal static void RefreshCharacterControlPanel()
        {
            if (Gui.CurrentLocationScreen && Gui.CurrentLocationScreen is GameLocationBaseScreen location)
            {
                location.CharacterControlPanel.RefreshNow();
            }
        }
    }

    internal static class Teleporter
    {
        internal static void ConfirmTeleportParty(Func<int3> getPosition)
        {
            var position = getPosition();

            Gui.GuiService.ShowMessage(
                MessageModal.Severity.Attention2,
                "Message/&TeleportPartyTitle",
                Gui.Format("Message/&TeleportPartyDescription", position.x.ToString(), position.x.ToString()),
                "Message/&MessageYesTitle", "Message/&MessageNoTitle",
                () => TeleportParty(position),
                null);
        }

        internal static int3 GetEncounterPosition()
        {
            var gameLocationService = ServiceRepository.GetService<IGameLocationService>();
            var x = (int)gameLocationService.GameLocation.LastCameraPosition.x;
            var z = (int)gameLocationService.GameLocation.LastCameraPosition.z;

            return new int3(x, 0, z);
        }

        internal static int3 GetLeaderPosition()
        {
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var position = characterService.PartyCharacters[0].LocationPosition;
            var currentCharacter = Global.CurrentCharacter ??
                                   characterService.PartyCharacters[0].RulesetCharacter;
            var locationCharacter = characterService.PartyCharacters
                .FirstOrDefault(x => x.RulesetCharacter == currentCharacter);

            return locationCharacter?.LocationPosition ?? position;
        }

        private static void TeleportParty(int3 position)
        {
            var characterService = ServiceRepository.GetService<IGameLocationCharacterService>();
            var positioningService = ServiceRepository.GetService<IGameLocationPositioningService>();
            var boxInt = new BoxInt(position, int3.zero, int3.zero);

            // 20 to improve teleport behavior on campaigns with different heights
            boxInt.Inflate(1, 20, 1);

            var characters = characterService.PartyCharacters.Union(characterService.GuestCharacters);

            foreach (var gameLocationCharacter in characters)
            {
                foreach (var alternatePosition in boxInt.EnumerateAllPositionsWithin())
                {
                    if (!positioningService.CanPlaceCharacter(
                            gameLocationCharacter, alternatePosition, CellHelpers.PlacementMode.Station)
                        || !positioningService.CanCharacterStayAtPosition_Floor(
                            gameLocationCharacter, alternatePosition, true))
                    {
                        continue;
                    }

                    ServiceRepository.GetService<IGameLocationPositioningService>().TeleportCharacter(
                        gameLocationCharacter, alternatePosition, LocationDefinitions.Orientation.North);
                }
            }
        }
    }
}
