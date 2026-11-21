using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Patches;

// Temporary diagnostics for the level-up gains screen, the after-rest Close button and the settings search Clear.
// Every line goes to the mod log with the prefix [I55Diag]. Each probe has its own line budget per launch, so one
// probe can't use up another's, and a probe writes one truncation line when its budget runs out. Nothing here
// changes what the game shows or does.
[UsedImplicitly]
public static class Issue55DiagnosticsPatcher
{
    internal const string LevelGains = "LevelGains";
    internal const string RestAfter = "RestAfter";
    internal const string Search = "Search";

    private const int LinesPerProbe = 250;
    private static readonly Dictionary<string, int> LinesUsed = new();

    // true while the probe still has budget; checked before building a message so an exhausted probe costs nothing
    internal static bool CanLog(string probe)
    {
        return !LinesUsed.TryGetValue(probe, out var used) || used <= LinesPerProbe;
    }

    internal static void Log(string probe, string msg)
    {
        LinesUsed.TryGetValue(probe, out var used);

        if (used > LinesPerProbe)
        {
            return;
        }

        LinesUsed[probe] = used + 1;

        Main.Info(used == LinesPerProbe
            ? $"[I55Diag] {probe} budget of {LinesPerProbe} lines used up; no more lines this launch"
            : $"[I55Diag] f{Time.frameCount} t{Time.realtimeSinceStartup:0.00} {probe} {msg}");
    }

    // product of every CanvasGroup alpha from the object up to the first group that ignores its parents
    private static float EffectiveAlpha(Component component)
    {
        var alpha = 1f;

        foreach (var group in component.GetComponentsInParent<CanvasGroup>(true))
        {
            alpha *= group.alpha;

            if (group.ignoreParentGroups)
            {
                break;
            }
        }

        return alpha;
    }

    // the rect's four corners in screen pixels (Unity counts y from the bottom), with the canvas mode and camera used;
    // a world-space canvas without its own camera is projected with Camera.main, as the game's raycaster does
    private static string ScreenSpan(RectTransform rect)
    {
        var corners = new Vector3[4];

        rect.GetWorldCorners(corners);

        var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        var mode = canvas ? canvas.renderMode : RenderMode.ScreenSpaceOverlay;
        var camera = mode == RenderMode.ScreenSpaceOverlay ? null : canvas!.worldCamera;

        if (!camera && mode == RenderMode.WorldSpace)
        {
            camera = Camera.main;
        }

        var points = corners.Select(c => RectTransformUtility.WorldToScreenPoint(camera, c)).ToArray();

        return
            $"x {points.Min(p => p.x):0}..{points.Max(p => p.x):0} y {points.Min(p => p.y):0}..{points.Max(p => p.y):0} " +
            $"canvas={mode} camera={(camera ? camera.name : "none")} " +
            $"viewport={(camera ? camera.pixelRect.ToString() : $"{Screen.width}x{Screen.height}")}";
    }

    private static string Describe(RectTransform rect)
    {
        if (!rect)
        {
            return "null";
        }

        return
            $"self={rect.gameObject.activeSelf} hier={rect.gameObject.activeInHierarchy} alpha={EffectiveAlpha(rect):0.00} " +
            $"anchoredY={rect.anchoredPosition.y:0.0} h={rect.rect.height:0.0} screen[{ScreenSpan(rect)}]";
    }

    #region Level gains

    // the panel refresh each sampler belongs to; a sampler stops once a newer refresh has started its own
    private static int _levelGainsRefresh;

    private static float OwnAlpha(Component component)
    {
        var group = component ? component.GetComponent<CanvasGroup>() : null;

        return group ? group.alpha : -1f;
    }

    // the panel's cached class, level and subclass, and the subclass unlocks at that level with their hidden flags
    private static string DescribeUnlocks(CharacterStageLevelGainsPanel panel)
    {
        var level = panel.lastGainedClassLevel;
        var subclass = panel.lastSubclassDefinition;
        var unlocks = subclass
            ? string.Join(", ", subclass.FeatureUnlocks
                .Where(x => x.Level == level)
                .Select(x => $"{x.FeatureDefinition?.Name} hidden={x.FeatureDefinition?.GuiPresentation?.Hidden}"))
            : "no cached subclass";

        return
            $"class={panel.lastGainedClassDefinition?.Name} level={level} subclass={subclass?.Name} " +
            $"subclassUnlocksAtLevel=[{unlocks}]";
    }

    private static string DescribeClassFeatures(CharacterStageLevelGainsPanel panel)
    {
        var rows = new StringBuilder();
        var list = panel.unlockedClassFeaturesList;

        if (list)
        {
            foreach (Transform child in list)
            {
                var item = child.GetComponent<FeatureDescriptionItem>();

                rows.Append(" | ")
                    .Append(item ? item.Feature?.Name ?? "no feature" : child.name)
                    .Append(" self=").Append(child.gameObject.activeSelf)
                    .Append(" hier=").Append(child.gameObject.activeInHierarchy);
            }
        }

        var modifier = panel.unlockedClassFeaturesModifier;
        var higher = panel.atHigherLevelModifier;

        // the reveal's GuiModifierAlpha writes the modifier object's own CanvasGroup, so that alpha is logged too
        return
            $"{DescribeUnlocks(panel)} group[{Describe(panel.unlockedClassFeaturesGroup)}] " +
            $"modifier active={(modifier ? modifier.gameObject.activeInHierarchy : null)} ownAlpha={OwnAlpha(modifier):0.00} " +
            $"higherLevel active={(higher ? higher.gameObject.activeInHierarchy : null)} ownAlpha={OwnAlpha(higher):0.00} " +
            $"rows:{rows}";
    }

    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel), nameof(CharacterStageLevelGainsPanel.FillUnlockedClassFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillUnlockedClassFeatures_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageLevelGainsPanel __instance)
        {
            if (!CanLog(LevelGains))
            {
                return;
            }

            Log(LevelGains,
                $"fill #{_levelGainsRefresh + 1}: activeFeatures={__instance.activeFeatures?.Count} " +
                DescribeClassFeatures(__instance));
        }
    }

    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel), nameof(CharacterStageLevelGainsPanel.PostRefreshCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PostRefreshCharacter_Patch
    {
        // the prefix reads the reveal's settings before the game's method starts the animations and clears them
        [UsedImplicitly]
        public static void Prefix(CharacterStageLevelGainsPanel __instance)
        {
            _levelGainsRefresh++;

            if (CanLog(LevelGains))
            {
                Log(LevelGains,
                    $"postRefresh #{_levelGainsRefresh}: startDelay={__instance.startDelay:0.00} " +
                    $"animate={__instance.animate} groups={__instance.modifierGroups?.Count}");
            }
        }

        // samples the class feature group while the timed reveal runs, to tell a row that was never bound from one
        // still fading in, and once more when the Higher Level line (revealed last) has fully appeared
        [UsedImplicitly]
        public static void Postfix(CharacterStageLevelGainsPanel __instance)
        {
            if (__instance.isActiveAndEnabled && CanLog(LevelGains))
            {
                __instance.StartCoroutine(SampleLevelGains(__instance, _levelGainsRefresh));
            }
        }

        private static IEnumerator SampleLevelGains(CharacterStageLevelGainsPanel panel, int refresh)
        {
            var start = Time.realtimeSinceStartup;
            var settledLogged = false;

            foreach (var at in new[] { 0.5f, 1.5f, 2.5f, 4f, 6f, 9f })
            {
                while (Time.realtimeSinceStartup - start < at)
                {
                    if (!panel || refresh != _levelGainsRefresh || !CanLog(LevelGains))
                    {
                        yield break;
                    }

                    if (!settledLogged && OwnAlpha(panel.atHigherLevelModifier) >= 0.99f)
                    {
                        settledLogged = true;
                        Log(LevelGains,
                            $"#{refresh} higher level settled +{Time.realtimeSinceStartup - start:0.00}s " +
                            DescribeClassFeatures(panel));
                    }

                    yield return null;
                }

                if (!panel || !panel.isActiveAndEnabled || refresh != _levelGainsRefresh || !CanLog(LevelGains))
                {
                    yield break;
                }

                Log(LevelGains, $"#{refresh} +{at:0.0}s {DescribeClassFeatures(panel)}");
            }
        }
    }

    #endregion

    #region Search Clear

    // GUI.DoButton receives the control ID Unity uses to grab the mouse on MouseDown and to check ownership on
    // MouseUp; while the settings search draws Clear, its ID and ownership before and after are handed to the
    // search diagnostics
    [HarmonyPatch(typeof(GUI), "DoButton", typeof(Rect), typeof(int), typeof(GUIContent), typeof(GUIStyle))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class GuiDoButton_Patch
    {
        [UsedImplicitly]
        public static void Prefix(int id)
        {
            if (Api.ModKit.SearchClearDiagnostics.DrawingClear)
            {
                Api.ModKit.SearchClearDiagnostics.OnDoButton(id, true);
            }
        }

        [UsedImplicitly]
        public static void Postfix(int id)
        {
            if (Api.ModKit.SearchClearDiagnostics.DrawingClear)
            {
                Api.ModKit.SearchClearDiagnostics.OnDoButton(id, false);
            }
        }
    }

    #endregion

    #region Rest Close

    private static string DescribeRestAfter(RestAfterPanel panel)
    {
        var modules = panel.restModules == null
            ? "null"
            : string.Join(",", panel.restModules.Select(x => x ? $"{x.name}:{x.gameObject.activeSelf}" : "null"));

        // the rendered button inside the Close group: its own bounds, activity and the alpha its graphic draws with
        var button = panel.closeGroup ? panel.closeGroup.GetComponentInChildren<Button>(true) : null;
        var buttonText = "null";

        if (button)
        {
            var graphic = button.targetGraphic;
            var graphicText = graphic
                ? $"graphic enabled={graphic.enabled} hier={graphic.gameObject.activeInHierarchy} " +
                  $"culled={graphic.canvasRenderer.cull} " +
                  $"alpha={graphic.canvasRenderer.GetInheritedAlpha() * graphic.color.a:0.00}"
                : "graphic=null";

            // IsInteractable also applies the parent CanvasGroups' interactable flags
            buttonText =
                $"{button.name} {Describe(button.transform as RectTransform)} interactable={button.interactable} " +
                $"isInteractable={button.IsInteractable()} isActiveAndEnabled={button.isActiveAndEnabled} {graphicText}";
        }

        return
            $"closeGroup[{Describe(panel.closeGroup)}] closeButton[{buttonText}] table[{Describe(panel.RestModulesTable)}] " +
            $"modules={modules}";
    }

    [HarmonyPatch(typeof(RestAfterPanel), nameof(RestAfterPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RestAfterRefresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RestAfterPanel __instance)
        {
            if (CanLog(RestAfter))
            {
                Log(RestAfter, $"Refresh: {DescribeRestAfter(__instance)}");
            }
        }
    }

    [HarmonyPatch(typeof(RestAfterPanel), nameof(RestAfterPanel.OnBeginShow))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RestAfterOnBeginShow_Patch
    {
        // runs after the mod's own OnBeginShow postfix, which hides the recovered features module
        [UsedImplicitly]
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(RestAfterPanel __instance)
        {
            if (!CanLog(RestAfter))
            {
                return;
            }

            Log(RestAfter, $"OnBeginShow end: {DescribeRestAfter(__instance)}");

            if (__instance.isActiveAndEnabled)
            {
                __instance.StartCoroutine(SampleRestAfter(__instance));
            }
        }

        private static IEnumerator SampleRestAfter(RestAfterPanel panel)
        {
            var start = Time.realtimeSinceStartup;

            foreach (var at in new[] { 0.5f, 2f })
            {
                while (Time.realtimeSinceStartup - start < at)
                {
                    yield return null;
                }

                if (!panel || !panel.isActiveAndEnabled || !CanLog(RestAfter))
                {
                    yield break;
                }

                Log(RestAfter, $"+{at:0.0}s {DescribeRestAfter(panel)}");
            }
        }
    }

    #endregion
}
