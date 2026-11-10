using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.UI;

namespace SolastaUnfinishedBusiness.Models;

// The game's multiplayer vote as every computer sees it. The game has one vote, the fast travel vote
// (MultiplayerFastTravelVoteModal, NetworkingManager's RequestVote / AnswerVote / CancelVote / NotifyVote* messages);
// the mod also runs the approval of time-passing actions on it (MultiplayerApprovalContext), with its own texts.
// User request, 2026-11-01: use the game's vote screens, and show the vote screen to every player.
//
// What the game does by itself: the asking player gets the vote screen (the other players with their votes, and
// Cancel); everyone else gets the request window (MultiplayerAnswerModal: Accept, Refuse, a 20 second timer that
// refuses when it runs out). When the vote passes, only the asking computer runs the function it gave the vote screen
// (for travel, MapWaypointItem.FastTravelVotePassed sends the travel command); the others just close their window.
//
// What this class changes, for travel and approval votes alike:
// - Every vote has an ID, and its answers, outcome and cancel carry it (Photon event 174, sent in place of the game's
//   AnswerVote / NotifyVote* / CancelVote messages, which carry no ID and are ignored when received). An answer to an
//   earlier vote that arrives late, or a refusal of another vote, can't count for the running one. The request itself
//   stays the game's RequestVote, with the ID added to its parameter (TakeVoteId removes it on arrival).
// - Answers are counted by whoever asked. The game counts them only on the host's screen (LocalCommandManager.
//   AnswerVote checks IsMasterClient), so a vote started by another player never received its answers.
// - A player who accepted gets the vote screen too, showing who proposed what and everyone's votes so far, instead of
//   the request window saying "waiting for players". Its Cancel refuses the vote, which ends it for everyone, as a
//   refusal does.
// - When the asking player cancels, the others are told who cancelled; the game always named the host.
// - The vote screen gets more player rows when the room has more than 4 players (the prefab has 3; the mod allows 6).
// - A player who leaves no longer blocks the vote: the game reset every row to "waiting" on a redraw, so the asking
//   computer waited for answers that could not come. The vote passes once every player still in the room accepted.
// - A vote nobody finishes is cancelled after VoteDeadlineSeconds, naming who didn't answer. The request window
//   refuses by itself after 20 seconds, but a computer can drop a request without showing it: the game's travel
//   request is dropped where its "nothing running that blocks saving" check fails (NetworkingManager.RequestVoteAsync),
//   with no answer.
// - Only one vote at a time: a request that arrives while another vote is running here is refused (that refusal names
//   the arriving vote, so the running one isn't affected).
// - A vote needs every player on this build (MultiplayerSessionContext's build versions): others would count answers
//   the game's way and not understand these messages.
//
// Every computer keeps one record of the running vote (_vote): its ID, who asked, what, and who accepted so far. The
// answers reach every computer, which is what lets each one show everyone's votes.
internal static class MultiplayerVoteContext
{
    private const byte EventCode = 174;
    // [AnswerKind, int voteId, int askerId, bool accepted, byte reason, string names]; reason is the approval's busy
    // reason (MultiplayerApprovalContext), 0 for an ordinary answer
    private const byte AnswerKind = 1;
    // from the asking computer: [OutcomeKind, int voteId, bool passed, int refusedBy]
    private const byte OutcomeKind = 2;
    // from the asking computer: [CancelKind, int voteId]
    private const byte CancelKind = 3;
    // added to the request's parameter, followed by the vote ID
    private const string VoteIdMarker = "\u0003FinishingBusinessVote";
    // longer than the request window's own 20 second timer, so only a request that was never shown runs into it
    private const float VoteDeadlineSeconds = 30f;
    // a computer that was asked forgets a vote after this long, whatever happened: the asking computer has cancelled
    // it by then (Codex review, 2026-11-02: a cancel overtaking its request left the request's vote here for good)
    private const float OtherDeadlineSeconds = 45f;
    // ... or this long after refusing it, when no outcome came
    private const float RefusedForgetSeconds = 5f;
    // votes remembered as ended, per asking player and ID
    private const int EndedVotesKept = 32;

    private static readonly HashSet<(int, int)> EndedVotes = [];
    private static readonly Queue<(int, int)> EndedVoteOrder = new();

    private static Vote _vote;
    private static bool _eventHandlerRegistered;
    private static int _lastVoteId = new System.Random().Next(1, 1 << 20);
    // the ID of the vote this computer asked for last, for the outcome and cancel sent after it ended here
    private static int _endedVoteId;

    // the vote screen's title and proposal labels with the prefab's own texts, captured on first use
    private static GuiLabel _titleLabel;
    private static GuiLabel _descriptionLabel;
    private static string _titleText;
    private static string _descriptionText;

    internal static bool IsVoteRunning => _vote != null;

    // the running vote's parameter on a computer that was asked, answered or not (null when there is none or this
    // computer asked)
    internal static string RequestParameter => _vote is { LocalIsAsker: false } ? _vote.Parameter : null;

    private static int LocalPlayerNumber =>
        ServiceRepository.GetService<INetworkingService>()?.LocalPlayerNumber ?? PhotonNetwork.LocalPlayer.ActorNumber;

    private static MultiplayerFastTravelVoteModal VoteScreen =>
        Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

    private static LocalCommandManager LocalCommands =>
        (ServiceRepository.GetService<INetworkingService>() as NetworkingManager)?.localCommandManager;

    // leaving or joining a room: forgets the vote and closes its screens without sending anything
    internal static void Reset()
    {
        if (_vote == null)
        {
            return;
        }

        var shownHere = _vote.LocalIsAsker || _vote.ShownToLocal;

        _vote = null;
        HideLocalScreens(shownHere);
    }

    // clients register as soon as they join, so the first answer isn't missed
    internal static void OnJoinedRoom()
    {
        Reset();
        // actor numbers belong to a room
        EndedVotes.Clear();
        EndedVoteOrder.Clear();
        RegisterEventHandler();
    }

    #region Asking player

    // MultiplayerVoteModal.ShowVote: records the vote this computer starts and adds its ID to the request's parameter;
    // false refuses it (another vote runs, or a player has another build)
    internal static bool OnShowVote(ref string parameter, MultiplayerVoteModal.VoteCanceledHandler voteCanceled)
    {
        string refusal = null;

        if (_vote != null)
        {
            refusal = Gui.Localize("Message/&MultiplayerVoteAlreadyRunning");
        }
        else
        {
            var otherBuilds = PhotonNetwork.PlayerListOthers
                .Where(x => !MultiplayerSessionContext.IsSameVerifiedBuild(MultiplayerSessionContext.GetPlayerVersion(x)))
                .Select(x => x.NickName)
                .ToList();

            if (otherBuilds.Count > 0)
            {
                refusal = Gui.Format("Message/&MultiplayerVoteDifferentBuild", string.Join(", ", otherBuilds));
            }
        }

        if (refusal != null)
        {
            Gui.GuiService.ShowAlert(refusal, Gui.ColorFailure, 5f);
            // the caller undoes what it prepared (fast travel clears its "preparing" state), as for a cancel
            voteCanceled?.Invoke();

            return false;
        }

        RegisterEventHandler();

        _vote = new Vote
        {
            Id = ++_lastVoteId,
            AskerId = LocalPlayerNumber,
            AskerName = PhotonNetwork.LocalPlayer?.NickName ?? string.Empty,
            Parameter = parameter,
            LocalIsAsker = true,
            StartTime = Time.unscaledTime
        };
        parameter += VoteIdMarker + _vote.Id;

        return true;
    }

    // ProcessVoteAccepted, ProcessVoteRefused and the asking player's Cancel end the vote here before the game closes
    // the screen (so closing it isn't taken for the game dropping the vote, OnScreenHiding) and before the game sends
    // the outcome or cancel, which then carries this ID
    internal static void OnAskerEndedVote()
    {
        if (_vote is not { LocalIsAsker: true })
        {
            return;
        }

        _endedVoteId = _vote.Id;
        _vote = null;
    }

    // called every network frame on the asking computer: the vote passes once every player still in the room accepted
    // (so a player who leaves doesn't hold it up), and is cancelled once nobody can still answer it in time
    private static void UpdateAsker()
    {
        var screen = VoteScreen;

        if (!screen || !screen.Visible || screen.Hiding)
        {
            return;
        }

        var waiting = PhotonNetwork.PlayerListOthers.Where(x => !_vote.Accepted.Contains(x.ActorNumber)).ToList();

        if (waiting.Count == 0)
        {
            screen.ProcessVoteAccepted();

            return;
        }

        if (Time.unscaledTime - _vote.StartTime < VoteDeadlineSeconds)
        {
            return;
        }

        Gui.GuiService.ShowAlert(
            Gui.Format("Message/&MultiplayerVoteNoAnswer", string.Join(", ", waiting.Select(x => x.NickName))),
            Gui.ColorFailure, 5f);
        // the vote screen's own Cancel: tells the others, closes it and runs the vote's cancel function
        screen.OnCancelVoteCb();
    }

    #endregion

    #region Other players

    // NetworkingManager.RequestVoteRPC: true when the request has an ID of this mod (the request then goes on its way,
    // with the ID taken out of the parameter); outputs the parameter without it
    internal static bool TryTakeVoteId(string parameter, out string original, out int voteId)
    {
        original = parameter;
        voteId = 0;

        var index = parameter?.LastIndexOf(VoteIdMarker, System.StringComparison.Ordinal) ?? -1;

        if (index < 0 || !int.TryParse(parameter!.Substring(index + VoteIdMarker.Length), out voteId))
        {
            return false;
        }

        original = parameter.Substring(0, index);

        return true;
    }

    // LocalCommandManager.RequestVote, on every computer but the asking one: records the vote; false when the request
    // isn't shown (no ID, so another build sent it; or another vote is running here, and this one is refused)
    internal static bool OnRequest(string caller, string parameter, int voteId, bool hasVoteId, int askerId)
    {
        if (!hasVoteId)
        {
            return false;
        }

        // the request waits for a command tick, but a cancel or outcome arrives at once: a vote that already ended on
        // the asking computer isn't shown when its request comes after (nobody would answer it any more)
        if (EndedVotes.Contains((askerId, voteId)))
        {
            return false;
        }

        if (_vote != null)
        {
            if (_vote.AskerId != askerId || _vote.Id != voteId)
            {
                SendAnswer(voteId, askerId, false, 0, string.Empty);
            }

            return false;
        }

        RegisterEventHandler();
        _vote = new Vote
        {
            Id = voteId,
            AskerId = askerId,
            AskerName = caller ?? string.Empty,
            Parameter = parameter,
            StartTime = Time.unscaledTime
        };

        return true;
    }

    // LocalCommandManager.VoteAccepted / VoteRejected: this player answered the request window
    internal static void OnLocalAnswer(bool accepted)
    {
        if (_vote is not { LocalIsAsker: false })
        {
            return;
        }

        _vote.Answered = true;

        if (accepted)
        {
            ShowVoteScreenToOther();
        }
        else
        {
            OnLocalRefusal();
        }
    }

    // this computer refused (the request window's Refuse or its timer, or the vote screen's Cancel): a refused vote
    // can't pass, and the asking computer's outcome normally ends it here at once; if that outcome never comes (the
    // asking computer had already ended the vote), it is forgotten after RefusedForgetSeconds so it can't block the
    // next vote
    private static void OnLocalRefusal()
    {
        if (_vote is { LocalIsAsker: false } && _vote.RefusedTime < 0f)
        {
            _vote.RefusedTime = Time.unscaledTime;
        }
    }

    // NetworkingManager.AnswerVote (the request window's Accept and Refuse): sent with the vote's ID instead
    internal static void SendLocalAnswer(bool accepted)
    {
        if (_vote is { LocalIsAsker: false, Preview: false })
        {
            SendAnswer(_vote.Id, _vote.AskerId, accepted, 0, string.Empty);
        }
    }

    // an approval this computer can't take part in (a fight or a cutscene): refused with the reason, also after this
    // player accepted, and this computer leaves the vote, closing its vote screen if it shows one
    internal static void RefuseAsBusy(byte reason, string names)
    {
        if (_vote is not { LocalIsAsker: false, Preview: false })
        {
            return;
        }

        var shownHere = _vote.ShownToLocal;

        SendAnswer(_vote.Id, _vote.AskerId, false, reason, names ?? string.Empty);
        _vote = null;
        HideLocalScreens(shownHere);
    }

    // the request window is replaced by the vote screen, which shows who proposed what and everyone's votes so far
    private static void ShowVoteScreenToOther()
    {
        var answerModal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (answerModal && answerModal.Visible && !answerModal.Hiding)
        {
            answerModal.Hide();
        }

        var screen = VoteScreen;

        if (!screen || screen.Visible)
        {
            return;
        }

        _vote.ShownToLocal = true;
        // never run here: the vote passes or ends on the asking computer, and Cancel is handled by OnCancelButton
        screen.VotePassed = null;
        screen.VoteCanceled = null;
        ApplyTexts(screen);
        screen.CollectPlayerInfo();
        screen.Show(false);
    }

    // called every network frame on a computer that was asked: the vote is over if the asking player left, if this
    // computer refused it and no outcome came, or once it ran longer than the asking computer allows any vote (its own
    // deadline cancels it at VoteDeadlineSeconds; a lost outcome or cancel can't keep it here past that)
    private static void UpdateOther()
    {
        var now = Time.unscaledTime;

        if (PhotonNetwork.CurrentRoom?.GetPlayer(_vote.AskerId) != null &&
            (_vote.RefusedTime < 0f || now - _vote.RefusedTime < RefusedForgetSeconds) &&
            now - _vote.StartTime < OtherDeadlineSeconds)
        {
            return;
        }

        RememberEnded(_vote.AskerId, _vote.Id);
        Reset();
    }

    // a vote that ended on its asking computer (or was given up here), so its request isn't shown if it comes late
    private static void RememberEnded(int askerId, int voteId)
    {
        if (!EndedVotes.Add((askerId, voteId)))
        {
            return;
        }

        EndedVoteOrder.Enqueue((askerId, voteId));

        while (EndedVoteOrder.Count > EndedVotesKept)
        {
            EndedVotes.Remove(EndedVoteOrder.Dequeue());
        }
    }

    #endregion

    #region Messages

    private static void RegisterEventHandler()
    {
        if (_eventHandlerRegistered || PhotonNetwork.NetworkingClient == null)
        {
            return;
        }

        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        _eventHandlerRegistered = true;
    }

    private static void SendAnswer(int voteId, int askerId, bool accepted, byte reason, string names)
    {
        Raise(new object[] { AnswerKind, voteId, askerId, accepted, reason, names });
    }

    // NetworkingManager.NotifyVoteAccepted / NotifyVoteRejected, from ProcessVoteAccepted / ProcessVoteRefused on the
    // asking computer: the outcome with the vote's ID instead
    internal static void SendOutcome(bool passed, int refusedBy)
    {
        if (_endedVoteId != 0)
        {
            Raise(new object[] { OutcomeKind, _endedVoteId, passed, refusedBy });
        }
    }

    // NetworkingManager.CancelVote, from the asking player's Cancel or the game closing the screen: with the ID instead
    internal static void SendCancel()
    {
        if (_endedVoteId != 0)
        {
            Raise(new object[] { CancelKind, _endedVoteId });
        }
    }

    private static void Raise(object[] data)
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.OfflineMode)
        {
            return;
        }

        PhotonNetwork.RaiseEvent(EventCode, data, new RaiseEventOptions { Receivers = ReceiverGroup.Others },
            SendOptions.SendReliable);
    }

    // anything that doesn't have the expected layout is ignored
    private static void OnEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode || photonEvent.CustomData is not object[] { Length: >= 2 } data ||
            data[0] is not byte kind || data[1] is not int voteId)
        {
            return;
        }

        var sender = photonEvent.Sender;

        switch (kind)
        {
            case AnswerKind when data.Length >= 6 && data[2] is int askerId && data[3] is bool accepted &&
                                 data[4] is byte reason && data[5] is string names:
                OnAnswer(voteId, askerId, sender, accepted, reason, names);
                break;
            case OutcomeKind when data.Length >= 4 && data[2] is bool passed && data[3] is int refusedBy:
                OnOutcome(voteId, sender, passed, refusedBy);
                break;
            case CancelKind:
                OnCancelled(voteId, sender);
                break;
        }
    }

    // an answer to the running vote, on every computer but the one that answered
    private static void OnAnswer(int voteId, int askerId, int actorNumber, bool accepted, byte reason, string names)
    {
        if (_vote == null || _vote.Preview || _vote.Id != voteId || _vote.AskerId != askerId ||
            actorNumber == _vote.AskerId)
        {
            return;
        }

        if (_vote.LocalIsAsker)
        {
            if (accepted)
            {
                _vote.Accepted.Add(actorNumber);
            }
            else if (reason != 0)
            {
                MultiplayerApprovalContext.OnBusyRefusal(reason, names);
            }

            var screen = VoteScreen;

            // the game's own counting: marks the row, and passes or refuses the vote
            if (screen && screen.Visible && !screen.Hiding)
            {
                screen.PlayerVoteReceived(actorNumber, accepted);
            }

            return;
        }

        if (accepted)
        {
            _vote.Accepted.Add(actorNumber);

            var screen = VoteScreen;

            if (_vote.ShownToLocal && screen && screen.Visible)
            {
                ApplyVotes(screen);
            }

            return;
        }

        // refused: the windows close here now; the asking computer's outcome then names who refused and ends the vote
        var shownHere = _vote.ShownToLocal;

        _vote.ShownToLocal = false;
        HideLocalScreens(shownHere);
    }

    // the asking computer reports the outcome: the game's own local handling (its alert naming who refused, and the
    // request window closing), then the vote screen closes
    private static void OnOutcome(int voteId, int senderId, bool passed, int refusedBy)
    {
        RememberEnded(senderId, voteId);

        if (_vote is not { LocalIsAsker: false, Preview: false } || _vote.Id != voteId || _vote.AskerId != senderId)
        {
            return;
        }

        var localCommands = LocalCommands;

        if (passed)
        {
            localCommands?.NotifyVoteAccepted();
        }
        else
        {
            localCommands?.NotifyVoteRejected(refusedBy);
        }

        Reset();
    }

    // the asking player cancelled: the others are told who (the game named the host), and their screens close
    private static void OnCancelled(int voteId, int senderId)
    {
        // also when the request hasn't arrived yet: it is then not shown when it does
        RememberEnded(senderId, voteId);

        if (_vote is not { LocalIsAsker: false, Preview: false } || _vote.Id != voteId || _vote.AskerId != senderId)
        {
            return;
        }

        var name = _vote.AskerName;

        Reset();
        Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerVoteCancelledBy", name), Gui.ColorAlert, 5f);
    }

    internal static void Update()
    {
        // the preview runs on its own driver and must never pass or cancel through the network
        if (_vote == null || _vote.Preview)
        {
            return;
        }

        if (_vote.LocalIsAsker)
        {
            UpdateAsker();
        }
        else
        {
            UpdateOther();
        }
    }

    #endregion

    #region Vote screen

    // MultiplayerVoteModal.OnCancelVoteCb: false when the button refuses another player's vote instead of cancelling
    internal static bool OnCancelButton(MultiplayerVoteModal screen)
    {
        if (_vote == null)
        {
            return true;
        }

        if (_vote.Preview)
        {
            _vote = null;
            screen.Hide();

            return false;
        }

        if (_vote.LocalIsAsker)
        {
            // the game's cancel: tells the others (SendCancel), closes the screen and runs the vote's cancel function
            OnAskerEndedVote();

            return true;
        }

        _vote.ShownToLocal = false;
        screen.Hide();
        SendLocalAnswer(false);
        OnLocalRefusal();

        return false;
    }

    // MultiplayerStatusModal.OnBeginHide: the game closed the vote screen without the vote ending (a load or a change
    // of game state). On the asking computer the vote is cancelled for everyone, as with Cancel; elsewhere only the
    // screen is gone, which isn't an answer.
    internal static void OnScreenHiding(MultiplayerStatusModal screen)
    {
        if (_vote == null || screen != VoteScreen)
        {
            return;
        }

        if (_vote.Preview)
        {
            _vote = null;

            return;
        }

        if (!_vote.LocalIsAsker)
        {
            _vote.ShownToLocal = false;

            return;
        }

        var voteCanceled = ((MultiplayerVoteModal)screen).VoteCanceled;

        OnAskerEndedVote();
        SendCancel();
        voteCanceled?.Invoke();
    }

    // MultiplayerVoteModal.Refresh binds one row per other player by index; the prefab has 3 rows (4 players), and
    // the mod allows up to 6 players, so rows are added by copying the last one
    internal static void EnsurePlayerRows(MultiplayerVoteModal screen)
    {
        var groups = screen.playerInfoGroups;
        var needed = (ServiceRepository.GetService<INetworkingService>()?.GetPlayersInRoom()?.Count ?? 1) - 1;

#if FB_DEVELOPMENT
        if (_vote is { Preview: true })
        {
            needed = _vote.PreviewPlayers.Count;
        }
#endif

        while (groups.Count > 0 && groups.Count < needed)
        {
            var last = groups[groups.Count - 1];
            var copy = Object.Instantiate(last, last.transform.parent);

            copy.name = $"{last.name} (FB {groups.Count})";
            groups.Add(copy);
        }
    }

    // MultiplayerVoteModal.Refresh binds every row as "waiting"; the votes received so far are marked again. On a
    // computer that was asked, the asking player's row shows accepted.
    internal static void ApplyVotes(MultiplayerVoteModal screen)
    {
        if (_vote == null || screen != VoteScreen || !(_vote.LocalIsAsker || _vote.ShownToLocal))
        {
            return;
        }

        foreach (var group in screen.playerInfoGroups.Where(x =>
                     x.gameObject.activeSelf && x.PlayerSlot != null &&
                     x.PlayerStatus != PlayerInfoGroup.PlayerAbstractStatus.Valid &&
                     (x.PlayerSlot.PlayerId == _vote.AskerId || _vote.Accepted.Contains(x.PlayerSlot.PlayerId))))
        {
            screen.SetPlayerVoteAccepted(group);
        }
    }

    // MultiplayerFastTravelVoteModal.ShowVoteHook and the vote screen of a player who accepted: the title, the proposal
    // line ("You propose a fast travel to" or "<name> proposes a fast travel to") and the destination, or for an
    // approval "Approval Vote", "You propose:" / "<name> proposes:" and the action with its in-game time
    internal static void ApplyTexts(MultiplayerFastTravelVoteModal screen)
    {
        if (_vote == null)
        {
            return;
        }

        CaptureLabels(screen);

        var approval = MultiplayerApprovalContext.DescribeVoteParameter(_vote.Parameter);
        var mine = _vote.LocalIsAsker;
        string title;
        string description;

        if (approval != null)
        {
            title = Gui.Localize("Message/&MultiplayerApprovalVoteTitle");
            description = mine
                ? Gui.Localize("Message/&MultiplayerApprovalVoteDescription")
                : Gui.Format("Message/&MultiplayerApprovalVoteOthersDescription", _vote.AskerName);
        }
        else
        {
            title = _titleText;
            description = mine
                ? _descriptionText
                : Gui.Format("Message/&MultiplayerVoteOthersTravelDescription", _vote.AskerName);
        }

        if (_titleLabel)
        {
            _titleLabel.Text = title;
        }

        if (_descriptionLabel)
        {
            _descriptionLabel.Text = description;
        }

        screen.travelDestinationLabel.Text = approval ?? _vote.Parameter;
    }

    private static void CaptureLabels(MultiplayerFastTravelVoteModal screen)
    {
        if (_titleLabel && _descriptionLabel)
        {
            return;
        }

        var labels = screen.GetComponentsInChildren<GuiLabel>(true);

        _titleLabel = labels.FirstOrDefault(x => x.name == "VoteTitle");
        _descriptionLabel = labels.FirstOrDefault(x => x.name == "VoteDescription");
        _titleText = _titleLabel?.Text;
        _descriptionText = _descriptionLabel?.Text;
    }

    // closes the request window, and the vote screen when it showed this vote here
    private static void HideLocalScreens(bool voteScreen)
    {
        var answerModal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (answerModal && answerModal.Visible && !answerModal.Hiding)
        {
            answerModal.Hide();
        }

        var screen = VoteScreen;

        if (voteScreen && screen && screen.Visible && !screen.Hiding)
        {
            screen.Hide();
        }
    }

    #endregion

#if FB_DEVELOPMENT
    #region Preview

    // A single-player preview of the vote screens with made-up players (user request, 2026-11-01), started from the
    // mod menu (Multiplayer). A development tool: compiled only into development builds (FB_DEVELOPMENT, see the
    // project file), never into a release (user direction, 2026-11-02). It goes through the same screen code as a real vote (texts, rows, extra rows, marking
    // votes, the request window giving way to the vote screen, Cancel), but nothing is sent: the made-up players
    // answer one after another on a timer, and the outcome is shown with the game's own alerts. The game fills the
    // rows from the room's players and the session's player slots, which single player doesn't have for made-up
    // players, so while the preview runs OnCollectPlayerInfo, OnBindRow and OnRefreshAvatar stand in for those
    // lookups. It can't show anything about the network side: who counts the answers, cancels reaching the others,
    // players leaving.

    private const int PreviewFirstPlayerId = -101;
    private const float PreviewSecondsBetweenAnswers = 1.5f;
    // a sample destination for the travel preview (a town of the main campaign)
    private const string PreviewDestination = "Caer Cyflen";
    private static readonly string[] PreviewNames = ["Aria", "Bram", "Corin", "Dessa", "Eldon"];

    internal const int PreviewMaxOtherPlayers = 5;

    // the preview needs a location (the vote screens belong to the game's location screens) and no online game
    internal static bool CanPreview()
    {
        return !Global.IsMultiplayer && Gui.GameLocation != null && VoteScreen &&
               Gui.GuiService.GetScreen<MultiplayerAnswerModal>() && _vote == null;
    }

    // asking: this player proposes and sees the others answer; otherwise the first made-up player proposes and this
    // player gets the request window first. otherPlayers is the number of rows on the vote screen (1 to 5).
    internal static void StartPreview(bool asking, bool approval, int otherPlayers, bool lastRefuses)
    {
        if (!CanPreview())
        {
            return;
        }

        otherPlayers = Mathf.Clamp(otherPlayers, 1, PreviewMaxOtherPlayers);

        var vote = new Vote
        {
            Preview = true, LocalIsAsker = asking, PreviewLastRefuses = lastRefuses
        };

        for (var i = 0; i < otherPlayers; i++)
        {
            vote.PreviewPlayers.Add(new SessionPlayerSlot(PreviewFirstPlayerId - i, PreviewNames[i], PreviewNames[i]));
        }

        var askerName = asking ? PhotonNetwork.LocalPlayer?.NickName ?? string.Empty : PreviewNames[0];
        string requestTitle;
        string requestText;

        if (approval)
        {
            vote.Parameter = MultiplayerApprovalContext.PreviewVoteParameter(askerName, out requestTitle,
                out requestText);
        }
        else
        {
            vote.Parameter = PreviewDestination;
            // the game's own request texts for travel (LocalCommandManager.RequestVote)
            requestTitle = Gui.Localize("Modal/&MapWaypointNavigateVoteTitle");
            requestText = Gui.Format("Modal/&MapWaypointNavigateVoteClientDescription", askerName,
                PreviewDestination);
        }

        vote.AskerId = asking ? PreviewFirstPlayerId + 1 : PreviewFirstPlayerId;
        vote.AskerName = askerName;

        // everyone but the proposer answers
        foreach (var slot in vote.PreviewPlayers.Where(x => x.PlayerId != vote.AskerId))
        {
            vote.PreviewToAnswer.Enqueue(slot.PlayerId);
        }

        vote.PreviewNextAnswerTime = Time.unscaledTime + PreviewSecondsBetweenAnswers;
        _vote = vote;

        var driver = new GameObject("FinishingBusinessVotePreview");

        Object.DontDestroyOnLoad(driver);
        driver.AddComponent<PreviewDriver>();

        if (asking)
        {
            var screen = VoteScreen;

            screen.VotePassed = null;
            screen.VoteCanceled = null;
            ApplyTexts(screen);
            screen.CollectPlayerInfo();
            screen.Show(false);

            return;
        }

        Gui.GuiService.ShowMultiplayerAnswer(requestTitle, requestText,
            () => OnPreviewLocalAnswer(true), () => OnPreviewLocalAnswer(false), true);
    }

    // the request window's Accept (the vote screen follows) or Refuse and its timer (the preview ends)
    private static void OnPreviewLocalAnswer(bool accepted)
    {
        if (_vote is not { Preview: true, LocalIsAsker: false })
        {
            return;
        }

        _vote.Answered = true;

        if (accepted)
        {
            ShowVoteScreenToOther();
        }
        else
        {
            _vote = null;
        }
    }

    // the next made-up answer, and the outcome once everyone answered or someone refused
    private static void UpdatePreview()
    {
        if (Time.unscaledTime < _vote.PreviewNextAnswerTime)
        {
            return;
        }

        _vote.PreviewNextAnswerTime = Time.unscaledTime + PreviewSecondsBetweenAnswers;

        if (_vote.PreviewToAnswer.Count == 0)
        {
            // a player who was asked and hasn't answered yet: the outcome waits for them
            if (!_vote.LocalIsAsker && !_vote.Answered)
            {
                return;
            }

            FinishPreview(null);

            return;
        }

        var playerId = _vote.PreviewToAnswer.Dequeue();

        if (_vote.PreviewLastRefuses && _vote.PreviewToAnswer.Count == 0)
        {
            FinishPreview(_vote.PreviewPlayers.First(x => x.PlayerId == playerId).PlayerNickname);

            return;
        }

        _vote.Accepted.Add(playerId);

        var screen = VoteScreen;

        if (screen && screen.Visible && (_vote.LocalIsAsker || _vote.ShownToLocal))
        {
            ApplyVotes(screen);
        }
    }

    // as the game ends a vote: the alerts it shows on the asking computer (everyone accepted, or who refused), and
    // the screens closing
    private static void FinishPreview(string refusedBy)
    {
        var shownHere = _vote.LocalIsAsker || _vote.ShownToLocal;

        if (refusedBy != null)
        {
            Gui.GuiService.ShowAlert(Gui.Format("Message/&MultiplayerVoteClientRejectedDescription", refusedBy),
                Gui.ColorFailure, 2.5f);
        }
        else if (_vote.LocalIsAsker)
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Message/&MultiplayerVoteClientAllClientsAcceptedDescription"),
                Gui.ColorSuccess, 2.5f);
        }

        _vote = null;
        HideLocalScreens(shownHere);
    }

    // MultiplayerVoteModal.CollectPlayerInfo: the made-up players instead of the room's (false skips the game's)
    internal static bool OnCollectPlayerInfo(MultiplayerVoteModal screen)
    {
        if (_vote is not { Preview: true })
        {
            return true;
        }

        screen.ClientInfos.Clear();

        foreach (var slot in _vote.PreviewPlayers)
        {
            screen.ClientInfos.Add(new NetworkingDefinitions.PlayerInfo
            {
                actorNumber = slot.PlayerId, nickname = slot.PlayerNickname, uniqueId = string.Empty
            });
        }

        return false;
    }

    // MultiplayerVoteModal.BindUnreadyPlayerInfoGroup: a made-up player's row, bound as the game binds a real one
    // (false skips the game's, which looks the player up in the session)
    internal static bool OnBindRow(PlayerInfoGroup group, int playerId)
    {
        var slot = _vote is { Preview: true } ? _vote.PreviewPlayers.FirstOrDefault(x => x.PlayerId == playerId) : null;

        if (slot == null)
        {
            return true;
        }

        group.gameObject.SetActive(true);
        group.Bind(slot, true, true, false, false, true, false, false,
            PlayerInfoGroup.PlayerAbstractStatus.Pending);

        return false;
    }

    // PlayerInfoGroup.RefreshPlayerAvatar: a made-up player gets one of the game's default avatars (the game asks the
    // Photon room for the player, which single player doesn't have). As the game's own fallback: false = no user avatar.
    internal static bool OnRefreshAvatar(int playerId, RawImage playerImage,
        List<AssetReferenceSprite> defaultPlayerSpriteReferences, ref Sprite defaultSprite, ref bool result)
    {
        if (_vote is not { Preview: true } || _vote.PreviewPlayers.All(x => x.PlayerId != playerId) ||
            defaultPlayerSpriteReferences is not { Count: > 0 })
        {
            return true;
        }

        var index = (PreviewFirstPlayerId - playerId) % defaultPlayerSpriteReferences.Count;

        defaultSprite = Gui.LoadAssetSync<Sprite>(defaultPlayerSpriteReferences[index]);
        playerImage.texture = defaultSprite.texture;
        playerImage.gameObject.SetActive(true);
        result = false;

        return false;
    }

    #endregion
#endif

    private sealed class Vote
    {
        // players who accepted so far (not the asking player, who proposed it)
        internal readonly HashSet<int> Accepted = [];
        // set by the asking computer; every message about this vote carries it
        internal int Id;
        // when this computer learned of the vote (the asking computer's deadline counts from it)
        internal float StartTime;
        internal int AskerId;
        internal string AskerName;
        // the travel destination, or an approval's encoded action (MultiplayerApprovalContext.DescribeVoteParameter)
        internal string Parameter;
        internal bool LocalIsAsker;
        // a computer that was asked: it answered the request window
        internal bool Answered;
        // a computer that was asked: the vote screen shows this vote (after Accept)
        internal bool ShownToLocal;
        // a computer that was asked: when it refused the vote, -1 until then
        internal float RefusedTime = -1f;

#if FB_DEVELOPMENT
        // the single-player preview (StartPreview): made-up players, nothing sent
        internal bool Preview;
        internal readonly List<SessionPlayerSlot> PreviewPlayers = [];
        // the made-up players still to answer, in order, and when the next one does
        internal readonly Queue<int> PreviewToAnswer = new();
        internal float PreviewNextAnswerTime;
        internal bool PreviewLastRefuses;
#else
        // the preview exists in development builds only, so no vote is one here
        internal bool Preview => false;
#endif
    }

#if FB_DEVELOPMENT
    // runs the preview's made-up answers each frame; it removes itself once the preview is over
    private sealed class PreviewDriver : MonoBehaviour
    {
        private void Update()
        {
            if (_vote is not { Preview: true })
            {
                Destroy(gameObject);

                return;
            }

            UpdatePreview();
        }
    }
#endif
}
