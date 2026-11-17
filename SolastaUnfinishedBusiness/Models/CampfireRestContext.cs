using System.Collections;
using System.Linq;
using static RuleDefinitions;

namespace SolastaUnfinishedBusiness.Models;

// Long rests at campfires (FunctorStartLongRest, the campfire gadget's functor):
// 1. In multiplayer any player can start the long rest at a campfire, not only the host. The game refuses clients
//    (GameRestingManager.CanExecuteLocationRest with player privileges, "only the host can rest"); this follows the
//    setting that already lets every player start rests from the rest screen (MultiplayerAllowPlayersToStartRests).
// 2. When the party is out of the long rest range, an ordinary campfire's long rest gathers it at the campfire first
//    (setting GatherPartyForCampfireRest): the whole party, dead, dying and unconscious characters and guests
//    included, fast travels to the campfire's point (CampfireFastTravelContext), and only when somebody is out of
//    the long rest range. Out of range means the game's rule fails (two living members more than 10 cells
//    apart) or anybody, downed included, is more than 10 cells from the campfire's user
//    (CampfireFastTravelContext.NeedsTravel); otherwise the rest runs as without the mod. There is no walk: if the
//    game can't fast travel, the travel fails or enemies interrupt it, nobody rests and the player is told. A party
//    too spread out for the game's rule can still use the campfire, since the gather brings it together; the approval
//    says the party gathers.
// 3. A campfire set to rest at once starts its rest without the multiplayer approval or a gather, as in the game (it
//    doesn't ask for one).
//
// How the game runs a campfire: the functor runs on every computer (gadget functors are part of the lockstep), but
// only the computer whose player controls the character that used the campfire opens the rest screen
// (FunctorStartLongRest.IsRestInitiatorControlledByPlayer). An ordinary campfire checks the rest rules first, then
// opens the rest screen with food consumed, and its Start Long Rest button (RestModal.OnStartLongRestCb) starts the
// rest. A campfire set to rest at once (the functor's bool parameter) skips the checks, opens the screen without food
// and presses Start itself. Rests on the world map are the travel's own (GameTravelScheduler), not this functor.
//
// The gather, on the computer that pressed Start (after the approval, in multiplayer): the rest screen closes, as it
// pauses game time while open (RestModal.OnBeginShow calls GameTime.Pause, OnEndHide resumes it); the party fast
// travels; once the game confirms the party arrived and the rest checks pass again, the rest screen opens again with
// the game's Start pressed, as the game does for a campfire that rests at once. A fight, a cutscene or another rest
// screen opening ends the gather without a rest.
//
// A long rest that has to wait first (the party already rested that day) gathers before the wait. The game checks the
// once-a-day rule before the distance (GameRestingManager.CanExecuteLocationRest), so a campfire offers its wait
// ("Waiting to Perform a Long Rest", FunctorStartLongRest.DoWaitLongRest) even for a spread-out party, and the long
// rest after the wait (StartDeferredLongRestAsync) would then refuse it as spread out, 16 hours later. Both waits, that
// one and the rest screen's combined wait and long rest (LongRestWaitContext), therefore gather the party at the
// campfire first, then wait;
// in multiplayer the gather, the wait and the rest are approved together.
internal static class CampfireRestContext
{
    private const string PartyTooSpreadOut = "Failure/&FailureFlagPartyTooSpreadOut";

    // the campfire whose functor runs or whose rest screen is open on this computer
    private static Campfire _campfire;

    // the campfire whose functor offered the game's wait before the long rest (its check failed on the once-a-day
    // rule), kept for the wait prompt's Yes, which runs after the functor ended
    private static Campfire _waitCampfire;

    // set while the functor runs on this computer (each step of it)
    internal static bool InFunctor { get; private set; }

    // set while the rest checks run without the campfire's spread-out allowance (after the gather, the real distance)
    private static bool SuppressSpreadOutAllowance { get; set; }

    // set while the game's Start Long Rest runs again after the gather
    internal static bool IsReplayingStart { get; private set; }

    // the gather (the fast travel) under way on this computer
    internal static bool IsGathering => _gather != null;

    private static Gather _gather;

    // a campfire set to rest at once pressing Start itself: no rest checks, no "wait until the party can rest" (the
    // campfire skipped them), no gather and no approval
    internal static bool IsForcedStart => _campfire is { Forced: true } && InFunctor;

    // FunctorStartLongRest.Execute: runs each step of the functor with the campfire known
    internal static IEnumerator WrapFunctor(IEnumerator functor, FunctorParametersDescription parameters)
    {
        var campfire = new Campfire
        {
            User = parameters?.ActingCharacters?.FirstOrDefault(x => x != null),
            Forced = parameters?.BoolParameter == true
        };

        // an earlier campfire's wait prompt that was answered No
        _waitCampfire = null;

        // the campfire's fast travel destination, on every computer (the functor runs on each in the lockstep): an
        // existing fast travel waypoint next to the campfire, or a temporary one (CampfireFastTravelContext); a
        // campfire that rests at once has no gather
        if (Gathers(campfire))
        {
            CampfireFastTravelContext.PrepareDestination(campfire.User);
        }
        else
        {
            CampfireFastTravelContext.RemoveDestination();
        }

        while (true)
        {
            bool moved;

            _campfire = campfire;
            InFunctor = true;

            try
            {
                moved = functor.MoveNext();
            }
            finally
            {
                InFunctor = false;
            }

            if (!moved)
            {
                yield break;
            }

            yield return functor.Current;
        }
    }

    // CanExecuteLocationRest: lets the campfire's functor check the rest without the host-only rule, as the rest
    // screen does (MultiplayerAllowPlayersToStartRests)
    internal static bool SkipsPlayerPrivileges => InFunctor && Main.Settings.MultiplayerAllowPlayersToStartRests;

    // CanExecuteLocationRest failed only because the party is spread out: true at a campfire whose gather fixes that
    // (the functor's check, the rest screen's button and Start, before the gather)
    internal static bool AllowsSpreadOut(RestType restType, string failureFlag)
    {
        return restType == RestType.LongRest &&
               failureFlag == PartyTooSpreadOut &&
               !SuppressSpreadOutAllowance &&
               Gathers(CurrentCampfire());
    }

    // CanExecuteLocationRest's result: when the campfire functor's own check fails on the once-a-day rule, the game
    // offers its wait next (FunctorStartLongRest.Execute), so the campfire is kept for that prompt's Yes
    internal static void OnRestChecked(RestType restType, string failureFlag, bool result)
    {
        if (InFunctor && !result && restType == RestType.LongRest &&
            failureFlag == LongRestWaitContext.RestTooOften && _campfire is { Forced: false })
        {
            _waitCampfire = _campfire;
        }
    }

    // the campfire this computer's rest belongs to: during its functor, while its rest screen is open, or while it
    // gathers
    private static Campfire CurrentCampfire()
    {
        if (_campfire == null || InFunctor || IsGathering || IsReplayingStart)
        {
            return _campfire;
        }

        var restModal = Gui.GuiService?.GetScreen<RestModal>();

        if (!restModal || !restModal.Visible)
        {
            _campfire = null;
        }

        return _campfire;
    }

    // an ordinary campfire (not one that rests at once) with the setting on: its long rest always gathers first
    private static bool Gathers(Campfire campfire)
    {
        return campfire is { Forced: false, User: not null } && Main.Settings.GatherPartyForCampfireRest;
    }

    // the rest screen's long rest belongs to a campfire whose party is out of range, so it gathers first
    internal static bool GathersAtCampfire()
    {
        var campfire = CurrentCampfire();

        return Gathers(campfire) && CampfireFastTravelContext.NeedsTravel(campfire.User);
    }

    // the game's long rest check as the rest screen runs it (every player may rest, with the setting), without the
    // campfire's spread-out allowance, and optionally without the once-a-day rule (LongRestWaitContext's switch, which
    // the hero's CanRest patch reads)
    private static bool CheckLongRest(out string failure, bool ignoreRestFrequency = false)
    {
        var inRestScreen = Patches.GameRestingManagerPatcher.InRestScreen;
        var ignoredRestFrequency = LongRestWaitContext.IgnoreRestFrequency;

        failure = string.Empty;
        SuppressSpreadOutAllowance = true;
        Patches.GameRestingManagerPatcher.InRestScreen = true;
        LongRestWaitContext.IgnoreRestFrequency = ignoreRestFrequency;

        try
        {
            return ServiceRepository.GetService<IGameRestingService>()
                ?.CanExecuteLocationRest(RestType.LongRest, ref failure, true) == true;
        }
        finally
        {
            SuppressSpreadOutAllowance = false;
            Patches.GameRestingManagerPatcher.InRestScreen = inRestScreen;
            LongRestWaitContext.IgnoreRestFrequency = ignoredRestFrequency;
        }
    }

    // RestModal.OnBeginShow: a rest screen that isn't the campfire's own (opened from the party bar, after a wait, or
    // after a world map rest) forgets the campfire; any rest screen but the one reopened after the gather also ends a
    // gather under way without a rest (the player started over; a fast travel already queued still runs)
    internal static void OnRestScreenShown()
    {
        if (IsReplayingStart)
        {
            return;
        }

        EndGather();

        if (!InFunctor)
        {
            _campfire = null;
        }
    }

    // after the gather: opens the rest screen again and presses the game's Start Long Rest, past the approval and the
    // campfire's own handling; the same steps as the game's campfire that rests at once (FunctorStartLongRest.Execute:
    // ForceRestType, Show(true), OnStartLongRestCb), keeping the food choice the campfire's screen had
    private static void ReplayStart(RestModal restModal, bool consumeFood)
    {
        // the location changed during the gather (a fight or cutscene would have ended it first)
        if (!restModal)
        {
            return;
        }

        IsReplayingStart = true;

        try
        {
            if (!restModal.Visible)
            {
                restModal.ForceRestType(RestType.LongRest, consumeFood);
                restModal.Show(true);
            }

            restModal.OnStartLongRestCb();
        }
        finally
        {
            IsReplayingStart = false;
        }
    }

    // the campfire rest screen's Start (after the approval in multiplayer): the rest screen closes, the whole party
    // fast travels to the campfire, then the screen opens again and the rest starts
    internal static void StartGather(RestModal restModal)
    {
        // the screen keeps its food choice (the campfire opened it with food consumed) in a field that Start reads
        var consumeFood = restModal.consumeFood;

        if (!StartGather(_campfire, false, () => ReplayStart(restModal, consumeFood)))
        {
            return;
        }

        // closing the screen resumes game time (RestModal.OnEndHide), which the travel needs; StartGather set the
        // gather first, so the campfire is kept while the screen is closed
        if (restModal.Visible)
        {
            restModal.Hide();
        }
    }

    // a long rest that waits first at a campfire: the whole party fast travels to the campfire, then the game's wait
    // and long rest run (ICommandService.StartDeferredLongRest, which rests once the wait is over). Called from the
    // game's campfire wait prompt (FunctorStartLongRest.DoWaitLongRest) and from the rest screen's combined wait and
    // long rest (LongRestWaitContext); true when the gather took over (or a rest rule other than the distance already
    // fails, which is shown instead of waiting for nothing), false when it isn't a gathering campfire or the party is
    // in range, and the caller waits as usual. In multiplayer the gather, the wait and the rest are approved together, and the wait after the
    // gather isn't asked about again.
    internal static bool TryGatherBeforeWait()
    {
        // the game's prompt kept its campfire; the rest screen's wait finds its campfire on the open screen
        var campfire = _waitCampfire ?? CurrentCampfire();

        _waitCampfire = null;

        if (!Gathers(campfire))
        {
            return false;
        }

        // every rule but the once-a-day one (the wait is for it) and the distance (the gather is for it)
        if (!CheckLongRest(out var failure, true) && failure != PartyTooSpreadOut)
        {
            Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);

            return true;
        }

        // in range: the game's wait and rest as usual
        if (!CampfireFastTravelContext.NeedsTravel(campfire.User))
        {
            return false;
        }

        // the leader can't lead the travel: said instead of waiting for nothing
        if (!LeaderCanTravel())
        {
            return true;
        }

        var hours = LongRestWaitContext.MissingHours();

        if (!MultiplayerApprovalContext.MustWaitForApprovalWithSubjectName(
                MultiplayerApprovalContext.ApprovalAction.CampfireWaitAndLongRest, string.Empty, hours.ToString(),
                (hours * 3600) + MultiplayerApprovalContext.RestSeconds(RestType.LongRest),
                () => StartGather(campfire, true, WaitThenRest)))
        {
            StartGather(campfire, true, WaitThenRest);
        }

        return true;
    }

    // after the gather before a wait: the game's wait and long rest, already approved with the gather; the hours are
    // counted again, as the game counts them, since the travel took a little game time
    private static void WaitThenRest()
    {
        MultiplayerApprovalContext.RunApproved(() =>
            ServiceRepository.GetService<ICommandService>()?.StartDeferredLongRest(LongRestWaitContext.MissingHours()));
    }

    // the gather: the whole party fast travels to the campfire's point (CampfireFastTravelContext), then arrived runs
    // once the rest rules pass (before a wait, all but the once-a-day rule). There is no walk: when the game can't fast
    // travel now, the gather fails with its message and nobody rests. False when it couldn't start.
    private static bool StartGather(Campfire campfire, bool beforeWait, System.Action arrived)
    {
        if (!Gathers(campfire))
        {
            return false;
        }

        // checked again here, as the leader may have gone down while the approval ran
        if (!LeaderCanTravel())
        {
            CampfireFastTravelContext.OnGatherAbandoned();

            return false;
        }

        var gather = new Gather { Arrived = arrived, BeforeWait = beforeWait, User = campfire.User };

        if (!CampfireFastTravelContext.TryStart(campfire.User, () => _gather == gather,
                travelled => AfterFastTravel(gather, travelled)))
        {
            ShowGatherFailed();
            CampfireFastTravelContext.OnGatherAbandoned();

            return false;
        }

        _gather = gather;

        return true;
    }

    // the campfire fast travel is over. travelled is false when it never began in time (the command may only be late,
    // so nothing more is sent), when the game couldn't travel (no path, its
    // waypoint lookup failed) or when enemies interrupted it short of the campfire; the gather then fails and nobody
    // rests.
    private static void AfterFastTravel(Gather gather, bool travelled)
    {
        // another rest screen opened during the travel, or a reset
        if (_gather != gather)
        {
            return;
        }

        EndGather();

        // a fight or cutscene (an enemy saw the party on the way): the game's own screens explain it
        if (IsBusy())
        {
            return;
        }

        if (!travelled)
        {
            ShowGatherFailed();
            CampfireFastTravelContext.OnGatherAbandoned();

            return;
        }

        // every rest rule again, now with the 10 cells too: the game's Start doesn't check them itself (only with a
        // gamepad), the rest screen's button did, before the gather. Before a wait, all but the once-a-day rule,
        // which the wait is for; the game checks every rule again once the wait is over.
        if (!CheckLongRest(out var failure, gather.BeforeWait))
        {
            if (failure == PartyTooSpreadOut)
            {
                // the game's own range rule failed: named like the gather's check below, with the general message
                // only if that check finds nobody
                ShowOutOfRange(gather.User);
            }
            else
            {
                // something else changed during the gather (an enemy came near, ...), shown as the game shows it
                Gui.GuiService.ShowAlert(failure, Gui.ColorFailure, 2.5f);
            }

            CampfireFastTravelContext.OnGatherAbandoned();

            return;
        }

        // the game's rule above leaves out dead, dying and unconscious characters, and the game's arrival places only
        // as many characters as it finds free cells for (GameLocationMapManager.SetupPartyOnDestination teleports
        // foundPositions.Count of them; ComputeFormationPlacementPositions can come back short on a cramped point), so
        // a downed character could still be left behind: the gather's own range check, with them, decides (read in
        // the game's code, not seen in a game). The message names who is out of range.
        if (CampfireFastTravelContext.NeedsTravel(gather.User))
        {
            ShowOutOfRange(gather.User);
            CampfireFastTravelContext.OnGatherAbandoned();

            return;
        }

        // the party is at the campfire and every rule passes: the rest screen's Start, or the wait and rest
        gather.Arrived();
    }

    private static bool IsBusy()
    {
        return Gui.Battle != null ||
               ServiceRepository.GetService<INarrativeDirectionService>() is { IsSequenceInProgress: true };
    }

    private static void EndGather()
    {
        _gather = null;
    }

    private static void ShowGatherFailed()
    {
        Gui.GuiService.ShowAlert(Gui.Localize("Message/&CampfireGatherFailed"), Gui.ColorFailure, 5f);
    }

    // after the travel the party is still out of the long rest range: "<name> is out of range for a long rest.", or
    // the general failure when no single character can be named
    private static void ShowOutOfRange(GameLocationCharacter user)
    {
        if (CampfireFastTravelContext.DescribeOutOfRange(user) is { } outOfRange)
        {
            Gui.GuiService.ShowAlert(outOfRange, Gui.ColorFailure, 5f);
        }
        else
        {
            ShowGatherFailed();
        }
    }

    // the party's leader (the first party member, from whom the game routes a fast travel) can lead it; when they are
    // dead, dying or unconscious the player is told who and in which state, e.g. "Kushiel is dead. Unable to fast
    // travel.", and nobody travels or rests
    internal static bool LeaderCanTravel()
    {
        var message = CampfireFastTravelContext.DescribeDownedLeader();

        if (message == null)
        {
            return true;
        }

        Gui.GuiService.ShowAlert(message, Gui.ColorFailure, 5f);

        return false;
    }

    // leaving or joining a room, or loading: forgets the campfire and any gather
    internal static void Reset()
    {
        _campfire = null;
        _waitCampfire = null;
        _gather = null;
        CampfireFastTravelContext.Reset();
    }

    private sealed class Campfire
    {
        // the campfire rests at once, without the rest screen's checks or food
        internal bool Forced;

        // the character who used the campfire; the party gathers at the campfire's point next to them
        internal GameLocationCharacter User;
    }

    private sealed class Gather
    {
        // what runs once the party arrived and the rules pass: the rest screen's Start, or the wait and rest
        internal System.Action Arrived;

        // the gather comes before a wait, so its last check leaves out the once-a-day rule
        internal bool BeforeWait;

        // the character who used the campfire: the whole party must be in range of them after the travel
        internal GameLocationCharacter User;
    }
}
