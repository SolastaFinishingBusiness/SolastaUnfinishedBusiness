using SolastaUnfinishedBusiness.Api.ModKit;

namespace SolastaUnfinishedBusiness.Displays;

// The Logging tab (user, 2026-11-05: "Add a new tab for logging and gate additional logging behind it. Turn it on by
// default."). These switches only decide what gets recorded, never what happens in the game, so computers with different
// choices still play the same. As with every setting, a client in a multiplayer room uses the host's choices while there.
internal static class LoggingDisplay
{
    internal static void DisplayLogging()
    {
        UI.Label();
        UI.Label(Gui.Localize("ModUi/&LoggingHelp"));
        // user, 2026-11-05: the host deciding the clients' logging is fine, with a warning
        UI.Label(Gui.Localize("ModUi/&LoggingHostWarning"));
        UI.Label();

        var toggle = Main.Settings.EnableAdditionalLogging;
        if (UI.Toggle(Gui.Localize("ModUi/&EnableAdditionalLogging"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.EnableAdditionalLogging = toggle;
        }

        if (!Main.Settings.EnableAdditionalLogging)
        {
            return;
        }

        UI.Label();

        toggle = Main.Settings.LogObjectIdRecords;
        if (UI.Toggle(Gui.Localize("ModUi/&LogObjectIdRecords"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.LogObjectIdRecords = toggle;
        }

        toggle = Main.Settings.LogStaleEffectChecks;
        if (UI.Toggle(Gui.Localize("ModUi/&LogStaleEffectChecks"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.LogStaleEffectChecks = toggle;
        }

        toggle = Main.Settings.LogPerception;
        if (UI.Toggle(Gui.Localize("ModUi/&LogPerception"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.LogPerception = toggle;
        }

        // what AI characters do with cells in a multiplayer battle, for comparing logs (AiMoveLogContext)
        toggle = Main.Settings.LogAiMoves;
        if (UI.Toggle(Gui.Localize("ModUi/&LogAiMoves"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.LogAiMoves = toggle;
        }

        // the travel encounter popup and what ends a game while it's up (issue 11, EncounterPopupContext)
        toggle = Main.Settings.LogEncounterPopup;
        if (UI.Toggle(Gui.Localize("ModUi/&LogEncounterPopup"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.LogEncounterPopup = toggle;
        }

        // the cells a push or pull goes through and what the areas there did (issue #29, ForcedMovementContext)
        toggle = Main.Settings.LogForcedMovement;
        if (UI.Toggle(Gui.Localize("ModUi/&LogForcedMovement"), ref toggle, UI.AutoWidth()))
        {
            Main.Settings.LogForcedMovement = toggle;
        }
    }
}
