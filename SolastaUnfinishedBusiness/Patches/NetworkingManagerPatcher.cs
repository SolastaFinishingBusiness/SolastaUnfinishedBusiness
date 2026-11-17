using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using JetBrains.Annotations;
using Photon.Pun;

namespace SolastaUnfinishedBusiness.Patches;

[UsedImplicitly]
public static class NetworkingManagerPatcher
{
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.CreateRoom))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class CreateOfflineRoomIfNeeded_Patch
    {
        [UsedImplicitly]
        public static void Prefix(ref NetworkingDefinitions.RoomInfo roomInfo)
        {
            //PATCH: allows up to 6 players to join the game if there are enough heroes available (PARTYSIZE)
            roomInfo.maxPlayers = Main.Settings.OverridePartySize;
        }
    }

    // Undoing an unlearned invocation during a level-up never applied in multiplayer (issue 9's second part). The game's
    // NetworkingManager.UndoUnlearnInvocation sends UndoUnlearnInvocationRPC with 4 values (tick, hero, tag, definition
    // name), but the handler takes 5: it also has an Int32 featureType, which it doesn't use. Photon rejects the call on every
    // computer, the sender included ("UndoUnlearnInvocationRPC ... has wrong parameters" in the log), so the hero keeps
    // the unlearned invocation. The mod's custom invocation panel calls
    // this for each custom invocation it resets. Sends the same message with the fifth value; the handler then runs the
    // game's own undo on every computer at the same tick, as the other level-up commands do.
    [HarmonyPatch(typeof(NetworkingManager), nameof(NetworkingManager.UndoUnlearnInvocation))]
    [SuppressMessage("Minor Code Smell", "S101:Types should be named in PascalCase", Justification = "Patch")]
    [UsedImplicitly]
    public static class UndoUnlearnInvocation_Patch
    {
        [UsedImplicitly]
        public static bool Prefix(
            NetworkingManager __instance, RulesetCharacterHero hero, string tag, string definitionName)
        {
            __instance.view.RPC(nameof(NetworkingManager.UndoUnlearnInvocationRPC), RpcTarget.AllViaServer,
                __instance.CommandTick, (long)hero.Guid, tag, definitionName, 0);

            return false;
        }
    }
}
