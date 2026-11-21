using SolastaUnfinishedBusiness.Patches;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.ModKit;

// Temporary diagnostics for the settings search Clear button. On mouse events while Clear is drawn it logs the
// event (raw and effective type before and after the button), the button's rect (and the last Repaint's), Unity's
// control ID for the button and whether it held the mouse before and after, mouse and keyboard ownership and
// the button's result; after a successful Clear it logs the cleared state, the next Layout's input and published
// query, and the first non-empty value the text field returns afterwards. Lines go to the mod log with the prefix
// [I55Diag].
internal static class SearchClearDiagnostics
{
    private static bool _watchAfterClear;
    private static bool _logNextLayout;

    // the Clear rect from the last Repaint, with its frame, to compare when a mouse event arrives already used
    private static Rect _lastRepaintRect;
    private static int _lastRepaintFrame = -1;

    // Unity's own MouseUp test in GUI.DoControl; internal, so it is called through reflection
    private static readonly System.Func<int, bool> HasMouseControl =
        (System.Func<int, bool>)System.Delegate.CreateDelegate(typeof(System.Func<int, bool>),
            HarmonyLib.AccessTools.Method(typeof(GUI), "HasMouseControl", [typeof(int)]));

    // filled by the GUI.DoButton hook for the current Clear call only; reset before each call, so a hook that did
    // not run shows as unavailable instead of an earlier call's values
    private static string _captureBefore;
    private static string _captureAfter;

    internal static bool DrawingClear { get; private set; }

    internal static void BeginClear()
    {
        _captureBefore = null;
        _captureAfter = null;
        DrawingClear = true;
    }

    internal static void EndClear()
    {
        DrawingClear = false;
    }

    internal static void OnDoButton(int id, bool before)
    {
        var capture = $"id={id} hasMouseControl={HasMouseControl(id)} hot={GUIUtility.hotControl}";

        if (before)
        {
            _captureBefore = capture;
        }
        else
        {
            _captureAfter = capture;
        }
    }

    internal struct State
    {
        internal EventType RawType;
        internal EventType Type;
        internal Vector2 Mouse;
        internal int HotBefore;
        internal int KeyboardBefore;
        internal string Focused;
    }

    private static bool IsMouseEvent(EventType type)
    {
        return type is EventType.MouseDown or EventType.MouseUp;
    }

    internal static void AfterTextField(string before, string after)
    {
        if (before == after || !Issue55DiagnosticsPatcher.CanLog(Issue55DiagnosticsPatcher.Search))
        {
            return;
        }

        Log($"TextField changed on {Event.current.rawType}/{Event.current.type}: '{before}' -> '{after}' " +
            $"keyboard={GUIUtility.keyboardControl}");

        if (_watchAfterClear && !string.IsNullOrEmpty(after))
        {
            Log("TextField first non-empty value after a Clear");
            _watchAfterClear = false;
        }
    }

    internal static State BeforeClear()
    {
        var current = Event.current;

        return new State
        {
            RawType = current.rawType,
            Type = current.type,
            Mouse = current.mousePosition,
            HotBefore = GUIUtility.hotControl,
            KeyboardBefore = GUIUtility.keyboardControl,
            Focused = GUI.GetNameOfFocusedControl()
        };
    }

    // hotControl after a MouseDown the button took is the button's own control ID
    internal static void AfterClear(State state, Rect rect, bool clicked, string input)
    {
        if (state.Type == EventType.Repaint)
        {
            _lastRepaintRect = rect;
            _lastRepaintFrame = Time.frameCount;
        }

        if ((!clicked && !IsMouseEvent(state.RawType)) ||
            !Issue55DiagnosticsPatcher.CanLog(Issue55DiagnosticsPatcher.Search))
        {
            return;
        }

        // GetRect returns a placeholder when the event was already used by an earlier control
        var rectText = state.Type == EventType.Used
            ? "unavailable (event already used)"
            : $"{rect} inside={rect.Contains(state.Mouse)}";

        Log($"Clear on {state.RawType}/{state.Type}->{Event.current.type}: clicked={clicked} mouse={state.Mouse} " +
            $"rect={rectText} lastRepaintRect={_lastRepaintRect} (f{_lastRepaintFrame}) " +
            $"inLastRepaintRect={_lastRepaintRect.Contains(state.Mouse)} " +
            $"doButton before[{_captureBefore ?? "unavailable"}] after[{_captureAfter ?? "unavailable"}] hot {state.HotBefore}->{GUIUtility.hotControl} " +
            $"keyboard {state.KeyboardBefore}->{GUIUtility.keyboardControl} focused='{state.Focused}' input='{input}'");
    }

    internal static void AfterClearApplied(string input)
    {
        _watchAfterClear = true;
        _logNextLayout = true;
        Log($"Clear applied: input='{input}' keyboard={GUIUtility.keyboardControl} " +
            $"focused='{GUI.GetNameOfFocusedControl()}'");
    }

    internal static void OnLayout(string input, string published)
    {
        if (!_logNextLayout)
        {
            return;
        }

        _logNextLayout = false;
        Log($"next Layout after Clear: input='{input}' published='{published}' keyboard={GUIUtility.keyboardControl}");
    }

    private static void Log(string msg)
    {
        Issue55DiagnosticsPatcher.Log(Issue55DiagnosticsPatcher.Search, msg);
    }
}
