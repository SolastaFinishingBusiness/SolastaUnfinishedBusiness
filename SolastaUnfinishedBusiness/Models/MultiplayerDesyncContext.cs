using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using TA;
using UnityEngine;

namespace SolastaUnfinishedBusiness.Models;

// Game-state fingerprint for multiplayer desyncs. Every few seconds each computer sends, for the same tick, each
// character's HP, temporary HP, position, conditions and spell slots plus the position in the shared dice stream.
// Every computer compares itself with the host. A difference seen twice in a row is a desync: it names the character
// and what differs, once per desync rather than at every check. Nothing is changed automatically: the host is offered a
// resync, in a message and as a button in the game log that stays usable, which makes a new recovery save that every client
// is brought back into, loaded by its exact file, never an older save in its place.
internal static class MultiplayerDesyncContext
{
    private const byte EventCode = 171;
    private const byte FingerprintKind = 1;
    private const byte ResyncKind = 3;
    private const byte ResyncCancelledKind = 6;
    private const ulong FingerprintTickInterval = 100;
    private const int ValuesPerCharacter = 15;

    // Marks a game log line whose button starts a resync. The game's own actions are 0 to 2, and it hides the button of its
    // own load action in multiplayer, so this needs one of its own. The line isn't saved with the game.
    private const GameConsoleEntry.ConsoleClickAction ResyncClickAction = (GameConsoleEntry.ConsoleClickAction)100;

    // A resync that was sent waits this long for the game's load to begin before another can be started. A load that begins
    // resets this through the tick reset. If the game's file sync then fails, the game itself leaves the session.
    private const float ResyncWaitSeconds = 30f;

    // recovery saves take turns in a few slots of their own, so they never replace a player's save
    private const string RecoverySavePrefix = "FB Recovery ";
    private const int RecoverySaveSlots = 3;

    private static readonly Dictionary<ulong, Fingerprint> LocalByTick = [];

    // The host's reports on a client: kept by tick within a window around the local tick, however they arrive. Lockstep keeps
    // computers far closer than one check apart, so the window is one check: a report from before a load, when its sender
    // reset later, then rarely fits (only in a load's first check, and a difference needs two checks to count).
    private static readonly BoundedTickReports<Fingerprint> HostReports = new(8, FingerprintTickInterval);
    private static readonly Dictionary<int, HashSet<string>> PreviousDifferencesByActor = [];

    // On the host: clients' checks that arrived before the host reached their tick. Ticks only stay close, so either side can
    // be first, and dropping these would leave the host without a report while the client already sees the difference.
    private static readonly Dictionary<int, BoundedTickReports<Fingerprint>> ClientReports = [];

    // The desync last reported for each player, so the same one isn't reported again. A map change doesn't clear this, as the
    // game still differs after one. Only a passing check does, or a new game (see ForgetIfNewGame).
    private static readonly Dictionary<int, string> ReportedByActor = [];

    // players whose condition IDs were already logged as differing, until a check finds them equal
    private static readonly HashSet<int> ConditionIdsReported = [];

    // players whose rounds per source were already logged as differing, until a check finds them equal
    private static readonly HashSet<int> ConditionRoundsReported = [];

    private static readonly FieldInfo[] RandomStateFields =
        typeof(UnityEngine.Random.State).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(x => x.FieldType == typeof(int))
            .OrderBy(x => x.Name)
            .ToArray();

    private static bool _eventHandlerRegistered;
    private static ulong _lastFingerprintTick = ulong.MaxValue;
    private static bool _resyncOffered;
    private static string _lastRecoverySave;
    private static string _lastRecoverySaveCampaign;
    private static float _resyncWaitUntil;

    // the entity ID check found a gap, which only a load clears, so a Resync button for it is still useful
    private static bool _idDesyncReported;

    // the game the two above are about; a load from a save makes a new one
    private static Game _reportedGame;

    // A controller has no log button to come back to, so the offer comes again at the first check when the game can save.
    private static bool _reofferWhenPossible;

    internal static void Reset()
    {
        LocalByTick.Clear();
        HostReports.Prune(ulong.MaxValue);
        PreviousDifferencesByActor.Clear();
        ConditionIdsReported.Clear();
        ConditionRoundsReported.Clear();
        _lastFingerprintTick = ulong.MaxValue;
        _resyncOffered = false;
        _resyncWaitUntil = 0f;
        _reofferWhenPossible = Gui.GamepadActive && ReportedByActor.Count > 0;
        ClientReports.Clear();
    }

    // A load from a save or a new session makes a new game, and what was reported about the old one is over.
    private static void ForgetIfNewGame()
    {
        var game = Gui.Game;

        if (ReferenceEquals(game, _reportedGame))
        {
            return;
        }

        _reportedGame = game;
        ReportedByActor.Clear();
        _idDesyncReported = false;
        _turnDesyncReported = false;
        _resyncLaterTold = false;
        TurnMismatchPlayers.Clear();
        TurnsReported.Clear();
        HostTurns.Clear();
        ClientTurns.Clear();
        _reofferWhenPossible = false;
    }

    // The game is going away: nothing here should keep it alive, or go on describing it, until the next one.
    internal static void GameDestroyed()
    {
        // the turn-end check's numbering and records belong to the game that's gone
        _turnGame = null;
        _turnBattle = null;
        _turnBattleNumber = 0;
        TurnsInRound.Clear();
        HostTurns.Clear();
        ClientTurns.Clear();
        TurnsReported.Clear();

        _reportedGame = null;
        ReportedByActor.Clear();
        _idDesyncReported = false;
        _turnDesyncReported = false;
        _resyncLaterTold = false;
        TurnMismatchPlayers.Clear();
        TurnsReported.Clear();
        HostTurns.Clear();
        ClientTurns.Clear();
        _reofferWhenPossible = false;
    }

    private static bool IsIronMan =>
        ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign is { IronManMode: true };

    #region Sending

    internal static void SendFingerprintIfNeeded(NetworkingManager networking)
    {
        // Before the settings below: a resync can also come from the entity ID check with these checks off, and clients still
        // have to hear about it.
        if (PhotonNetwork.InRoom)
        {
            RegisterEventHandler();
        }

        if (!Main.Settings.MultiplayerEnableDesyncDetection ||
            !Main.Settings.MultiplayerEnableStateFingerprint ||
            !Global.IsMultiplayer ||
            !PhotonNetwork.InRoom ||
            PhotonNetwork.OfflineMode ||
            networking.frozenGameplay ||
            !networking.ValidGameplayContext())
        {
            return;
        }

        var tick = networking.tick.LocalTick;

        if (tick == 0 || tick % FingerprintTickInterval != 0 || tick == _lastFingerprintTick)
        {
            return;
        }

        _lastFingerprintTick = tick;
        ForgetDepartedPlayers();

        var fingerprint = Fingerprint.Capture(tick);

        LocalByTick[tick] = fingerprint;
        Forget(LocalByTick, tick);

        PhotonNetwork.RaiseEvent(EventCode, fingerprint.ToEvent(),
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);

        // the host compares any client whose check for this tick got here first
        if (PhotonNetwork.IsMasterClient)
        {
            foreach (var reports in ClientReports.ToList())
            {
                if (reports.Value.Take(tick, out var early) && PhotonNetwork.CurrentRoom?.GetPlayer(reports.Key) != null)
                {
                    Compare(fingerprint, early, reports.Key);
                }
            }
        }

        // a client compares itself as soon as the host's fingerprint for this tick is known
        if (!PhotonNetwork.IsMasterClient && HostReports.Take(tick, out var host))
        {
            Compare(host, fingerprint, PhotonNetwork.MasterClient?.ActorNumber ?? 0);
        }
    }

    // Host only: a player who left no longer differs from anyone, and a resync mustn't reload everyone else because of them.
    private static void ForgetDepartedPlayers()
    {
        if (!PhotonNetwork.IsMasterClient || PhotonNetwork.CurrentRoom == null)
        {
            return;
        }

        foreach (var actor in ReportedByActor.Keys.Concat(ClientReports.Keys).Concat(PreviousDifferencesByActor.Keys)
                     .Concat(ConditionIdsReported).Concat(ConditionRoundsReported).Distinct().ToList())
        {
            if (PhotonNetwork.CurrentRoom.GetPlayer(actor) != null)
            {
                continue;
            }

            ReportedByActor.Remove(actor);
            ClientReports.Remove(actor);
            PreviousDifferencesByActor.Remove(actor);
            ConditionIdsReported.Remove(actor);
            ConditionRoundsReported.Remove(actor);
        }

        // A player whose game differed at a turn end has left: the host is told once, by name, that this report alone
        // doesn't mean the rest of the party needs a reload, and it no longer keeps Resync on offer by itself.
        foreach (var departed in TurnMismatchPlayers.Where(x => PhotonNetwork.CurrentRoom.GetPlayer(x.Key) == null)
                     .ToList())
        {
            TurnMismatchPlayers.Remove(departed.Key);
            TurnsReported.RemoveWhere(x => x.Actor == departed.Key);
            Main.Info($"Multiplayer: {departed.Value}, reported at a turn end, has left");
            Gui.GuiService?.ShowAlert(
                Gui.Format("Message/&MultiplayerDesyncTurnPlayerLeft", departed.Value), Gui.ColorAlert, 10f);

            if (TurnMismatchPlayers.Count == 0)
            {
                _turnDesyncReported = false;
            }
        }
    }

    private static void Forget(Dictionary<ulong, Fingerprint> byTick, ulong tick)
    {
        foreach (var old in byTick.Keys.Where(x => x + (FingerprintTickInterval * 4) < tick).ToList())
        {
            byTick.Remove(old);
        }
    }

    private static void RegisterEventHandler()
    {
        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    #endregion

    #region Receiving

    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode || photonEvent.CustomData is not object[] { Length: > 0 } data ||
            data[0] is not byte kind)
        {
            return;
        }

        var sender = PhotonNetwork.CurrentRoom?.GetPlayer(photonEvent.Sender);

        switch (kind)
        {
            case FingerprintKind when Main.Settings.MultiplayerEnableDesyncDetection &&
                                      Main.Settings.MultiplayerEnableStateFingerprint:
                OnFingerprint(Fingerprint.FromEvent(data), sender);
                break;
            case TurnKind when Main.Settings.MultiplayerEnableDesyncDetection &&
                               Main.Settings.MultiplayerEnableStateFingerprint:
                OnTurnEvent(data, sender);
                break;
            case ResyncKind when sender is { IsMasterClient: true }:
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncHostResyncing", Gui.ColorAlert, 10f);
                break;
            case ResyncCancelledKind when sender is { IsMasterClient: true }:
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncHostResyncCancelled", Gui.ColorFailure, 10f);
                break;
        }
    }

    private static void OnFingerprint(Fingerprint received, Player sender)
    {
        if (received == null || sender == null)
        {
            return;
        }

        if (sender.IsMasterClient)
        {
            if (LocalByTick.TryGetValue(received.Tick, out var local))
            {
                Compare(received, local, sender.ActorNumber);
            }
            else if (ServiceRepository.GetService<INetworkingService>() is NetworkingManager networking)
            {
                HostReports.Add(received.Tick, networking.tick.LocalTick, received);
            }

            return;
        }

        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        if (LocalByTick.TryGetValue(received.Tick, out var hostPrint))
        {
            Compare(hostPrint, received, sender.ActorNumber);
        }
        else if (ServiceRepository.GetService<INetworkingService>() is NetworkingManager hostNetworking)
        {
            if (!ClientReports.TryGetValue(sender.ActorNumber, out var reports))
            {
                reports = new BoundedTickReports<Fingerprint>(8, FingerprintTickInterval);
                ClientReports[sender.ActorNumber] = reports;
            }

            reports.Add(received.Tick, hostNetworking.tick.LocalTick, received);
        }
    }

    #endregion

    #region Comparing

    // host is the host's fingerprint, other is this computer's (on a client) or a client's (on the host)
    private static void Compare(Fingerprint host, Fingerprint other, int otherActor)
    {
        // a check is only meaningful when nothing was moving or acting on either computer at that tick
        if (!host.Quiet || !other.Quiet)
        {
            return;
        }

        ForgetIfNewGame();

        var allDifferences = Differences(host, other);

        // Condition IDs and rounds per source are only logged. IDs don't change what a player sees, and a reload offer for them
        // would come again with each new condition once the numbering has drifted. Rounds per source rest on the order of IDs,
        // which two computers can have swapped (see the fingerprint), so they aren't sure enough to stop play for.
        var differences = allDifferences.Where(x => x.Type == DifferenceType.State).ToList();

        PreviousDifferencesByActor.TryGetValue(otherActor, out var previous);
        PreviousDifferencesByActor[otherActor] = new HashSet<string>(differences.Select(x => x.Key));

        var playerName = PhotonNetwork.IsMasterClient
            ? PhotonNetwork.CurrentRoom?.GetPlayer(otherActor)?.NickName ?? "?"
            : Gui.Localize("ModUi/&MultiplayerHostRole").Trim();

        ReportLogOnly(allDifferences.Where(x => x.Type == DifferenceType.ConditionIds).ToList(), ConditionIdsReported,
            "condition IDs or sources", "condition-ids", otherActor, playerName, host.Tick);
        ReportLogOnly(allDifferences.Where(x => x.Type == DifferenceType.ConditionRounds).ToList(), ConditionRoundsReported,
            "condition rounds per source", "condition-rounds", otherActor, playerName, host.Tick);

        // a difference must show up in two checks in a row, so a value changing between frames isn't reported
        var confirmed = differences.Where(x => previous != null && previous.Contains(x.Key)).ToList();

        if (confirmed.Count == 0)
        {
            if (differences.Count == 0)
            {
                ReportedByActor.Remove(otherActor);
            }

            return;
        }

        var signature = string.Join("|", confirmed.Select(x => x.Key).OrderBy(x => x));

        var details = string.Join("\n", confirmed.Select(x => x.Description).Take(6));

        // reported once
        if (ReportedByActor.TryGetValue(otherActor, out var reported) && reported == signature)
        {
            if (_reofferWhenPossible && PhotonNetwork.IsMasterClient && !_resyncOffered &&
                ServiceRepository.GetService<IGameSerializationService>() is GameSerializationManager serialization &&
                CanSaveAndLoad(serialization))
            {
                _reofferWhenPossible = false;
                OfferResync(playerName, details);
            }

            return;
        }

        ReportedByActor[otherActor] = signature;

        Main.Error($"Multiplayer state desync with {playerName} at tick {host.Tick}:\n{details}");
        // TEMPORARY: each computer saves its recent object-ID records for comparison
        ObjectIdDiagnosticsContext.DesyncDetected("state-desync", (ulong)host.Tick);

        if (!PhotonNetwork.IsMasterClient)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);
            AddResyncLogLine(
                Gui.Format("Message/&MultiplayerDesyncResyncLine", SafeForLogFormat(playerName)), false);

            return;
        }

        OfferResync(playerName, details);
    }

    private static List<Difference> Differences(Fingerprint host, Fingerprint other)
    {
        var differences = new List<Difference>();
        if (host.Perception != other.Perception)
        {
            differences.Add(new Difference("perception", DifferenceType.State,
                Gui.Localize("Message/&MultiplayerDesyncPerception")));
        }

        if (host.Dice != other.Dice)
        {
            differences.Add(new Difference("dice", DifferenceType.State,
                Gui.Localize("Message/&MultiplayerDesyncDice")));
        }

        foreach (var hostCharacter in host.Characters)
        {
            var name = CharacterName(hostCharacter.Guid);

            if (!other.TryGet(hostCharacter.Guid, out var otherCharacter))
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:missing", DifferenceType.State,
                    Gui.Format("Message/&MultiplayerDesyncMissing", name)));

                continue;
            }

            void Add(string field, DifferenceType type, string hostValue, string otherValue)
            {
                differences.Add(new Difference($"{hostCharacter.Guid}:{field}", type,
                    Gui.Format("Message/&MultiplayerDesyncField", name, Gui.Localize($"Message/&MultiplayerDesync{field}"),
                        hostValue, otherValue)));
            }

            if (hostCharacter.HitPoints != otherCharacter.HitPoints)
            {
                Add("HitPoints", DifferenceType.State,
                    hostCharacter.HitPoints.ToString(), otherCharacter.HitPoints.ToString());
            }

            if (hostCharacter.TemporaryHitPoints != otherCharacter.TemporaryHitPoints)
            {
                Add("TemporaryHitPoints", DifferenceType.State,
                    hostCharacter.TemporaryHitPoints.ToString(), otherCharacter.TemporaryHitPoints.ToString());
            }

            if (!hostCharacter.Position.Equals(otherCharacter.Position))
            {
                Add("Position", DifferenceType.State,
                    hostCharacter.Position.ToString(), otherCharacter.Position.ToString());
            }

            if (hostCharacter.SpellSlots != otherCharacter.SpellSlots)
            {
                Add("SpellSlots", DifferenceType.State, "-", "-");
            }

            if (hostCharacter.PowerUses != otherCharacter.PowerUses)
            {
                Add("PowerUses", DifferenceType.State, "-", "-");
            }

            if (hostCharacter.Conditions != otherCharacter.Conditions)
            {
                Add("Conditions", DifferenceType.State, "-", "-");
            }
            else
            {
                // The same conditions under different object IDs or sources, which is what a numbering gap looks like. This
                // text only goes to the log (see Compare), so it has no translations.
                if (hostCharacter.ConditionIds != otherCharacter.ConditionIds)
                {
                    differences.Add(new Difference($"{hostCharacter.Guid}:ConditionIds", DifferenceType.ConditionIds,
                        name));
                }

                if (hostCharacter.ConditionRounds != otherCharacter.ConditionRounds)
                {
                    differences.Add(new Difference($"{hostCharacter.Guid}:ConditionRounds",
                        DifferenceType.ConditionRounds, name));
                }
            }
        }

        foreach (var otherCharacter in other.Characters.Where(x => !host.TryGet(x.Guid, out _)))
        {
            differences.Add(new Difference($"{otherCharacter.Guid}:extra", DifferenceType.State,
                Gui.Format("Message/&MultiplayerDesyncExtra", CharacterName(otherCharacter.Guid))));
        }

        return differences;
    }

    private static void ReportLogOnly(List<Difference> differences, HashSet<int> reported, string what, string recordName,
        int actor, string playerName, ulong tick)
    {
        if (differences.Count == 0)
        {
            reported.Remove(actor);

            return;
        }

        if (!reported.Add(actor))
        {
            return;
        }

        Main.Error($"Multiplayer {what} differ with {playerName} at tick {tick}: " +
                   string.Join(", ", differences.Select(x => x.Description)));
        // TEMPORARY: each computer saves its recent object-ID records for comparison
        ObjectIdDiagnosticsContext.DesyncDetected(recordName, tick);
    }

    private static string CharacterName(ulong guid)
    {
        var character = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
            .FirstOrDefault(x => x.Guid == guid);

        return character?.Name ?? $"#{guid}";
    }

    #endregion

    #region Recovery save

    // a save in one of the recovery slots, named only once the game confirms it was written; null with the reason
    // when it can't be made
    private static string MakeRecoverySave(out string reason)
    {
        reason = null;

        if (ServiceRepository.GetService<IGameSerializationService>() is not GameSerializationManager serialization)
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveUnavailable");

            return null;
        }

        if (ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign is { IronManMode: true })
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveIronMan");

            return null;
        }

        if (!serialization.CanSave)
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveBusy");

            return null;
        }

        var name = NextRecoverySlot();
        var path = Path.Combine(TacticalAdventuresApplication.SaveGameDirectory, name) + ".sav";
        var saved = false;

        // the game raises GameSaved at the end of SaveGame, after writing; it returns without it when the disk is full
        serialization.GameSaved += OnSaved;

        try
        {
            serialization.SaveGame(path, GameSerializationDefinitions.SaveType.Manual);
        }
        catch (Exception e)
        {
            // SaveGame clears its saving flag only at its end
            serialization.saving = false;
            Main.Error($"Multiplayer recovery save failed: {e.Message}");
        }
        finally
        {
            serialization.GameSaved -= OnSaved;
        }

        if (!saved || !File.Exists(path))
        {
            reason = Gui.Localize("Message/&MultiplayerDesyncSaveFailed");

            return null;
        }

        Main.Info($"Multiplayer recovery save written: {path}");
        _lastRecoverySave = name;
        _lastRecoverySaveCampaign = Gui.GameCampaign?.CampaignGuid;

        return name;

        void OnSaved(GameSerializationDefinitions.SaveType saveType, string savedGameName)
        {
            saved = true;
        }
    }

    // The last recovery save, for telling the host what it can load by hand. Slots are shared by every game on this computer,
    // so it only counts if it's from this game and is still there.
    private static string UsableLastRecoverySave()
    {
        if (_lastRecoverySave == null || _lastRecoverySaveCampaign != Gui.GameCampaign?.CampaignGuid)
        {
            return null;
        }

        var path = Path.Combine(TacticalAdventuresApplication.SaveGameDirectory, _lastRecoverySave) + ".sav";

        return File.Exists(path) ? _lastRecoverySave : null;
    }

    // an unused slot first, then the oldest
    private static string NextRecoverySlot()
    {
        return Enumerable.Range(1, RecoverySaveSlots)
            .Select(x => RecoverySavePrefix + x)
            .OrderBy(x =>
            {
                var path = Path.Combine(TacticalAdventuresApplication.SaveGameDirectory, x) + ".sav";

                return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            })
            .First();
    }

    private static IEnumerable<(int repertoire, int level, int used)> SpellSlotEntries(GameLocationCharacter character)
    {
        var repertoires = character?.RulesetCharacter?.SpellRepertoires;

        if (repertoires == null)
        {
            yield break;
        }

        for (var r = 0; r < repertoires.Count; r++)
        {
            foreach (var slot in repertoires[r].usedSpellsSlots.OrderBy(x => x.Key))
            {
                yield return (r, slot.Key, slot.Value);
            }
        }
    }
    #endregion

    #region Turn-end check

    // At the end of every combatant's turn in a multiplayer battle, each player's game sends the host
    // that character's values (the same as the regular check: hit points, cell, conditions, spell slots, power uses),
    // its facing and the dice position. The host compares them with its own at the same turn end and, if they differ,
    // gives the usual desync report and Resync offer, naming the character and round. The regular check only compares
    // when nothing moves or acts, which in a busy fight can be turns later and can't say which turn caused it; a turn end
    // is a fixed point every computer passes in the same order. The dice position is only compared when nothing else
    // was acting on either computer, as in the regular check. One message per turn per player; nothing waits.
    private const byte TurnKind = 7;

    // how long a turn's record is kept for the other side's to arrive
    private const float TurnRecordSeconds = 60f;

    private static readonly Dictionary<string, TurnPrint> HostTurns = new();
    private static readonly Dictionary<(int Actor, string Key), TurnPrint> ClientTurns = new();
    private static readonly HashSet<(int Actor, string Key)> TurnsReported = [];

    // A turn is named by the battle, the round, the character, whether it's a legendary turn, and which of that
    // character's turns this is in the round: Thief Reflexes and Time Stop give one character two turns in a round, and
    // successive battles reuse rounds and characters. Battles are counted on each computer
    // as they start; only one battle runs at a time, so every computer counts the same.
    private static Game _turnGame;
    private static GameLocationBattle _turnBattle;
    private static int _turnBattleNumber;
    private static readonly Dictionary<string, int> TurnsInRound = new();

    // a turn-end difference was reported, so the Resync button has something to fix
    private static bool _turnDesyncReported;

    // host: the players a turn-end difference was reported with, by actor, with their names, so the host can be told
    // when one of them leaves
    private static readonly Dictionary<int, string> TurnMismatchPlayers = new();

    // a controller host was told once that Resync must wait for a save (see StartResync); a new report tells it again
    private static bool _resyncLaterTold;

    private sealed class TurnPrint
    {
        // host: the players already compared against this turn
        internal readonly HashSet<int> Answered = [];
        internal CharacterPrint Character;
        internal long Dice;
        internal int Facing;
        internal string Key;
        internal bool Quiet;
        internal float Received;
        internal int Round;
    }

    // GameLocationCharacter.EndBattleTurn and EndLegendaryTurn postfixes, on every computer (a legendary turn ends
    // through EndLegendaryTurn, which skips EndBattleTurn, so it's hooked too)
    internal static void OnTurnEnded(GameLocationCharacter character, int round, bool legendary)
    {
        if (!Main.Settings.MultiplayerEnableDesyncDetection ||
            !Main.Settings.MultiplayerEnableStateFingerprint ||
            !Global.IsMultiplayer ||
            !PhotonNetwork.InRoom ||
            PhotonNetwork.OfflineMode ||
            character?.RulesetCharacter == null ||
            Gui.Battle is not { } battle)
        {
            return;
        }

        RegisterEventHandler();

        // a new game or battle starts its own numbering
        if (Gui.Game != _turnGame)
        {
            _turnGame = Gui.Game;
            _turnBattle = null;
            _turnBattleNumber = 0;
        }

        if (battle != _turnBattle)
        {
            _turnBattle = battle;
            _turnBattleNumber++;
            TurnsInRound.Clear();
        }

        var turnName = $"{_turnBattleNumber}:{round}:{character.Guid}:{(legendary ? "L" : "T")}";

        TurnsInRound.TryGetValue(turnName, out var occurrence);
        TurnsInRound[turnName] = ++occurrence;

        var actions = ServiceRepository.GetService<IGameLocationActionService>();
        var characters = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [];
        var print = new TurnPrint
        {
            Character = Fingerprint.PrintOf(character),
            Dice = Hash(CaptureRandomState()),
            Facing = (int)character.Orientation,
            Key = $"{turnName}:{occurrence}",
            Quiet = characters.All(x => x == character || (!x.MoveInProgress && actions?.IsCharacterActing(x) != true)),
            Received = Time.realtimeSinceStartup,
            Round = round
        };

        if (!PhotonNetwork.IsMasterClient)
        {
            var values = new int[ValuesPerCharacter];

            Fingerprint.WriteValues(print.Character, values, 0);
            PhotonNetwork.RaiseEvent(EventCode,
                new object[]
                {
                    TurnKind, print.Round, (long)character.Guid, values, print.Facing, print.Dice, print.Quiet,
                    print.Key
                },
                new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);

            return;
        }

        ForgetOldTurns();
        HostTurns[print.Key] = print;

        foreach (var pending in ClientTurns.Where(x => x.Key.Key == print.Key).ToList())
        {
            ClientTurns.Remove(pending.Key);
            print.Answered.Add(pending.Key.Actor);
            CompareTurn(print, pending.Value, pending.Key.Actor);
        }
    }

    // host: a player's record of a turn end
    private static void OnTurnEvent(object[] data, Player sender)
    {
        if (!PhotonNetwork.IsMasterClient || sender == null || data.Length != 8 || data[1] is not int round ||
            data[2] is not long guid || data[3] is not int[] { Length: ValuesPerCharacter } values ||
            data[4] is not int facing || data[5] is not long dice || data[6] is not bool quiet ||
            data[7] is not string key)
        {
            return;
        }

        var print = new TurnPrint
        {
            Character = Fingerprint.ReadValues((ulong)guid, values, 0),
            Dice = dice,
            Facing = facing,
            Key = key,
            Quiet = quiet,
            Received = Time.realtimeSinceStartup,
            Round = round
        };

        ForgetOldTurns();

        if (HostTurns.TryGetValue(print.Key, out var hostPrint))
        {
            if (hostPrint.Answered.Add(sender.ActorNumber))
            {
                CompareTurn(hostPrint, print, sender.ActorNumber);
            }
        }
        else
        {
            ClientTurns[(sender.ActorNumber, print.Key)] = print;
        }
    }

    private static void ForgetOldTurns()
    {
        var before = Time.realtimeSinceStartup - TurnRecordSeconds;

        foreach (var key in HostTurns.Where(x => x.Value.Received < before).Select(x => x.Key).ToList())
        {
            HostTurns.Remove(key);
        }

        foreach (var key in ClientTurns.Where(x => x.Value.Received < before).Select(x => x.Key).ToList())
        {
            ClientTurns.Remove(key);
        }

        // reported turns are named by battle too, so a later battle is never suppressed; old ones only take memory
        TurnsReported.RemoveWhere(x => !HostTurns.ContainsKey(x.Key) && !ClientTurns.ContainsKey((x.Actor, x.Key)));
    }

    // host: one character at one turn end; a difference is reported at once (a turn end is a settled point, so there's
    // no need to see it twice), once per player and turn
    private static void CompareTurn(TurnPrint host, TurnPrint other, int otherActor)
    {
        var hostPrint = new Fingerprint { Dice = host.Quiet && other.Quiet ? host.Dice : 0 };
        var otherPrint = new Fingerprint { Dice = host.Quiet && other.Quiet ? other.Dice : 0 };

        hostPrint.Add(host.Character);
        otherPrint.Add(other.Character);

        var name = CharacterName(host.Character.Guid);
        var differences = Differences(hostPrint, otherPrint).Where(x => x.Type == DifferenceType.State)
            .Select(x => x.Description).ToList();

        if (host.Facing != other.Facing)
        {
            differences.Add(Gui.Format("Message/&MultiplayerDesyncField", name,
                Gui.Localize("Message/&MultiplayerDesyncFacing"),
                ((LocationDefinitions.Orientation)host.Facing).ToString(),
                ((LocationDefinitions.Orientation)other.Facing).ToString()));
        }

        if (differences.Count == 0 || !TurnsReported.Add((otherActor, host.Key)))
        {
            return;
        }

        ForgetIfNewGame();
        _turnDesyncReported = true;
        _resyncLaterTold = false;

        var playerName = PhotonNetwork.CurrentRoom?.GetPlayer(otherActor)?.NickName ?? "?";

        TurnMismatchPlayers[otherActor] = playerName;
        var details = Gui.Format("Message/&MultiplayerDesyncTurnEnd", name, host.Round.ToString()) + "\n" +
                      string.Join("\n", differences.Take(6));

        Main.Error($"Multiplayer desync with {playerName} at the end of {name}'s turn, round {host.Round}:\n{details}");
        // TEMPORARY: each computer saves its recent object-ID records for comparison
        ObjectIdDiagnosticsContext.DesyncDetected("turn-desync",
            ServiceRepository.GetService<INetworkingService>() is NetworkingManager networking
                ? networking.tick.LocalTick
                : 0UL);
        OfferResync(playerName, details);
    }

    #endregion

    #region Resync

    // Host only: a message with Resync and Later, and a line in the game log with a Resync button. Choosing Later, or a moment
    // when the game can't save, loses nothing: the button stays until it's used.
    private static void OfferResync(string playerName, string details)
    {
        // an Iron Man game can't make the save a resync needs, so it's not offered at all
        if (!Main.Settings.MultiplayerOfferHostResync || IsIronMan)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);

            return;
        }

        AddResyncLogLine(Gui.Format("Message/&MultiplayerDesyncResyncLine", SafeForLogFormat(playerName)), true);

        if (_resyncOffered || Time.realtimeSinceStartup < _resyncWaitUntil)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerDesyncDetails", playerName, details),
                Gui.ColorFailure, 10f);

            return;
        }

        _resyncOffered = true;

        var description = Gui.Format("Message/&MultiplayerDesyncResyncDescription", playerName, details);

        // a controller can't click log lines, which is why the game leaves out its own load button then too
        if (!Gui.GamepadActive)
        {
            description += "\n\n" + Gui.Localize("Message/&MultiplayerDesyncResyncLaterHint");
        }

        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2,
            "Message/&MultiplayerDesyncResyncTitle", description,
            "Message/&MultiplayerDesyncResyncAccept", "Message/&MultiplayerDesyncResyncLater",
            () =>
            {
                _resyncOffered = false;
                StartResync();
            },
            () => _resyncOffered = false);
    }

    // The line is built as the game builds its "game saved" line with the Load button. Only the host gets the button: only its
    // load brings everyone back in, and a client's copy of the line just says what happened.
    private static void AddResyncLogLine(string line, bool withButton)
    {
        var console = Gui.Game?.GameConsole;

        if (console == null)
        {
            return;
        }

        withButton &= !Gui.GamepadActive;

        var entry = new GameConsoleEntry(withButton ? line + " {0}" : line, console.consoleTableDefinition, false,
            ConsoleStyleDuplet.ParameterType.System);

        if (withButton)
        {
            entry.AddParameter(ConsoleStyleDuplet.ParameterType.Button,
                Gui.Localize("Message/&MultiplayerDesyncResyncAccept"), ResyncClickAction, null, null, null);
        }

        console.AddEntry(entry, -1);
    }

    // the line goes through string.Format again, so a player's name mustn't add placeholders to it
    private static string SafeForLogFormat(string text)
    {
        return (text ?? "?").Replace("{", "").Replace("}", "");
    }

    // The entity ID check found a gap (MultiplayerContext). A gap often changes nothing a player can see, so this only adds the
    // log line with its button and no message that stops play. Only the host gets it, because only the host can resync.
    internal static void OfferResyncForIdGap()
    {
        if (!PhotonNetwork.IsMasterClient || !Main.Settings.MultiplayerOfferHostResync || IsIronMan)
        {
            return;
        }

        ForgetIfNewGame();
        _idDesyncReported = true;
        AddResyncLogLine(Gui.Localize("Message/&MultiplayerDesyncIdResyncLine"), true);
    }

    // From the game log's click handler: true if the click was on a Resync button, which the game has nothing to do for.
    internal static bool HandleLogClick(GameRecordEntry entry)
    {
        if (entry is not GameConsoleEntry { ClickAction: ResyncClickAction })
        {
            return false;
        }

        StartResync();

        return true;
    }

    // Whether the game will let the host save and then load. Not CanLoad, because that's also false when no save exists yet,
    // which the recovery save itself would change.
    private static bool CanSaveAndLoad(GameSerializationManager serialization)
    {
        return serialization.CanSave && serialization.loadDisabledTokens.Count == 0;
    }

    // It comes back for a controller only after someone chose Resync at a bad moment, or a map change: choosing Later otherwise
    // stays quiet. Nothing is reported again, and it waits until the game can save. Not for a save that failed while the game
    // was willing, such as a full disk, or for a host role that moved: another try wouldn't go differently.
    private static void ReofferIfOnlyABadMoment(GameSerializationManager serialization)
    {
        _reofferWhenPossible = Gui.GamepadActive && PhotonNetwork.IsMasterClient && !CanSaveAndLoad(serialization);
    }

    // Every way to start a resync comes through here, the message and the log button alike.
    private static void StartResync()
    {
        // lines stay in the log after the host role moved to someone else
        if (!PhotonNetwork.IsMasterClient || Time.realtimeSinceStartup < _resyncWaitUntil ||
            ServiceRepository.GetService<INetworkingService>() is not NetworkingManager networking ||
            ServiceRepository.GetService<IGameSerializationService>() is not GameSerializationManager serialization)
        {
            return;
        }

        ForgetIfNewGame();
        ForgetDepartedPlayers();

        // lines also stay after a check finds the game matching, or the player who differed leaves
        if (ReportedByActor.Count == 0 && !_idDesyncReported && !_turnDesyncReported)
        {
            Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncResyncNotNeeded", Gui.ColorAlmostWhite, 6f);

            return;
        }

        // The game refuses to save while anything scripted runs, such as a cutscene (CanSave is false while functors run). This
        // is only a bad moment, so it's found before clients are told a reload is coming, and Resync can simply be used again.
        if (!CanSaveAndLoad(serialization))
        {
            Main.Info("Multiplayer resync not started: the game can't save or load right now");

            // A controller host can't use the log's Resync button, and only the regular check offers Resync again by
            // itself; with only a turn-end or numbering report, nothing would. So it's told once what to do instead.
            // Nothing about Resync, saving or loading changes.
            if (Gui.GamepadActive && ReportedByActor.Count == 0 && !_resyncLaterTold)
            {
                _resyncLaterTold = true;
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncResyncSaveLater", Gui.ColorFailure, 12f);
            }
            else
            {
                Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncResyncNotNow", Gui.ColorFailure, 10f);
            }

            ReofferIfOnlyABadMoment(serialization);

            return;
        }

        _resyncWaitUntil = Time.realtimeSinceStartup + ResyncWaitSeconds;
        PhotonNetwork.RaiseEvent(EventCode, new object[] { ResyncKind },
            new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
        networking.StartCoroutine(Resync(serialization));
    }

    // the host's new recovery save is the state everyone continues from: loading it as host brings every client in.
    // If it can't be written, nothing is loaded: an older save isn't loaded in its place
    private static IEnumerator Resync(GameSerializationManager serialization)
    {
        // lets the message box close first
        yield return null;

        var previous = UsableLastRecoverySave();
        var save = MakeRecoverySave(out var reason);

        if (save == null)
        {
            Main.Error($"Multiplayer resync cancelled: no save ({reason}), nothing was loaded");
            _resyncWaitUntil = 0f;
            ReofferIfOnlyABadMoment(serialization);
            PhotonNetwork.RaiseEvent(EventCode, new object[] { ResyncCancelledKind },
                new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
            Gui.GuiService.ShowMessage(MessageModal.Severity.Serious3,
                "Message/&MultiplayerDesyncResyncTitle",
                previous == null
                    ? Gui.Format("Message/&MultiplayerDesyncResyncNoSave", reason)
                    : Gui.Format("Message/&MultiplayerDesyncResyncNoSaveRecovery", reason, previous),
                "Message/&MessageOkTitle", string.Empty, null, null);

            yield break;
        }

        // the save takes a moment and can't be undone, but nothing is lost if the load can't follow: it's only one more save
        if (!PhotonNetwork.IsMasterClient || !CanSaveAndLoad(serialization))
        {
            Main.Error("Multiplayer resync cancelled: the game can't load right now, nothing was loaded");
            _resyncWaitUntil = 0f;
            ReofferIfOnlyABadMoment(serialization);
            PhotonNetwork.RaiseEvent(EventCode, new object[] { ResyncCancelledKind },
                new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
            Gui.GuiService.ShowAlert("Message/&MultiplayerDesyncResyncNotNow", Gui.ColorFailure, 10f);

            yield break;
        }

        // The game's own multiplayer load, as its Load Game screen uses: a lockstep command that has the host send the save to
        // the clients and every computer load it together (ServerLoadGame, ClientLoadGame). GameSerializationManager.LoadGame
        // only loads on this computer, so clients would keep running a game the host had left. State here is reset by the
        // load's tick reset.
        Main.Info($"Multiplayer resync: loading {save}");
        ServiceRepository.GetService<ICommandService>().LoadGame(serialization.BuildFilenameWithPathAndExtension(save));
    }

    #endregion

    #region Fingerprint

    private static GameLocationCharacter FindCharacter(ulong guid)
    {
        return ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters
            .FirstOrDefault(x => x.Guid == guid);
    }

    private static int[] CaptureRandomState()
    {
        if (ServiceRepository.GetService<IGameService>()?.Game is not { } game)
        {
            return [];
        }

        object state = game.randomState;

        return RandomStateFields.Select(x => (int)x.GetValue(state)).ToArray();
    }

    private static long Hash(IEnumerable<int> values)
    {
        // FNV-1a, 64 bits
        var hash = unchecked((long)14695981039346656037UL);

        foreach (var value in values)
        {
            hash = unchecked((hash ^ value) * 1099511628211L);
        }

        return hash;
    }

    private static IEnumerable<int> TextValues(string text)
    {
        return (text ?? string.Empty).Select(x => (int)x);
    }

    private enum DifferenceType
    {
        State,
        ConditionIds,
        ConditionRounds
    }

    private sealed class Difference(string key, DifferenceType type, string description)
    {
        internal string Key { get; } = key;
        internal DifferenceType Type { get; } = type;
        internal string Description { get; } = description;
    }

    private sealed class CharacterPrint
    {
        internal long ConditionIds;
        internal long ConditionRounds;
        internal long Conditions;
        internal ulong Guid;
        internal int HitPoints;
        internal int3 Position;
        internal long SpellSlots;

        // the remaining uses of each of the character's powers, by name: a power used on one computer only (an
        // after-rest power whose command didn't run everywhere) shows here
        internal long PowerUses;
        internal int TemporaryHitPoints;
    }

    private sealed class Fingerprint
    {
        private readonly Dictionary<ulong, CharacterPrint> _byGuid = [];
        internal readonly List<CharacterPrint> Characters = [];
        internal long Dice;
        internal long Perception;
        internal bool Quiet;
        internal ulong Tick;

        internal bool TryGet(ulong guid, out CharacterPrint character)
        {
            return _byGuid.TryGetValue(guid, out character);
        }

        internal void Add(CharacterPrint character)
        {
            Characters.Add(character);
            _byGuid[character.Guid] = character;
        }

        // one character's values, as the regular check and the turn-end check compare them
        internal static CharacterPrint PrintOf(GameLocationCharacter character)
        {
            var ruleset = character.RulesetCharacter;

            return new CharacterPrint
            {
                Guid = character.Guid,
                HitPoints = ruleset.CurrentHitPoints,
                TemporaryHitPoints = ruleset.TemporaryHitPoints,
                Position = character.LocationPosition,
                Conditions = Hash(ruleset.ConditionsByCategory
                    .OrderBy(x => x.Key)
                    .SelectMany(x => x.Value)
                    .Select(x => $"{x.ConditionDefinition?.Name}:{x.RemainingRounds}")
                    .OrderBy(x => x)
                    .SelectMany(TextValues)),
                // apart from Conditions, so it can be told from a real difference in what a character has
                ConditionIds = Hash(ruleset.ConditionsByCategory
                    .SelectMany(x => x.Value)
                    .Select(x => $"{x.ConditionDefinition?.Name}:{x.Guid}:{x.SourceGuid}")
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .SelectMany(TextValues)),
                // Rounds tied to their source, which Conditions sorts apart, so it can't see two sources' rounds swapped.
                // Sources go by their order and not their IDs, so a plain numbering gap, which keeps that order, doesn't
                // change this and can't hide a real difference. IDs swapped between computers would change it.
                ConditionRounds = Hash(ruleset.ConditionsByCategory
                    .SelectMany(x => x.Value)
                    .OrderBy(x => x.SourceGuid)
                    .ThenBy(x => x.ConditionDefinition?.Name, StringComparer.Ordinal)
                    .ThenBy(x => x.RemainingRounds)
                    .Select(x => $"{x.ConditionDefinition?.Name}:{x.RemainingRounds}")
                    .SelectMany(TextValues)),
                SpellSlots = Hash(SpellSlotEntries(character)
                    .SelectMany(x => new[] { x.repertoire, x.level, x.used })),
                PowerUses = Hash(ruleset.UsablePowers
                    .Where(x => x?.PowerDefinition != null)
                    .Select(x => $"{x.PowerDefinition.Name}:{x.RemainingUses}")
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .SelectMany(TextValues))
            };
        }

        internal static Fingerprint Capture(ulong tick)
        {
            var characters = ServiceRepository.GetService<IGameLocationCharacterService>()?.ValidCharacters ?? [];
            var actions = ServiceRepository.GetService<IGameLocationActionService>();
            var fingerprint = new Fingerprint
            {
                Tick = tick,
                Dice = Hash(CaptureRandomState()),
                Perception = PerceptionContext.Digest(),
                // nothing moving or acting, and no perception check requested or waiting for its command
                Quiet = characters.All(x => !x.MoveInProgress && actions?.IsCharacterActing(x) != true) &&
                        PerceptionContext.IsIdle
            };

            foreach (var character in characters.Where(x => x.RulesetCharacter != null).OrderBy(x => x.Guid))
            {
                fingerprint.Add(PrintOf(character));
            }

            return fingerprint;
        }

        internal object[] ToEvent()
        {
            var values = new int[Characters.Count * ValuesPerCharacter];

            for (var i = 0; i < Characters.Count; i++)
            {
                WriteValues(Characters[i], values, i * ValuesPerCharacter);
            }

            return
            [
                FingerprintKind, (long)Tick, Dice, Quiet,
                Characters.Select(x => (long)x.Guid).ToArray(), values, Perception
            ];
        }

        internal static void WriteValues(CharacterPrint c, int[] values, int o)
        {
            values[o] = c.HitPoints;
            values[o + 1] = c.TemporaryHitPoints;
            values[o + 2] = c.Position.x;
            values[o + 3] = c.Position.y;
            values[o + 4] = c.Position.z;
            values[o + 5] = (int)(c.Conditions >> 32);
            values[o + 6] = (int)c.Conditions;
            values[o + 7] = (int)(c.SpellSlots >> 32);
            values[o + 8] = (int)c.SpellSlots;
            values[o + 9] = (int)(c.ConditionIds >> 32);
            values[o + 10] = (int)c.ConditionIds;
            values[o + 11] = (int)(c.ConditionRounds >> 32);
            values[o + 12] = (int)c.ConditionRounds;
            values[o + 13] = (int)(c.PowerUses >> 32);
            values[o + 14] = (int)c.PowerUses;
        }

        internal static Fingerprint FromEvent(object[] data)
        {
            if (data.Length < 7 || data[6] is not long || data[4] is not long[] guids || data[5] is not int[] values ||
                values.Length != guids.Length * ValuesPerCharacter)
            {
                return null;
            }

            var fingerprint = new Fingerprint
            {
                Tick = (ulong)(long)data[1], Dice = (long)data[2], Perception = (long)data[6], Quiet = (bool)data[3]
            };

            for (var i = 0; i < guids.Length; i++)
            {
                fingerprint.Add(ReadValues((ulong)guids[i], values, i * ValuesPerCharacter));
            }

            return fingerprint;
        }

        internal static CharacterPrint ReadValues(ulong guid, int[] values, int o)
        {
            return new CharacterPrint
            {
                Guid = guid,
                HitPoints = values[o],
                TemporaryHitPoints = values[o + 1],
                Position = new int3(values[o + 2], values[o + 3], values[o + 4]),
                Conditions = ((long)values[o + 5] << 32) | (uint)values[o + 6],
                SpellSlots = ((long)values[o + 7] << 32) | (uint)values[o + 8],
                ConditionIds = ((long)values[o + 9] << 32) | (uint)values[o + 10],
                ConditionRounds = ((long)values[o + 11] << 32) | (uint)values[o + 12],
                PowerUses = ((long)values[o + 13] << 32) | (uint)values[o + 14]
            };
        }
    }

    #endregion
}
