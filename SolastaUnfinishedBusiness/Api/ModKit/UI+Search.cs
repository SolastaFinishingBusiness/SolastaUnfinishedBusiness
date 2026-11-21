using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using SolastaUnfinishedBusiness.Api.LanguageExtensions;
using UnityEngine;
using GL = UnityEngine.GUILayout;

namespace SolastaUnfinishedBusiness.Api.ModKit;

// Menu search: while there is search text every searchable page is drawn, and each titled control only appears when
// its text matches, under a "Tab › Section" heading. Controls without a title (grids, fields, bare sliders) only
// appear next to a matching label, or when one of their own texts matches. Collapsed sections are opened while
// searching; every section's state is remembered when the search starts and put back when it ends.
//
// IMGUI draws each frame in several passes that must contain the same controls, so the search text, and everything
// derived from it, only changes at the start of a frame (the Layout pass). That includes opening the sections: they
// are all opened when the search starts, before anything is drawn. Opened only when its header was drawn, a section
// changed state partway through the Layout pass, after controls above it had already read that state, and the Repaint
// pass then drew a different number of controls (the Spells page's Expand All row lost or gained Select Displayed,
// and Unity aborted the menu with "Getting control N's position in a group with only N controls").
internal static partial class UI
{
    private static readonly Regex RichTextTags = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly List<string> SearchPath = [];

    // real options whose names also start with Display: they aren't sections, so a search must neither open them nor
    // put them back when it ends (that undid a change made to them from the search results)
    private static readonly HashSet<string> DisplayOptionsThatAreNotSections =
    [
        nameof(Settings.DisplayAllKnownSpellsDuringLevelUp),
        nameof(Settings.DisplayPactSlotsOnSpellSelectionPanel)
    ];

    // collapsed or expanded sections are stored in the settings' Display... switches
    private static readonly PropertyInfo[] SectionStateProperties = typeof(Settings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(x => x.PropertyType == typeof(bool) && x.CanRead && x.CanWrite && x.Name.StartsWith("Display") &&
                    !DisplayOptionsThatAreNotSections.Contains(x.Name))
        .ToArray();

    // per class and per spell list sections are stored in Display... dictionaries
    private static readonly PropertyInfo[] SectionStateDictionaries = typeof(Settings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(x => typeof(IDictionary<string, bool>).IsAssignableFrom(x.PropertyType) && x.CanRead &&
                    x.Name.StartsWith("Display"))
        .ToArray();

    // every on/off setting: a section a search opens is found by which of these turn on in that frame, whatever its
    // name, and is closed again when the search ends
    private static readonly PropertyInfo[] BoolSettings = typeof(Settings)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(x => x.PropertyType == typeof(bool) && x.CanRead && x.CanWrite)
        .ToArray();

    private static readonly HashSet<PropertyInfo> SettingsOpenedBySearch = [];
    private static Dictionary<PropertyInfo, bool> _boolSettingsAtFrameStart;
    private static bool _sectionForcedOpen;

    private static Dictionary<PropertyInfo, bool> _sectionStatesBeforeSearch;
    private static Dictionary<PropertyInfo, Dictionary<string, bool>> _sectionDictionariesBeforeSearch;
    private static string _pendingSearchHeader;
    private static string _searchInput = string.Empty;
    private static int _searchBypassDepth;
    private static bool _searchShowsUntitled;

    internal static string SearchText { get; private set; } = string.Empty;

    internal static bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    private static int SearchMatchCount { get; set; }

    internal static void SearchBox()
    {
        // the text typed last frame applies from this frame's Layout pass, before anything is drawn, so the Clear
        // button and every searched control are the same in all of the frame's passes
        if (Event.current.type == EventType.Layout)
        {
            TrackSettingsOpenedBySearch();
            SetSearchText(_searchInput);
        }

        AlwaysEnabled(() =>
        {
            using (HorizontalScope())
            {
                GL.Label(Gui.Localize("ModUi/&SearchSettings").Bold(), GL.ExpandWidth(false));
                Space(10f);
                var inputBefore = _searchInput ?? string.Empty;

                _searchInput = GL.TextField(_searchInput ?? string.Empty, GL.Width(350f));
                SearchClearDiagnostics.AfterTextField(inputBefore, _searchInput);
                Space(10f);

                // shown from the frame's search text, so every pass of the frame has the same controls
                if (IsSearching)
                {
                    var diag = SearchClearDiagnostics.BeforeClear();
                    var clicked = GL.Button(Gui.Localize("ModUi/&SearchClear"), GL.ExpandWidth(false));

                    SearchClearDiagnostics.AfterClear(diag, clicked, _searchInput);

                    if (clicked)
                    {
                        _searchInput = string.Empty;
                        GUI.FocusControl(null);
                    }
                }
            }
        });

        SearchMatchCount = 0;
        _pendingSearchHeader = null;
        _searchShowsUntitled = false;
        SearchPath.Clear();
    }

    // ends any search, putting every section back as it was; used before saving and when the menu closes
    internal static void EndSearch()
    {
        TrackSettingsOpenedBySearch();
        _searchInput = string.Empty;
        SetSearchText(string.Empty);
    }

    private static void SetSearchText(string text)
    {
        var wasSearching = IsSearching;

        SearchText = text ?? string.Empty;

        if (!wasSearching && IsSearching)
        {
            SettingsOpenedBySearch.Clear();
            _sectionForcedOpen = false;
            _sectionStatesBeforeSearch = SectionStateProperties
                .ToDictionary(x => x, x => (bool)x.GetValue(Main.Settings));
            _sectionDictionariesBeforeSearch = SectionStateDictionaries
                .Where(x => x.GetValue(Main.Settings) is IDictionary<string, bool>)
                .ToDictionary(x => x,
                    x => new Dictionary<string, bool>((IDictionary<string, bool>)x.GetValue(Main.Settings)));
            OpenAllSections();
            _boolSettingsAtFrameStart = ReadBoolSettings();
        }
        else if (wasSearching && !IsSearching && _sectionStatesBeforeSearch != null)
        {
            foreach (var state in _sectionStatesBeforeSearch)
            {
                state.Key.SetValue(Main.Settings, state.Value);
            }

            foreach (var states in _sectionDictionariesBeforeSearch ?? [])
            {
                if (states.Key.GetValue(Main.Settings) is not IDictionary<string, bool> current)
                {
                    continue;
                }

                current.Clear();

                foreach (var state in states.Value)
                {
                    current[state.Key] = state.Value;
                }
            }

            foreach (var setting in SettingsOpenedBySearch)
            {
                setting.SetValue(Main.Settings, false);
            }

            SettingsOpenedBySearch.Clear();
            _boolSettingsAtFrameStart = null;
            _sectionStatesBeforeSearch = null;
            _sectionDictionariesBeforeSearch = null;
        }
    }

    // opens every section stored in the settings, from the start of the frame the search starts in (called from
    // SearchBox in the Layout pass, before anything is drawn), so no section changes state partway through a frame.
    // Sections whose state isn't in the settings are still opened by DisclosureToggle as their header is drawn.
    private static void OpenAllSections()
    {
        foreach (var property in SectionStateProperties)
        {
            property.SetValue(Main.Settings, true);
        }

        foreach (var property in SectionStateDictionaries)
        {
            if (property.GetValue(Main.Settings) is not IDictionary<string, bool> states)
            {
                continue;
            }

            foreach (var key in states.Keys.ToList())
            {
                states[key] = true;
            }
        }
    }

    internal static void NoteSectionForcedOpen()
    {
        _sectionForcedOpen = true;
    }

    private static Dictionary<PropertyInfo, bool> ReadBoolSettings()
    {
        return BoolSettings.ToDictionary(x => x, x => (bool)x.GetValue(Main.Settings));
    }

    // a frame that forced a section open: the settings that turned on in it are that section's (a click on a real
    // setting in the very same frame would be counted too, which is unlikely as sections open on the first frame)
    private static void TrackSettingsOpenedBySearch()
    {
        if (!IsSearching || _boolSettingsAtFrameStart == null)
        {
            return;
        }

        if (_sectionForcedOpen)
        {
            foreach (var setting in BoolSettings)
            {
                if (!_boolSettingsAtFrameStart[setting] && (bool)setting.GetValue(Main.Settings) &&
                    !_sectionStatesBeforeSearch.ContainsKey(setting))
                {
                    SettingsOpenedBySearch.Add(setting);
                }
            }

            _sectionForcedOpen = false;
        }

        _boolSettingsAtFrameStart = ReadBoolSettings();
    }

    internal static void SearchResultsFooter()
    {
        if (IsSearching && SearchMatchCount == 0)
        {
            GL.Label(Gui.Format("ModUi/&SearchNoResults", SearchText.Trim()));
        }
    }

    // draws one tab or sub-page of the search results, labelled with where it lives in the menu
    private static void SearchSection(string name, Action draw)
    {
        SearchPath.Add(RichTextTags.Replace(name ?? string.Empty, string.Empty));
        _pendingSearchHeader = string.Join(" › ", SearchPath);
        _searchShowsUntitled = false;

        try
        {
            draw();
        }
        finally
        {
            SearchPath.RemoveAt(SearchPath.Count - 1);
            _pendingSearchHeader = null;
            _searchShowsUntitled = false;
        }
    }

    private static bool SearchMatches(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return RichTextTags.Replace(text, string.Empty)
            .IndexOf(SearchText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // true when a control with this text must not be drawn because it doesn't match the search
    private static bool SearchHides(params string[] texts)
    {
        if (!IsSearching || _searchBypassDepth > 0)
        {
            return false;
        }

        // an empty title (spacers, unlabeled parts) neither matches nor ends the current match
        if (texts.All(string.IsNullOrEmpty))
        {
            return !_searchShowsUntitled;
        }

        if (!Array.Exists(texts, SearchMatches))
        {
            _searchShowsUntitled = false;

            return true;
        }

        CountSearchMatch();
        _searchShowsUntitled = true;

        return false;
    }

    // controls without a title show only after a matching label, or when one of their own texts matches
    private static bool SearchHidesUntitled(params string[] ownTexts)
    {
        if (!IsSearching || _searchBypassDepth > 0 || _searchShowsUntitled)
        {
            return false;
        }

        if (ownTexts == null || !Array.Exists(ownTexts, SearchMatches))
        {
            return true;
        }

        CountSearchMatch();

        return false;
    }

    private static void CountSearchMatch()
    {
        SearchMatchCount++;

        if (_pendingSearchHeader == null)
        {
            return;
        }

        Space(10f);
        GL.Label(_pendingSearchHeader.Orange().Bold());
        _pendingSearchHeader = null;
    }

    // the parts of a matched control (its label, its reset button, ...) are drawn without being searched again
    private static void WithoutSearch(Action draw)
    {
        _searchBypassDepth++;

        try
        {
            draw();
        }
        finally
        {
            _searchBypassDepth--;
        }
    }
}
