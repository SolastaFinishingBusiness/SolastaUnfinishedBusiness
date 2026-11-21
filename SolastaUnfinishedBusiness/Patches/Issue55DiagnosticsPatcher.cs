using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using HarmonyLib;
using JetBrains.Annotations;
using TMPro;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Patches;

// Temporary diagnostics for the level-up gains screen and the after-rest Close button. Every line goes to the mod
// log with the prefix [I55Diag]; lines per launch are capped so a long session cannot flood the log. Nothing here
// changes what the game shows or does.
[UsedImplicitly]
public static class Issue55DiagnosticsPatcher
{
    private const int MaxLines = 600;
    private static int _lines;

    internal static void Log(string msg)
    {
        if (_lines >= MaxLines)
        {
            return;
        }

        _lines++;
        Main.Info($"[I55Diag] f{Time.frameCount} t{Time.realtimeSinceStartup:0.00} {msg}");
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

    // screen-space bottom and top of a rect, in pixels from the bottom of the screen as Unity counts them
    private static string ScreenSpan(RectTransform rect)
    {
        if (!rect)
        {
            return "null";
        }

        var corners = new Vector3[4];

        rect.GetWorldCorners(corners);

        var canvas = rect.GetComponentInParent<Canvas>()?.rootCanvas;
        var camera = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var bottom = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        var top = RectTransformUtility.WorldToScreenPoint(camera, corners[1]);

        return $"y {bottom.y:0}..{top.y:0} x {bottom.x:0}";
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

    private static string DescribeClassFeatures(CharacterStageLevelGainsPanel panel)
    {
        var list = panel.unlockedClassFeaturesList;
        var rows = new StringBuilder();

        if (list)
        {
            foreach (Transform child in list)
            {
                if (!child.gameObject.activeSelf)
                {
                    continue;
                }

                var text = child.GetComponentInChildren<TMP_Text>(true)?.text ?? "?";

                rows.Append(" | ").Append(child.name).Append(": ").Append(text);
            }
        }

        return
            $"group[{Describe(panel.unlockedClassFeaturesGroup)}] modifierActive={panel.unlockedClassFeaturesModifier?.gameObject.activeInHierarchy} " +
            $"higherLevel={panel.atHigherLevelGroup?.gameObject.activeInHierarchy} " +
            $"higherLevelAlpha={(panel.atHigherLevelGroup ? EffectiveAlpha(panel.atHigherLevelGroup) : -1):0.00} rows:{rows}";
    }

    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel), nameof(CharacterStageLevelGainsPanel.FillUnlockedClassFeatures))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class FillUnlockedClassFeatures_Patch
    {
        [UsedImplicitly]
        public static void Postfix(CharacterStageLevelGainsPanel __instance)
        {
            var level = __instance.lastGainedClassLevel;
            var subclass = __instance.lastSubclassDefinition;
            var subclassUnlocks = subclass
                ? string.Join(", ", subclass.FeatureUnlocks
                    .Where(x => x.Level == level)
                    .Select(x => $"{x.FeatureDefinition?.Name} hidden={x.FeatureDefinition?.GuiPresentation?.Hidden}"))
                : "no cached subclass";

            Log($"LevelGains fill: class={__instance.lastGainedClassDefinition?.Name} level={level} " +
                $"subclass={subclass?.Name} subclassUnlocksAtLevel=[{subclassUnlocks}] " +
                $"activeFeatures={__instance.activeFeatures?.Count} {DescribeClassFeatures(__instance)}");
        }
    }

    [HarmonyPatch(typeof(CharacterStageLevelGainsPanel), nameof(CharacterStageLevelGainsPanel.PostRefreshCharacter))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class PostRefreshCharacter_Patch
    {
        // samples the class feature group while the panel's timed reveal runs, to tell a row that was never bound
        // from one that was still fading in
        [UsedImplicitly]
        public static void Postfix(CharacterStageLevelGainsPanel __instance)
        {
            Log($"LevelGains postRefresh: startDelay={__instance.startDelay:0.00} animate={__instance.animate} " +
                $"groups={__instance.modifierGroups?.Count}");

            if (__instance.isActiveAndEnabled)
            {
                __instance.StartCoroutine(SampleLevelGains(__instance));
            }
        }

        private static IEnumerator SampleLevelGains(CharacterStageLevelGainsPanel panel)
        {
            var start = Time.realtimeSinceStartup;

            foreach (var at in new[] { 0.5f, 1.5f, 2.5f, 4f, 6f })
            {
                while (Time.realtimeSinceStartup - start < at)
                {
                    yield return null;
                }

                if (!panel || !panel.isActiveAndEnabled)
                {
                    yield break;
                }

                Log($"LevelGains +{at:0.0}s {DescribeClassFeatures(panel)}");
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

        return
            $"close[{Describe(panel.closeGroup)}] table[{Describe(panel.RestModulesTable)}] screenH={Screen.height} modules={modules}";
    }

    [HarmonyPatch(typeof(RestAfterPanel), nameof(RestAfterPanel.Refresh))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class RestAfterRefresh_Patch
    {
        [UsedImplicitly]
        public static void Postfix(RestAfterPanel __instance)
        {
            Log($"RestAfter Refresh: {DescribeRestAfter(__instance)}");
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
            Log($"RestAfter OnBeginShow end: {DescribeRestAfter(__instance)}");

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

                if (!panel || !panel.isActiveAndEnabled)
                {
                    yield break;
                }

                Log($"RestAfter +{at:0.0}s {DescribeRestAfter(panel)}");
            }
        }
    }

    #endregion
}
