using HarmonyLib;
using TA;
using TA.AI;
using TA.AI.Activities;

namespace SolastaUnfinishedBusiness.Models;

// "[AiMove]" lines: what each AI-controlled character did with cells in a multiplayer battle, so two
// players' logs can be compared line by line to find where their games split. The host's AI sends its decision to
// everyone, but some details are worked out on each computer, such as where MovePathTowardsTarget stops (it reads that
// computer's own visibility service and the light); a Brood of Blood ended in different cells on two computers in desync
// issue 04 with nothing in the logs to say why.
// - every action an AI activity runs that has target cells (moves, teleports, cell-targeted spells), from
//   ActivityBase.ExecuteAndWait: round, actor, activity, action, from where, to which cells;
// - every stopping cell MovePathTowardsTarget works out, with whether it avoided light and the actor's lighting now.
// Logging only: nothing here changes the game. Multiplayer only, behind Logging > "AI moves" (on by default, like the
// perception lines), and at most MaxLinesPerBattle lines per battle.
internal static class AiMoveLogContext
{
    private const int MaxLinesPerBattle = 200;

    private static GameLocationBattle _battle;
    private static int _lines;

    // GameDestroyed (GameManagerPatcher): the battle belongs to the game that's gone
    internal static void ForgetGame()
    {
        _battle = null;
        _lines = 0;
    }

    private static bool IsOn =>
        Global.IsMultiplayer && Main.Settings is { EnableAdditionalLogging: true, LogAiMoves: true };

    private static void Log(string message)
    {
        var battle = Gui.Battle;

        if (battle == null)
        {
            return;
        }

        // a new battle starts a new count
        if (battle != _battle)
        {
            _battle = battle;
            _lines = 0;
        }

        if (_lines > MaxLinesPerBattle)
        {
            return;
        }

        _lines++;

        Main.Info(_lines > MaxLinesPerBattle
            ? $"[AiMove] {MaxLinesPerBattle} lines this battle; no more until the next one"
            : $"[AiMove] round {battle.CurrentRound} {message}");
    }

    private static string Name(GameLocationCharacter character)
    {
        return character == null ? "?" : $"{character.Name} ({character.Guid})";
    }

    internal static void OnActivityAction(ActivityBase activity, CharacterActionParams actionParams)
    {
        if (!IsOn || actionParams?.ActingCharacter is not { } actor || actionParams.Positions is not { Count: > 0 })
        {
            return;
        }

        Log($"{Name(actor)} {activity?.GetType().Name} {actionParams.ActionDefinition?.Id} from " +
            $"{actor.LocationPosition} to {string.Join(" ", actionParams.Positions)}");
    }

    internal static void OnDestinationChosen(
        AiLocationCharacter character, bool avoidLight, int3 destination, LocationDefinitions.Orientation orientation)
    {
        if (!IsOn || character?.GameLocationCharacter is not { } actor)
        {
            return;
        }

        Log($"{Name(actor)} chose {destination} facing {orientation} from {actor.LocationPosition} " +
            $"(avoid light {avoidLight}, lighting now {actor.LightingState})");
    }

    [HarmonyPatch(typeof(ActivityBase), nameof(ActivityBase.ExecuteAndWait))]
    private static class ExecuteAndWaitPatch
    {
        private static void Prefix(ActivityBase __instance, CharacterActionParams actionParams)
        {
            MultiplayerContext.Guarded("logging an AI action", () => OnActivityAction(__instance, actionParams));
        }
    }

    [HarmonyPatch(typeof(MovePathTowardsTarget), nameof(MovePathTowardsTarget.ComputeDestinationAndOrientation))]
    private static class ComputeDestinationPatch
    {
        private static void Postfix(
            AiLocationCharacter character, bool avoidLight, ref int3 destination,
            ref LocationDefinitions.Orientation orientation)
        {
            var chosen = destination;
            var facing = orientation;

            MultiplayerContext.Guarded("logging an AI destination",
                () => OnDestinationChosen(character, avoidLight, chosen, facing));
        }
    }
}
