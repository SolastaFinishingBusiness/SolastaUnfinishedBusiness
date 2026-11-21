using SolastaUnfinishedBusiness.Patches;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Api.ModKit;

// Temporary diagnostics for the settings search Clear button: on mouse events over the search row it logs the IMGUI
// event, the Clear button's rect and control ownership, the button's result, and every later change of the field's
// text until the next non-empty value after a Clear. Lines go to the mod log with the prefix [I55Diag].
internal static class SearchClearDiagnostics
{
    private static bool _watchAfterClear;

    internal struct State
    {
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
        var type = Event.current.rawType;

        if (before != after)
        {
            Log($"TextField changed on {type}: '{before}' -> '{after}' keyboard={GUIUtility.keyboardControl}");

            if (_watchAfterClear && !string.IsNullOrEmpty(after))
            {
                Log("TextField first non-empty value after a Clear");
                _watchAfterClear = false;
            }
        }
    }

    internal static State BeforeClear()
    {
        var current = Event.current;

        return new State
        {
            Type = current.rawType,
            Mouse = current.mousePosition,
            HotBefore = GUIUtility.hotControl,
            KeyboardBefore = GUIUtility.keyboardControl,
            Focused = GUI.GetNameOfFocusedControl()
        };
    }

    internal static void AfterClear(State state, bool clicked, string input)
    {
        if (!clicked && !IsMouseEvent(state.Type))
        {
            return;
        }

        // the rect of the button just drawn; on Layout passes it is a placeholder, so only mouse passes are logged
        var rect = GUILayoutUtility.GetLastRect();

        Log($"Clear on {state.Type}: clicked={clicked} mouse={state.Mouse} rect={rect} inside={rect.Contains(state.Mouse)} " +
            $"hot {state.HotBefore}->{GUIUtility.hotControl} keyboard {state.KeyboardBefore}->{GUIUtility.keyboardControl} " +
            $"focused='{state.Focused}' input='{input}' used={Event.current.type == EventType.Used}");

        if (clicked)
        {
            _watchAfterClear = true;
        }
    }

    private static void Log(string msg)
    {
        Issue55DiagnosticsPatcher.Log($"Search {msg}");
    }
}
