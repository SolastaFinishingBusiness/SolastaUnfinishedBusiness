using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using SolastaUnfinishedBusiness.CustomUI;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

// The location editor's gadget, prop and room palettes get the Dungeon Maker list bar (issue #42; user, 2026-11-13:
// "Can we just add the new dropdown for sorts to the palette then?"), in place of the former "Enable assets sorting
// on Dungeon Maker editor" setting, which swapped the game's own comparison for one by category title and name and
// only took effect at the next start of the game.
//
// What the game does (from its IL): each palette sorts its blueprints once, when the game starts (RuntimeLoaded,
// Array.Sort with the palette's Compare), and every Bind groups them by category in that order: one
// BlueprintCategoryItem per category, in the order the categories first appear, each binding row i of its
// blueprintsTable to blueprint i and switching off (and unbinding) the rows past the end. So the categories keep the
// game's order (user's choice: within each category, like the campaign editor's lists), and within a category the
// bar orders and filters the rows on screen, as it does for the editor's lists:
//
//   Sort: Default (the game's order), Name (the blueprint's internal name) or Title (its name as shown), with the
//   direction arrow; the search shows only the blueprints whose name or title contains the text, and a category none
//   of whose blueprints is shown is hidden while the search is on.
//
// The bar's "Categories:" toggle puts the categories themselves in title order instead of the game's (one choice for
// the palettes and the campaign editor); the separator under each category is then shown on all but the last one on
// screen, as the game's Bind does it (separator.SetActive(!last)).
//
// Only what is shown changes; the sort and its direction are the same saved choice as the other screens' bars. The
// search belongs to each palette and stays while the palette stays open, as the game binds it again when the
// environment changes.
[UsedImplicitly]
public static class BlueprintPaletteSortPatcher
{
    // the campaign editor's Import button, copied for the bar's Clear button: a palette has no button of its own,
    // and the editor screen exists from the start of the game, shown or not
    private const string SourceButton = "RightPanel/ExportGroup/ImportContentsButton";

    // the categories each palette bound last, in the game's order
    private static readonly ConditionalWeakTable<BaseBlueprintSelectionPanel, List<BlueprintCategoryItem>> Bound =
        new();

    // The palettes seen so far. The game builds the three of them together and its tabs only show and hide them
    // (Codex's review of 72916454), so a change made on one palette's bar is applied to all of them: the sort and
    // the category order are one choice. Each keeps its own search.
    private static readonly List<BaseBlueprintSelectionPanel> Palettes = [];

    // Each palette's fold states by category (the BaseBlueprint.Category key the game groups by), taken before the
    // game lets go of the headers. An environment change calls the palette's Unbind, which sends every header back
    // to the prefab pool (Gui.ReleaseChildrenToPool), and then Bind(resetFold: false), which takes headers from the
    // pool and leaves each its own folded flag: in the game's order a fold could already land on another category,
    // and with the categories reordered it did (seen in game on 8534f3b2: Activators open, then Adventuring; traced by
    // Codex in the IL). So the folds are restored by category after such a Bind; with resetFold the game's own
    // reset stands.
    private static readonly ConditionalWeakTable<BaseBlueprintSelectionPanel, Dictionary<string, bool>> Folds =
        new();

    // the category each bound header shows, from its first blueprint
    private static string CategoryKeyOf(BlueprintCategoryItem category)
    {
        return category && category.blueprintsTable
            ? category.blueprintsTable.Cast<Transform>().Select(BlueprintOf).FirstOrDefault(x => x)?.Category
            : null;
    }

    // remembers the folds of the headers the palette still holds (none once Unbind has released them, and then the
    // last ones taken stay)
    private static void RememberFolds(BaseBlueprintSelectionPanel panel)
    {
        if (!panel || !panel.categoriesTable || !Bound.TryGetValue(panel, out var categories))
        {
            return;
        }

        var held = categories.Where(x => x && x.transform.parent == panel.categoriesTable).ToList();

        if (held.Count == 0)
        {
            return;
        }

        var folds = Folds.GetOrCreateValue(panel);

        foreach (var category in held)
        {
            var key = CategoryKeyOf(category);

            if (key != null)
            {
                folds[key] = category.folded;
            }
        }
    }

    // gives each header just bound the fold its category had (the game's own ForceFoldOrUnfold: flag, toggle, table,
    // arrow, with no callbacks)
    private static void RestoreFolds(BaseBlueprintSelectionPanel panel, IEnumerable<BlueprintCategoryItem> categories)
    {
        if (!Folds.TryGetValue(panel, out var folds))
        {
            return;
        }

        foreach (var category in categories)
        {
            var key = CategoryKeyOf(category);

            if (key != null && folds.TryGetValue(key, out var folded) && category.folded != folded)
            {
                category.ForceFoldOrUnfold(folded);
            }
        }
    }

    private static void ApplyAll()
    {
        Palettes.RemoveAll(x => !x);

        foreach (var palette in Palettes)
        {
            DungeonMakerListTools.RefreshBar(KeyOf(palette));
            Apply(palette);
        }
    }

    // the palette's search key: its own class name, so the three palettes have their own search
    private static string KeyOf(BaseBlueprintSelectionPanel panel)
    {
        return panel.GetType().Name;
    }

    // a row the game has just bound to a blueprint: switched on and holding one (the game switches off and unbinds
    // the rows past the end of the category)
    private static bool IsBoundRow(Transform row)
    {
        return row.gameObject.activeSelf && row.TryGetComponent<BaseBlueprintItem>(out var item) &&
               item.BaseBlueprint;
    }

    private static BaseBlueprint BlueprintOf(Transform row)
    {
        return row.TryGetComponent<BaseBlueprintItem>(out var item) ? item.BaseBlueprint : null;
    }

    private static string BlueprintName(Transform row)
    {
        return BlueprintOf(row)?.name;
    }

    // the blueprint's name as the palette shows it
    private static string BlueprintTitle(Transform row)
    {
        var title = BlueprintOf(row)?.GuiPresentation?.Title;

        return DungeonMakerListTools.PlainText(string.IsNullOrEmpty(title) ? BlueprintName(row) : Gui.Localize(title));
    }

    // Orders and filters every category of the palette, and hides a category none of whose blueprints the search
    // leaves. The categories are those the game bound last; one it has switched off since is left alone.
    private static void Apply(BaseBlueprintSelectionPanel panel)
    {
        if (!panel || !Bound.TryGetValue(panel, out var categories))
        {
            return;
        }

        var key = KeyOf(panel);
        var searching = DungeonMakerListTools.Search(key).Length > 0;

        foreach (var category in categories.Where(x => x && x.transform.parent == panel.categoriesTable))
        {
            var table = category.blueprintsTable;

            DungeonMakerListTools.Apply(table, key, BlueprintName, BlueprintTitle);

            var anyShown = !searching ||
                           table.Cast<Transform>().Any(x => x.gameObject.activeSelf && BlueprintOf(x));

            if (category.gameObject.activeSelf != anyShown)
            {
                category.gameObject.SetActive(anyShown);
            }
        }

        // the categories in the game's order (the order bound) or by title, and the separators of the ones shown
        var ordered = DungeonMakerListTools.OrderSections(
            categories.Where(x => x && x.transform.parent == panel.categoriesTable).Select(x => x.transform).ToList(),
            x => DungeonMakerListTools.PlainText(x.GetComponent<BlueprintCategoryItem>()?.categoryTitle?.TMP_Text?.text));
        var shown = ordered
            .Where(x => x.gameObject.activeSelf)
            .Select(x => x.GetComponent<BlueprintCategoryItem>())
            .Where(x => x && x.separator)
            .ToList();

        for (var i = 0; i < shown.Count; i++)
        {
            var separator = shown[i].separator.gameObject;
            var wanted = i < shown.Count - 1;

            if (separator.activeSelf != wanted)
            {
                separator.SetActive(wanted);
            }
        }

        LayoutRebuilder.ForceRebuildLayoutImmediate(panel.categoriesTable);
    }

    //PATCH: the folds are remembered before the palette lets go of its headers
    [HarmonyPatch(typeof(BaseBlueprintSelectionPanel), nameof(BaseBlueprintSelectionPanel.Unbind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BaseBlueprintSelectionPanel_Unbind
    {
        [UsedImplicitly]
        public static void Prefix(BaseBlueprintSelectionPanel __instance)
        {
            RememberFolds(__instance);
        }
    }

    //PATCH: the bar, and the view of each category, after the game bound the palette's categories (each palette's
    //own Bind fills AvailableBlueprints and then calls this one)
    [HarmonyPatch(typeof(BaseBlueprintSelectionPanel), nameof(BaseBlueprintSelectionPanel.Bind))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class BaseBlueprintSelectionPanel_Bind
    {
        // the folds, if the headers are still held (a Bind with no Unbind before it)
        [UsedImplicitly]
        public static void Prefix(BaseBlueprintSelectionPanel __instance)
        {
            RememberFolds(__instance);
        }

        [UsedImplicitly]
        public static void Postfix(BaseBlueprintSelectionPanel __instance, bool resetFold)
        {
            var table = __instance.categoriesTable;

            if (!Palettes.Contains(__instance))
            {
                Palettes.Add(__instance);
            }

            if (!table)
            {
                return;
            }

            var editor = Gui.GuiService?.GetScreen<UserCampaignEditorScreen>();

            DungeonMakerListTools.Ensure(__instance.transform, KeyOf(__instance), "BlueprintsScrollview",
                SourceButton, "ModUi/&DungeonMakerSortTitle", ApplyAll, null, stacked: true,
                keepSearch: true, sourceRoot: editor ? editor.transform : null, sections: true);

            // the categories the game has just bound, and in each the rows it bound
            var categories = Bound.GetOrCreateValue(__instance);

            categories.Clear();

            foreach (Transform child in table)
            {
                if (child.gameObject.activeSelf && child.TryGetComponent<BlueprintCategoryItem>(out var category))
                {
                    categories.Add(category);
                    DungeonMakerListTools.Bound(category.blueprintsTable, IsBoundRow);
                }
            }

            // each category gets its own fold back, unless the game reset them all
            if (!resetFold)
            {
                RestoreFolds(__instance, categories);
            }

            Apply(__instance);
        }
    }
}
