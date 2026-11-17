using System;
using System.Collections.Generic;
using System.Linq;
using Photon.Pun;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Multiplayer approval of actions that pass in-game time (rests, learning or scribing spells, Mark Deity, rituals and
// spells or powers that take a minute or more). The action waits on the computer that asked for it until every other
// player approves, then goes through the lockstep as usual.
//
// The approval is the game's own fast travel vote, with only its texts changed: the
// asking computer calls MultiplayerVoteModal.ShowVote with an encoded parameter (EncodeVoteParameter) and the action
// as the function to run when the vote passes; the game sends the request, counts the answers and reports the outcome.
// MultiplayerVoteContext makes that vote work whoever asks, and shows it to every player; this class supplies the
// approval's texts (DescribeVoteParameter, ShowRequest) and the parts the game's vote doesn't have:
// - nobody is asked while a character is in combat or in a cutscene: the asking player is told which character is
//   busy instead (before asking, and when another computer is busy as the request arrives: that computer refuses
//   with the reason, MultiplayerVoteContext.RefuseAsBusy);
// - a fight or cutscene starting while the vote runs cancels it: on the asking computer, and on any other computer,
//   whether its player answered yet or already accepted, which refuses with the reason;
//   a vote that passes just as the asking computer gets busy doesn't run its action. The cutscene itself goes on as
//   the game runs it (it stops every character and places the party), as nothing of the vote holds it up.
//
// The request reaches the other computers without the game's travel request step (MultiplayerVoteModalPatcher's
// RequestVoteRPC patch): that step stops every character's current action and drops the request, without an answer,
// where a game script or a save-blocking action is running, neither of which applies to an approval.
internal static class MultiplayerApprovalContext
{
    internal enum ApprovalAction : byte
    {
        ShortRest = 1,
        LongRest = 2,
        LearnSpell = 3,
        ScribeScroll = 4,
        MarkDeity = 5,
        CastSpell = 6,
        CastRitual = 7,
        UsePower = 8,
        WaitAndLongRest = 9,
        // the party gathers at a campfire (the whole party fast travels there), then takes a long rest
        // (CampfireRestContext); no character name
        CampfireLongRest = 10,
        // the party already rested today at a campfire: it gathers there, waits until it can rest again, then takes a
        // long rest (CampfireRestContext); no character name, the subject part holds the hours of the wait
        CampfireWaitAndLongRest = 11
    }

    // the busy refusal's reasons (sent as a byte in the vote's answer, 0 for none)
    private enum Answer : byte
    {
        None = 0,
        InCombat = 2,
        InCutscene = 3
    }

    // marks a vote parameter as an approval (a travel vote's parameter is the destination's name); the parts are
    // separated by PartSeparator: marker, action, character name, subject definition name, seconds
    private const string ApprovalMarker = "\u0001FinishingBusinessApproval";
    private const char PartSeparator = '\u0002';
    // GameCampActivityMarkDeity's duration
    internal const int MarkDeitySeconds = 600;
    // actions up to this long can skip approval (rituals, 1 and 10 minute castings, Mark Deity)
    private const int ShortActionSeconds = 600;

    // the approval this computer asked for, while its vote runs
    private static PendingRequest _pending;

    // set while an approved action runs again, so the patches let it through
    internal static bool IsRunningApprovedAction { get; private set; }

    private static bool IsOnlineRoom =>
        Global.IsMultiplayer && PhotonNetwork.InRoom && !PhotonNetwork.OfflineMode;

    // leaving or joining a room: forgets the approval and the vote, closing their screens
    internal static void Reset()
    {
        _pending = null;
        MultiplayerVoteContext.Reset();
        CampfireRestContext.Reset();
    }

    #region Asking

    // true when the action must wait: it then runs through proceed once every other player approves
    internal static bool MustWaitForApproval(
        ApprovalAction action, string characterName, BaseDefinition subject, int seconds, Action proceed)
    {
        return MustWaitForApprovalWithSubjectName(action, characterName, subject?.Name ?? string.Empty, seconds,
            proceed);
    }

    // the same with the subject part given as text (a definition's name, or a value such as the hours of a wait)
    internal static bool MustWaitForApprovalWithSubjectName(
        ApprovalAction action, string characterName, string subjectName, int seconds, Action proceed)
    {
        if (IsRunningApprovedAction ||
            !Main.Settings.MultiplayerRequireApprovalForTimeConsumingActions ||
            !IsOnlineRoom ||
            seconds <= 0 ||
            (Main.Settings.MultiplayerApprovalSkipActionsOfTenMinutesOrLess && seconds <= ShortActionSeconds) ||
            PhotonNetwork.PlayerListOthers.Length == 0)
        {
            return false;
        }

        if (_pending != null || MultiplayerVoteContext.IsVoteRunning)
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Message/&MultiplayerVoteAlreadyRunning"), Gui.ColorFailure, 5f);

            return true;
        }

        var busy = DescribeLocalBusy(out _);

        if (busy != null)
        {
            ShowBusy(busy);

            return true;
        }

        var screen = Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

        // the game checks this when the request arrives on each computer (NetworkingManager.RequestVoteAsync, "nothing
        // running that blocks saving") and would tell the asking player that fast travel isn't possible; checked here
        // first so an approval doesn't get the travel message
        if (!screen || screen.Visible ||
            ServiceRepository.GetService<IFastTravelService>() is not { CanFastTravel: true })
        {
            Gui.GuiService.ShowAlert(Gui.Localize("Failure/&FailureGenericTryAgain"), Gui.ColorFailure, 5f);

            return true;
        }

        var pending = new PendingRequest { Proceed = proceed };

        _pending = pending;
        // the game's vote: sends the request to everyone (MultiplayerVoteContext swaps in the approval's texts), counts
        // the answers on this computer and runs one of the two functions
        screen.ShowVote(NetworkingDefinitions.MultiplayerVoteType.MapWaypointNavigate,
            EncodeVoteParameter(action, characterName, subjectName, seconds),
            () => OnVotePassed(pending),
            () => OnVoteCanceled(pending));

        return true;
    }

    // everyone approved (the game shows its own "everyone accepted" alert): the action runs
    private static void OnVotePassed(PendingRequest pending)
    {
        if (_pending != pending)
        {
            return;
        }

        _pending = null;

        // the vote passed in the same frame a fight or cutscene started here, before Update could cancel it
        var busy = DescribeLocalBusy(out _);

        if (busy != null)
        {
            ShowBusy(busy);

            return;
        }

        IsRunningApprovedAction = true;

        try
        {
            pending.Proceed();
        }
        finally
        {
            IsRunningApprovedAction = false;
        }
    }

    // runs an action that an earlier approval already covered, past the patches that would ask again (the wait and
    // long rest after a campfire's gather, approved with the gather)
    internal static void RunApproved(Action action)
    {
        var wasRunning = IsRunningApprovedAction;

        IsRunningApprovedAction = true;

        try
        {
            action();
        }
        finally
        {
            IsRunningApprovedAction = wasRunning;
        }
    }

    // refused, cancelled, or the game closed the vote: the action doesn't run. The game already said who refused;
    // a refusal for being busy also says which character is busy.
    private static void OnVoteCanceled(PendingRequest pending)
    {
        if (_pending != pending)
        {
            return;
        }

        _pending = null;

        if (pending.BusyReason != null)
        {
            ShowBusy(pending.BusyReason);
        }
    }

    // another computer refused because one of its characters is busy (MultiplayerVoteContext, before the game counts
    // the refusal): the reason is shown once the vote is cancelled
    internal static void OnBusyRefusal(byte reason, string names)
    {
        if (_pending == null || (Answer)reason is not (Answer.InCombat or Answer.InCutscene))
        {
            return;
        }

        _pending.BusyReason = Gui.Format((Answer)reason == Answer.InCombat
            ? "Message/&MultiplayerApprovalInCombat"
            : "Message/&MultiplayerApprovalInCutscene", names);
    }

    // called every network frame: a fight or cutscene starting while the vote runs ends it
    internal static void Update()
    {
        MultiplayerVoteContext.Update();

        if (!IsOnlineRoom)
        {
            return;
        }

        if (_pending != null)
        {
            var busy = DescribeLocalBusy(out _);
            var screen = Gui.GuiService.GetScreen<MultiplayerFastTravelVoteModal>();

            if (busy != null && screen && screen.Visible && !screen.Hiding)
            {
                _pending.BusyReason = busy;
                // the vote screen's own Cancel: tells the others, closes it and cancels the approval
                screen.OnCancelVoteCb();
            }

            return;
        }

        // an approval this computer was asked for, answered or not, is refused with the reason if this computer gets
        // busy: a player who already accepted takes the acceptance back, which ends the vote for everyone
        if (MultiplayerVoteContext.RequestParameter != null &&
            DescribeVoteParameter(MultiplayerVoteContext.RequestParameter) != null &&
            DescribeLocalBusy(out var answer) != null)
        {
            MultiplayerVoteContext.RefuseAsBusy((byte)answer, DescribeLocalBusyNames(answer));
            HideAnswerModal();
        }
    }

    private static void ShowBusy(string reason)
    {
        Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Message/&MultiplayerApprovalBusyTitle",
            Gui.Format("Message/&MultiplayerApprovalBusyDescription", reason),
            "Message/&MessageOkTitle", string.Empty, null, null);
    }

    #endregion

    #region Answering

    // LocalCommandManager.RequestVote for an approval (true; false for a travel vote, which the game shows as usual):
    // the game's request window with the approval's texts and the game's own Accept and Refuse, or the busy reply
    internal static bool ShowRequest(LocalCommandManager commandManager, string caller, string parameter)
    {
        if (!TryDecodeVoteParameter(parameter, out var action, out var characterName, out var subjectName,
                out var seconds))
        {
            return false;
        }

        if (DescribeLocalBusy(out var answer) != null)
        {
            MultiplayerVoteContext.RefuseAsBusy((byte)answer, DescribeLocalBusyNames(answer));

            return true;
        }

        DescribeRequest(caller, action, characterName, subjectName, seconds, out var title, out var text);
        // as the game's LocalCommandManager.RequestVote does for travel, with these texts
        Gui.GuiService.ShowMultiplayerAnswer(title, text, commandManager.VoteAccepted, commandManager.VoteRejected,
            true);

        return true;
    }

    private static void DescribeRequest(string caller, ApprovalAction action, string characterName,
        string subjectName, int seconds, out string title, out string text)
    {
        title = Gui.Localize("Message/&MultiplayerApprovalRequestTitle");
        text = Gui.Format("Message/&MultiplayerApprovalRequestDescription", caller,
            DescribeAction(action, characterName, subjectName), FormatDuration(seconds));
    }

#if FB_DEVELOPMENT
    // the vote screen preview (MultiplayerVoteContext.StartPreview, development builds only): a short rest by the
    // first party character, as the vote parameter and as the request window's texts for a player named caller
    internal static string PreviewVoteParameter(string caller, out string requestTitle, out string requestText)
    {
        var characterName = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters
            .FirstOrDefault()?.Name ?? caller;
        var seconds = RestSeconds(RestType.ShortRest);

        DescribeRequest(caller, ApprovalAction.ShortRest, characterName, string.Empty, seconds, out requestTitle,
            out requestText);

        return EncodeVoteParameter(ApprovalAction.ShortRest, characterName, string.Empty, seconds);
    }
#endif

    private static void HideAnswerModal()
    {
        var answerModal = Gui.GuiService.GetScreen<MultiplayerAnswerModal>();

        if (answerModal && answerModal.Visible && !answerModal.Hiding)
        {
            answerModal.Hide();
        }
    }

    #endregion

    #region Vote parameter

    private static string EncodeVoteParameter(ApprovalAction action, string characterName, string subjectName,
        int seconds)
    {
        return string.Join(PartSeparator.ToString(), ApprovalMarker, ((byte)action).ToString(),
            characterName ?? string.Empty, subjectName ?? string.Empty, seconds.ToString());
    }

    private static bool TryDecodeVoteParameter(string parameter, out ApprovalAction action, out string characterName,
        out string subjectName, out int seconds)
    {
        action = default;
        characterName = subjectName = string.Empty;
        seconds = 0;

        if (parameter == null || !parameter.StartsWith(ApprovalMarker + PartSeparator))
        {
            return false;
        }

        var parts = parameter.Split(PartSeparator);

        if (parts.Length != 5 || !byte.TryParse(parts[1], out var actionValue) ||
            !int.TryParse(parts[4], out seconds))
        {
            return false;
        }

        action = (ApprovalAction)actionValue;
        characterName = parts[2];
        subjectName = parts[3];

        return true;
    }

    internal static bool IsApprovalParameter(string parameter)
    {
        return TryDecodeVoteParameter(parameter, out _, out _, out _, out _);
    }

    // the approval's action with its in-game time, as the vote screen shows it, or null for a travel vote
    internal static string DescribeVoteParameter(string parameter)
    {
        return TryDecodeVoteParameter(parameter, out var action, out var characterName, out var subjectName,
            out var seconds)
            ? Gui.Format("Message/&MultiplayerApprovalVoteAction", DescribeAction(action, characterName, subjectName),
                FormatDuration(seconds))
            : null;
    }

    #endregion

    #region Busy

    // the reason this computer can't be asked right now (a fight or a cutscene), or null
    private static string DescribeLocalBusy(out Answer answer)
    {
        if (Gui.Battle != null)
        {
            answer = Answer.InCombat;

            return Gui.Format("Message/&MultiplayerApprovalInCombat", DescribeLocalBusyNames(answer));
        }

        if (ServiceRepository.GetService<INarrativeDirectionService>() is { IsSequenceInProgress: true })
        {
            answer = Answer.InCutscene;

            return Gui.Format("Message/&MultiplayerApprovalInCutscene", DescribeLocalBusyNames(answer));
        }

        answer = Answer.None;

        return null;
    }

    // this player's characters taking part, or every party character when none of them is
    private static string DescribeLocalBusyNames(Answer answer)
    {
        var party = ServiceRepository.GetService<IGameLocationCharacterService>()?.PartyCharacters ?? [];
        var involved = answer == Answer.InCombat && Gui.Battle != null
            ? party.Where(x => Gui.Battle.PlayerContenders.Contains(x)).ToList()
            : party.ToList();
        var mine = Gui.ActivePlayerController?.ControlledCharacters ?? [];
        var names = involved.Where(x => mine.Contains(x)).ToList();

        if (names.Count == 0)
        {
            names = involved;
        }

        return names.Count == 0
            ? PhotonNetwork.LocalPlayer?.NickName ?? "?"
            : string.Join(", ", names.Select(x => x.Name));
    }

    #endregion

    #region Text

    private static string DescribeAction(ApprovalAction action, string characterName, string subjectName)
    {
        var subject = subjectName;

        // the wait is sent in hours and worded on each computer
        if (action == ApprovalAction.WaitAndLongRest && int.TryParse(characterName, out var hours))
        {
            characterName = FormatDuration(hours * 3600);
        }

        // the gather before a wait: the wait, sent in hours too
        if (action == ApprovalAction.CampfireWaitAndLongRest && int.TryParse(subjectName, out var waitHours))
        {
            return Gui.Format($"Message/&MultiplayerApprovalAction{action}", characterName,
                FormatDuration(waitHours * 3600));
        }

        if (DatabaseRepository.GetDatabase<SpellDefinition>().TryGetElement(subjectName, out var spell))
        {
            subject = spell.FormatTitle();
        }
        else if (DatabaseRepository.GetDatabase<FeatureDefinitionPower>().TryGetElement(subjectName, out var power))
        {
            subject = power.FormatTitle();
        }

        return Gui.Format($"Message/&MultiplayerApprovalAction{action}", characterName, subject);
    }

    internal static string FormatDuration(int seconds)
    {
        var hours = seconds / 3600;
        var minutes = (seconds % 3600 + 59) / 60;
        var parts = new List<string>();

        if (hours > 0)
        {
            parts.Add(Gui.Format(hours == 1 ? "Message/&MultiplayerApprovalHour" : "Message/&MultiplayerApprovalHours",
                hours.ToString()));
        }

        if (minutes > 0 || hours == 0)
        {
            parts.Add(Gui.Format(
                minutes == 1 ? "Message/&MultiplayerApprovalMinute" : "Message/&MultiplayerApprovalMinutes",
                minutes.ToString()));
        }

        return string.Join(" ", parts);
    }

    #endregion

    #region Durations

    internal static int RestSeconds(RestType restType)
    {
        return RestDurationInSeconds.TryGetValue(restType, out var seconds) ? seconds : 0;
    }

    // in-game time an action passes, as the game counts it when the action runs (0 for none)
    internal static int ActionSeconds(CharacterActionParams actionParams, out ApprovalAction action,
        out BaseDefinition subject)
    {
        action = ApprovalAction.CastSpell;
        subject = null;

        int seconds;

        switch (actionParams?.RulesetEffect)
        {
            case RulesetEffectSpell spell:
                subject = spell.SpellDefinition;

                if (spell.SlotLevel < 0 && spell.SpellRepertoire == null && spell.SpellDefinition.Ritual)
                {
                    action = ApprovalAction.CastRitual;
                    seconds = ComputeActivationTimeSeconds(spell.SpellDefinition.RitualCastingTime);
                }
                else
                {
                    seconds = ComputeActivationTimeSeconds(spell.SpellDefinition.ActivationTime);
                }

                break;
            case RulesetEffectPower power:
                action = ApprovalAction.UsePower;
                subject = power.PowerDefinition;
                seconds = ComputeActivationTimeSeconds(power.PowerDefinition.ActivationTime);
                break;
            default:
                return 0;
        }

        // a feast (Heroes' Feast) passes its hours after the casting
        seconds += actionParams.RulesetEffect.EffectDescription.EffectForms
            .Where(x => x.FormType == EffectForm.EffectFormType.Alteration &&
                        x.AlterationForm.AlterationType == AlterationForm.Type.Feast)
            .Sum(x => x.AlterationForm.FeastDurationHours * 3600);

        return seconds;
    }

    #endregion

    // a room was joined: forgets any approval and vote from before
    internal static void OnJoinedRoom()
    {
        _pending = null;
        MultiplayerVoteContext.OnJoinedRoom();
        CampfireRestContext.Reset();
    }

    private sealed class PendingRequest
    {
        internal Action Proceed;
        // set when the vote ends because a character is busy, shown once it is cancelled
        internal string BusyReason;
    }
}
