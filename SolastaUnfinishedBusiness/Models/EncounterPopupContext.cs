namespace SolastaUnfinishedBusiness.Models;

// Issue 11 (a host in a six-player game): the travel encounter popup's Proceed did nothing, 9 times,
// each click a NullReferenceException at EncounterRequestModal.OnProceedCb IL_0005. That is Gui.Game being null: the
// game had been destroyed (GameManager.DestroyGameAsync, from a load or an exit) or the game service was gone
// (GameManager.Shutdown), while the popup stayed on screen. The game never closes this popup when the game goes: it hides
// only when the travel interruption ends, an encounter starts or a player joins a game, and GuiGameScreen.GameDestroying
// only unhooks. Why the game went under the player is unknown: that launch's Player.log was lost
// (evidence/encounter-proceed-issue11/FINDINGS.md).
//
// So, in EncounterRequestModalPatcher: the popup closes when the game is destroyed, its buttons do nothing when there is
// no game, and this logs what the popup saw, so the next occurrence shows the cause. The guards only act when there is
// no game, so they change nothing in a working game. The log lines are "[EncounterPopup]" in the mod's log, switched with
// Logging > "Encounter popup" and capped per launch.
internal static class EncounterPopupContext
{
    private const int MaxLines = 64;
    private static int _lines;

    // the popup on screen, or null; events elsewhere (loads, exits, disconnects) are logged only while it's up
    internal static EncounterRequestModal Visible { get; set; }

    internal static void Log(string message)
    {
        if (Main.Settings is not { EnableAdditionalLogging: true, LogEncounterPopup: true } || _lines >= MaxLines)
        {
            return;
        }

        _lines++;
        Main.Info($"[EncounterPopup] {message} ({State()})");
    }

    // what a click on the popup depends on: the game service, the game, and the game's state
    internal static string State()
    {
        var service = ServiceRepository.GetService<IGameService>();
        var game = service?.Game;
        var exists = game != null;
        var state = exists ? game.FiniteStateMachine?.CurrentState?.GetType().Name ?? "none" : "-";

        return $"service={(service != null ? "yes" : "no")}, game={(exists ? "yes" : "no")}, state={state}, " +
               $"multiplayer={Global.IsMultiplayer}";
    }
}
