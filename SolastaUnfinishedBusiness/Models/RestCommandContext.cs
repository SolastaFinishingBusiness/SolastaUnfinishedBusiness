using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// After-rest buttons (Memorize Spell, Spell Mastery, Signature Spells and the mod's custom rest powers) run as functors
// on the computer of the player who clicks them only: the game's AfterRestActionItem calls IFunctorService locally,
// and only the functor itself can tell the other computers. The game's own functors do that (FunctorUsePower sends
// its power through ICommandService, the spell preparation screen sends PrepareSpells). A functor that adds and removes
// conditions or changes prepared spells directly changes only the clicker's game, and a condition added that way also
// takes an object ID on one computer only (desync issue 04: a ConditionMemorizeSpell on one client).
//
// The changes the mod's functors make to the hero therefore go through this command: it rides on the game's own
// AssignClassLevelRPC in place of a class name (like the level down and the end-infusion commands), so every computer
// runs it at the same tick. The functor on the clicker sends it, waits until its own copy has run, and only then
// carries on (opening the preparation screen, for example). Single player runs the change at once.
internal static class RestCommandContext
{
    private const string Marker = "UbRestCommand:";

    // a command that hasn't come back by then is given up, so a lost connection can't keep the rest screen waiting
    private const float WaitSeconds = 30f;

    // the commands this computer sent and is waiting for, registered before sending: null until its own copy has run,
    // then whether it did. Only the sender's waits are kept (other computers' commands aren't recorded), each removed
    // when its wait ends, and all of them on joining a room
    private static readonly Dictionary<string, bool?> Waiting = new();

    private static readonly Dictionary<string, Func<RulesetCharacterHero, string, bool>> Handlers = new();

    private static long _sequence;

    // a new room: waits from the last one can't end
    internal static void OnJoinedRoom()
    {
        Waiting.Clear();
    }

    // a handler gets the hero and the command's argument and says whether it did what was asked; it runs on every
    // computer at the command's tick, so it must only use game state (never this computer's screens or input), and
    // every computer gets the same answer
    internal static void Register(string verb, Func<RulesetCharacterHero, string, bool> handler)
    {
        Handlers[verb] = handler;
    }

    // the outcome of a command on this computer
    internal enum Outcome
    {
        // ran here, so it ran (or will run) on every computer at the same tick
        Ran,

        // not run here and not anywhere: never sent, or every computer skipped it alike (hero or command not found)
        Failed,

        // sent but not back after WaitSeconds; it may still run later, alike on every computer
        TimedOut
    }

    // runs the command on every computer and waits (as a coroutine step) until it has run on this one; the outcome
    // tells the caller whether to carry on
    // waitSeconds: how long to wait for it here (WaitSeconds unless the caller needs less)
    internal static IEnumerator Send(
        RulesetCharacterHero hero, string verb, string argument, Action<Outcome> done, float waitSeconds = WaitSeconds)
    {
        if (!Handlers.TryGetValue(verb, out var handler))
        {
            Main.Error($"[RestCommand] unknown command {verb}");
            done?.Invoke(Outcome.Failed);
            yield break;
        }

        if (!Global.IsMultiplayer)
        {
            // run first, then report: inside done?.Invoke(...) the call would be skipped with no callback
            var handled = handler(hero, argument ?? string.Empty);

            done?.Invoke(handled ? Outcome.Ran : Outcome.Failed);
            yield break;
        }

        // multiplayer without the game's networking: running it here alone is exactly the desync this avoids
        if (ServiceRepository.GetService<INetworkingService>() is not NetworkingManager { view: not null } networking)
        {
            Main.Error($"[RestCommand] {verb} for {hero.Name} not sent: no network in a multiplayer game");
            done?.Invoke(Outcome.Failed);
            yield break;
        }

        // unique per sender: the actor number keeps two players' commands apart
        var nonce = $"{PhotonNetwork.LocalPlayer?.ActorNumber ?? 0}-{++_sequence}";

        Waiting[nonce] = null;

        networking.view.RPC(nameof(NetworkingManager.AssignClassLevelRPC), RpcTarget.AllViaServer,
            networking.CommandTick, (long)hero.Guid, $"{Marker}{nonce}:{verb}:{argument}", false);
        PhotonNetwork.SendAllOutgoingCommands();

        var deadline = Time.realtimeSinceStartup + waitSeconds;
        bool? ran = null;

        while (Global.IsMultiplayer && Time.realtimeSinceStartup < deadline &&
               Waiting.TryGetValue(nonce, out ran) && ran == null)
        {
            yield return null;
        }

        Waiting.TryGetValue(nonce, out ran);
        Waiting.Remove(nonce);

        Outcome outcome;

        if (ran == null)
        {
            outcome = Outcome.TimedOut;
            Main.Error($"[RestCommand] {verb} for {hero.Name} hasn't run here after {waitSeconds} s; not carrying on");
        }
        else if (ran == false)
        {
            outcome = Outcome.Failed;
            Main.Error($"[RestCommand] {verb} for {hero.Name} couldn't run; not carrying on");
        }
        else
        {
            outcome = Outcome.Ran;
        }

        done?.Invoke(outcome);
    }

    // AssignClassLevelAsync replacement for this command's marker (see MultiplayerPatcher)
    internal static bool TryHandleCommand(
        NetworkingManager networking, long heroGuid, string classDefinitionName, ref IEnumerator result)
    {
        if (classDefinitionName == null || !classDefinitionName.StartsWith(Marker))
        {
            return false;
        }

        // nonce:verb:argument, where the argument may itself contain colons
        var parts = classDefinitionName.Substring(Marker.Length).Split([':'], 3);

        if (parts.Length != 3)
        {
            return false;
        }

        result = RunAsync(networking, heroGuid, parts[0], parts[1], parts[2]);

        return true;
    }

    private static IEnumerator RunAsync(
        NetworkingManager networking, long heroGuid, string nonce, string verb, string argument)
    {
        var hero = networking.GetEntityFromGuid<RulesetCharacterHero>(heroGuid, false);
        var ran = false;

        if (hero != null && Handlers.TryGetValue(verb, out var handler))
        {
            try
            {
                ran = handler(hero, argument);
            }
            catch (Exception ex)
            {
                Main.Error(ex);
            }
        }
        else
        {
            Main.Error($"[RestCommand] {verb} for hero {heroGuid} not run: hero or command not found");
        }

        // recorded even when it didn't run, so the sender stops waiting; every computer got the same answer
        // recorded only where its sender is waiting for it (this computer's own command)
        if (Waiting.ContainsKey(nonce))
        {
            Waiting[nonce] = ran;
        }

        yield break;
    }
}
