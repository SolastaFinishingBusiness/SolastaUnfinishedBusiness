using System;
using System.Collections.Generic;

namespace SolastaUnfinishedBusiness.Models;

// Dungeon Maker campaign clock (issue #42, adapted from BlackguardRogue's Dungeon Maker tools): lets a campaign
// READ and CONTROL the in-game clock through ordinary campaign variables. The author creates Int variables with
// exactly these names (case-sensitive); a variable that doesn't exist is ignored.
//
//  READ-OUTS (the mod keeps them up to date)
//    Clock_Hour         current hour, 0-23
//    Clock_Minute       current minute, 0-59
//    Clock_Day          the day number the in-game journal shows ("Day 1" on the first day): GameTime.CampaignDay
//    Clock_DayOfMonth   the calendar day as the game stores it: 0-29
//    Clock_Month        the month as stored: 0-11
//    Clock_Year         the year
//    Clock_CampaignDay  days since the campaign began, from the campaign definition's start date (Day 1 = the
//                       starting day). It differs from Clock_Day only in a save whose stored start date is wrong.
//                       Clock_SkipToDay is numbered like this variable.
//
//  COUNTERS (a Bool or Int switch, and an Int the mod adds to while the switch is on; reset it yourself)
//    Clock_CountMinutes / Clock_MinutesElapsed   +1 for every clock minute passed
//    Clock_CountHours   / Clock_HoursElapsed     +1 for every clock hour boundary passed
//    Clock_CountDays    / Clock_DaysElapsed      +1 for every midnight passed
//    however the time passed (walking around, rests, travel, battle rounds, a skip).
//
//  SKIP TIME (write the variable; the mod sets it back to 0 and advances the clock)
//    Clock_SkipHours    N (1-168): N hours on
//    Clock_SkipDays     N (1-180): N whole days on, keeping the time of day
//    Clock_SkipToDay    X: to 00:00 of campaign day X (numbered like Clock_CampaignDay), which must be 1 to 180
//                       days ahead
//    Clock_SkipToHour   1-23: to that hour (today, or tomorrow if it has passed); 24: to the next midnight
//    A value outside its range is not cut down to fit: that request fails (status Failed, a line in the log) and
//    the others written with it still run. A Clock_SkipToHour written with a Clock_SkipToDay that fails fails
//    too, as the two name one moment together.
//    Requests written during the same gadget update are ONE skip to ONE destination, worked out in the order
//    above. With Clock_SkipToDay the hour is an hour OF THAT DAY: Clock_SkipToDay 5 with Clock_SkipToHour 9 ends
//    on day 5 at 09:00, and with Clock_SkipToHour 24 on day 5 at 00:00 (24 is midnight, and day 5 has one).
//    Each has an optional String status variable, e.g. Clock_SkipHours_Status: Running, Succeeded, Failed or
//    Cancelled. A skip can't be cancelled once written; only a battle ends one (below).
//    Clock_SkipRemainingSeconds is the mod's own variable, made when a skip starts: what is left of the skip that
//    is running. It is saved with the game, so a game saved in the middle of a long skip finishes the skip when it
//    is loaded, and a skip running when the party leaves the location finishes in the next one. Authors can read
//    it, but should not write it. Clock_SkipTargetMinute, the mod's too, holds the clock minute a skip to a given
//    hour or day ends on (0 for a skip by a number of hours or days), so time that passes between two locations,
//    such as travel, is not added on top of it when it finishes there.
//    NEVER IN BATTLE (user direction, 2026-11-13: "Skips should be like long rest waits or learning spells or
//    something like that. They should never occur in battle"): a request written while a battle is in progress
//    fails, and a battle that starts while a skip runs ends it, the way an encounter ends a rest, with the status
//    Cancelled. The clock only moves by rounds in a battle; days passing in one would end every effect and
//    condition there at once.
//
// How the skip runs: the game's own "skip time" gadget function never moves the clock more than 23 hours at once,
// and one clock update signals at most one dawn and one new day however long it is. A skip of several days in one
// update (what the first version did) therefore gave one dawn and one journal entry in all. So a skip here
// advances in steps of four hours, each an ordinary clock update, six steps per gadget update: every dawn, new day
// and journal entry passes as it would have, the counters count every minute, hour and day, and a long skip
// can't freeze the game.
// A skip is ONE WAIT for an object's timers, its "every N hours" (GameGadgetListenerHourUpdate) and its "after a
// time" (GameGadgetListenerElapsedTime) listeners, as a long rest or scribing a scroll is (issue #42's Clock Skip
// specification): they don't check themselves from the moment a skip is taken until it ends (HoldTimers), also
// across the game's own clock updates between the skip's batches, a save loaded in the middle of it and a change of
// location; the elapsed-time listener still adds up the seconds. When the skip ends, or a battle cuts it short,
// every timer checks itself once (CheckTimers), so a due timer has one activation, as after the game's own single
// clock update for a rest, and an hour timer's next interval counts from the arrival hour. (A two-hour timer
// started at 08:00, then an eight-hour skip: at 16:00 one activation is due, the next at 18:00.) The read-outs only
// show every fourth hour on the way (Clock_Hour 11, 15, 19, ... from 07:30), so an activator waiting for
// "Clock_Hour = 10" isn't set off by a skip passing 10:00, as the specification allows; it sees the arrival hour.
// A counter (Clock_HoursElapsed) or a test like "Clock_Hour >= 10" doesn't miss.
internal static class DungeonMakerClockContext
{
    private const string HourVariable = "Clock_Hour";
    private const string MinuteVariable = "Clock_Minute";
    private const string DayVariable = "Clock_Day";
    private const string DayOfMonthVariable = "Clock_DayOfMonth";
    private const string MonthVariable = "Clock_Month";
    private const string YearVariable = "Clock_Year";
    private const string CampaignDayVariable = "Clock_CampaignDay";
    private const string CountHoursVariable = "Clock_CountHours";
    private const string HoursElapsedVariable = "Clock_HoursElapsed";
    private const string CountDaysVariable = "Clock_CountDays";
    private const string DaysElapsedVariable = "Clock_DaysElapsed";
    private const string CountMinutesVariable = "Clock_CountMinutes";
    private const string MinutesElapsedVariable = "Clock_MinutesElapsed";
    private const string SkipHoursVariable = "Clock_SkipHours";
    private const string SkipDaysVariable = "Clock_SkipDays";
    private const string SkipToHourVariable = "Clock_SkipToHour";
    private const string SkipToDayVariable = "Clock_SkipToDay";
    private const string SkipRemainingVariable = "Clock_SkipRemainingSeconds";
    private const string SkipTargetVariable = "Clock_SkipTargetMinute";

    private const int MaxSkipHours = 24 * 7;
    private const int MaxSkipDays = 180;
    private const int MaxSkipToDayAhead = 180;
    private const int SecondsPerHour = 3600;
    private const int SecondsPerDay = 86400;

    // One step is one clock update. Four hours is well inside what the game does itself in one update (a long
    // rest is eight), and it can't pass two dawns or two midnights.
    private const int StepSeconds = 4 * SecondsPerHour;

    // six steps = one in-game day per gadget update; the longest skip (180 days) takes about three seconds
    private const int StepsPerTick = 6;

    private static readonly string[] SkipVariables =
        [SkipHoursVariable, SkipDaysVariable, SkipToDayVariable, SkipToHourVariable];

    private static SkipJob _job;
    private static bool _skipRequested;

    // a skip request was written while a battle was in progress: it fails when it is taken
    private static bool _requestedInBattle;

    private static string _lastError = string.Empty;

    // Objects' timers don't check themselves (GameGadgetListener_CheckTimer in DungeonMakerCommandsPatcher) while a
    // skip runs, or while one kept in Clock_SkipRemainingSeconds waits to go on: between a save's load (when each
    // timer's PostLoad checks it) and the location being ready, between two locations, or with the clock paused.
    // Checkpoint keeps that variable above 0 for as long as a skip lasts, and EndJob sets it to 0, then CheckTimers
    // makes the one check.
    // Only while the commands are switched on (Codex's review of d26e0f30): switched off, the skip is no longer
    // advanced (the commands' Tick returns at once), and holding the timers then would stop every hour and
    // elapsed-time object in the location for as long as the setting stays off. The skip stays where it was, kept
    // in _job or the variable, and goes on, with its timers held again, once the setting is back on.
    internal static bool HoldTimers =>
        Main.Settings.EnableDungeonMakerVariableCommands &&
        (_job != null ||
         (ServiceRepository.GetService<IGameVariableService>() is { } variables &&
          DungeonMakerCommandsContext.IntValueOf(variables, SkipRemainingVariable) > 0));

    private static bool BattleInProgress =>
        ServiceRepository.GetService<IGameLocationBattleService>() is { IsBattleInProgress: true };

    #region Hooks

    internal static void OnLocationReady()
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();
        var time = Gui.GameCampaign?.GameTime;

        if (variables == null || time?.TimeInfo == null)
        {
            return;
        }

        WriteReadouts(time);

        // a skip written outside a location, or kept in a save, starts now
        foreach (var name in SkipVariables)
        {
            _skipRequested |= DungeonMakerCommandsContext.IntValueOf(variables, name) != 0;
        }

        // A skip was running when this game was saved: its requests were already set back to 0 and the rest of it
        // was only in memory, so without this the remainder would be lost and its status would say Running for
        // good. The requests that belong to it are the ones whose status still says Running.
        var remaining = (long)DungeonMakerCommandsContext.IntValueOf(variables, SkipRemainingVariable);

        if (_job != null || remaining <= 0)
        {
            return;
        }

        // A skip to a given hour or day ends at that moment, not after a duration: whatever time passed since the
        // checkpoint (travel between two locations) counts towards it. A skip by a number of hours or days keeps
        // what was left of it.
        var target = DungeonMakerCommandsContext.IntValueOf(variables, SkipTargetVariable);

        if (target > 0)
        {
            remaining = Math.Max(0, ((long)target * 60) - Now(time.TimeInfo));
        }

        _job = new SkipJob
        {
            Remaining = remaining,
            TargetMinute = target,
            Description = "the rest of a skip from the save or the previous location"
        };

        foreach (var name in SkipVariables)
        {
            if (DungeonMakerCommandsContext.StringValueOf(
                    variables, name + DungeonMakerCommandsContext.StatusSuffix) ==
                DungeonMakerCommandsContext.StatusRunning)
            {
                _job.Variables.Add(name);
            }
        }

        Main.Info($"DM clock: resuming a skip from the save, {remaining} second(s) left.");
    }

    // The location is going away. A skip can't be cancelled once written, so a running one is not ended here: what
    // is left of it is already in Clock_SkipRemainingSeconds (Checkpoint runs after every batch of steps and right
    // after the skip is taken), and its requests still say Running, which is all OnLocationReady needs to take it
    // up in the next location. Only the copy in memory goes, as this may also be the game itself going away (a
    // load, the main menu); the next game's own variables then decide.
    internal static void OnLocationUnloading()
    {
        if (_job != null)
        {
            Main.Info(
                $"DM clock: the location was left during a skip; {_job.Remaining} second(s) are kept for the next one.");
            _job = null;
        }

        _skipRequested = false;
        _requestedInBattle = false;
    }

    // from the commands context: a variable it has no use for was written. Whether a battle is in progress is
    // noted now, at the write: the request is taken at the end of the gadget update, and the battle could have
    // ended by then.
    internal static void OnVariableChanged(string name)
    {
        if (Array.IndexOf(SkipVariables, name) < 0)
        {
            return;
        }

        _skipRequested = true;
        _requestedInBattle |= BattleInProgress;
    }

    // GameTime.UpdateTime, after it ran: the one function the game advances the clock with (walking around, rests,
    // travel, battle rounds, the game's own skip). minutesBefore is the clock in minutes before it ran. Nothing is
    // done unless the minute changed, which is once every few seconds of play, not every frame.
    internal static void OnTimeUpdated(GameTime time, int minutesBefore)
    {
        var info = time?.TimeInfo;

        if (info == null || !Main.Settings.EnableDungeonMakerVariableCommands)
        {
            return;
        }

        var minutesNow = info.ComputeMinutes();

        if (minutesNow == minutesBefore)
        {
            return;
        }

        try
        {
            var variables = ServiceRepository.GetService<IGameVariableService>();

            if (variables == null)
            {
                return;
            }

            // TimeInfo counts minutes, hours and days from the same origin, so whole hours and days before the
            // update follow from the minutes before it
            Count(variables, CountMinutesVariable, MinutesElapsedVariable, minutesNow - minutesBefore);
            Count(variables, CountHoursVariable, HoursElapsedVariable, info.ComputeHours() - (minutesBefore / 60));
            Count(variables, CountDaysVariable, DaysElapsedVariable, info.ComputeDays() - (minutesBefore / 1440));
            WriteReadouts(time);

            if (info.ComputeHours() != minutesBefore / 60)
            {
                DungeonMakerCommandsContext.NoteHourChanged();
            }
        }
        catch (Exception e)
        {
            // report a repeating error once, and keep working: one bad update must not switch the clock variables
            // off for the rest of the session
            var message = e.ToString();

            if (message != _lastError)
            {
                _lastError = message;
                Main.Error($"Dungeon Maker clock: {message}");
            }
        }
    }

    // from the commands context, after the game updated its gadgets
    internal static void Tick()
    {
        var time = Gui.GameCampaign?.GameTime;

        if (time?.TimeInfo == null)
        {
            return;
        }

        if (_job == null && _skipRequested && (_requestedInBattle || BattleInProgress))
        {
            // never in battle: written during one (or found in a save made during one), the requests fail
            _skipRequested = false;
            _requestedInBattle = false;
            RejectInBattle();
        }
        else if (_job == null && _skipRequested)
        {
            _skipRequested = false;
            _job = BuildJob(time);

            // The requests are now set back to 0 and marked Running, and the skip may not advance at once (the
            // clock can be paused): what is left is written down straight away, so a save made before the first
            // step can still resume it.
            if (_job != null)
            {
                Checkpoint();
            }
        }

        if (_job == null)
        {
            return;
        }

        // never in battle: a battle that starts during the skip ends it, as an encounter ends a rest (see the
        // header). The same on every computer, as the battle is.
        if (BattleInProgress)
        {
            Main.Info($"DM clock: the skip ({_job.Description}) ended early: a battle started.");

            // The skip stops at the time already reached, and that is where the wait ends for the timers too: once
            // the skip is ended (nothing held any more), the check held back is made, once. A timer that isn't due
            // stays as it is, as on any tick.
            EndJob(DungeonMakerCommandsContext.StatusCancelled);
            DungeonMakerCommandsContext.CheckTimers(time.TimeInfo.Hour);

            return;
        }

        // the clock is paused: UpdateTime would do nothing, so the skip waits for it
        if (time.Paused)
        {
            return;
        }

        for (var step = 0; step < StepsPerTick && _job.Remaining > 0; step++)
        {
            var seconds = (int)Math.Min(_job.Remaining, StepSeconds);

            // the objects' timers are held for the whole skip (HoldTimers)
            time.UpdateTime(seconds);
            _job.Remaining -= seconds;
        }

        Checkpoint();

        if (_job.Remaining > 0)
        {
            return;
        }

        Main.Info($"DM clock: skipped {_job.Description}.");
        EndJob(DungeonMakerCommandsContext.StatusSucceeded);

        // the one check of the objects' timers for the whole skip, now that nothing holds them
        DungeonMakerCommandsContext.CheckTimers(time.TimeInfo.Hour);
    }

    #endregion

    // what is left of the running skip goes into a campaign variable, so a save made now holds it (see
    // OnLocationReady)
    private static void Checkpoint()
    {
        DungeonMakerCommandsContext.EnsureInt(SkipRemainingVariable);
        DungeonMakerCommandsContext.EnsureInt(SkipTargetVariable);
        DungeonMakerCommandsContext.WriteInt(SkipRemainingVariable, (int)Math.Max(0, _job.Remaining));
        DungeonMakerCommandsContext.WriteInt(SkipTargetVariable, _job.TargetMinute);
    }

    private static void EndJob(string status)
    {
        // nothing is left to resume: the skip is done, or a battle ended it
        DungeonMakerCommandsContext.WriteInt(SkipRemainingVariable, 0);
        DungeonMakerCommandsContext.WriteInt(SkipTargetVariable, 0);

        foreach (var name in _job.Variables)
        {
            DungeonMakerCommandsContext.WriteStatus(name, status);
        }

        _job = null;
    }

    private static void Count(IGameVariableService variables, string switchName, string counterName, int crossed)
    {
        if (crossed > 0 && DungeonMakerCommandsContext.IsOn(variables, switchName) &&
            variables.TryFindVariable(counterName, out var counter) && counter is
            {
                Type: GameVariableDefinitions.Type.Int
            })
        {
            // through the variable's own setter, so the game's listeners see it; none of these is a command
            counter.IntValue += crossed;
        }
    }

    private static void WriteReadouts(GameTime time)
    {
        var info = time.TimeInfo;

        DungeonMakerCommandsContext.WriteInt(HourVariable, info.Hour);
        DungeonMakerCommandsContext.WriteInt(MinuteVariable, info.Minute);
        DungeonMakerCommandsContext.WriteInt(DayVariable, time.CampaignDay);
        DungeonMakerCommandsContext.WriteInt(DayOfMonthVariable, info.Day);
        DungeonMakerCommandsContext.WriteInt(MonthVariable, info.Month);
        DungeonMakerCommandsContext.WriteInt(YearVariable, info.Year);

        if (TryGetCampaignDay(info, out var campaignDay))
        {
            DungeonMakerCommandsContext.WriteInt(CampaignDayVariable, campaignDay);
        }
    }

    // Day 1 = the campaign's starting day, with the game's 360-day year and 30-day month
    private static bool TryGetCampaignDay(TimeInfo info, out int campaignDay)
    {
        var definition = Gui.GameCampaign?.campaignDefinition;

        campaignDay = definition == null
            ? 0
            : (360 * (info.Year - definition.StartYear)) + (30 * (info.Month - definition.StartMonth)) +
              (info.Day - definition.StartDay) + 1;

        return definition != null;
    }

    // Reads the skip requests that are set right now, sets them back to 0 and turns them into one distance in
    // seconds. They are applied in a fixed order to a clock position that only exists on paper ("at"), so two of
    // them never run as two skips. Returns null when nothing valid was asked.
    private static SkipJob BuildJob(GameTime time)
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();
        var info = time.TimeInfo;

        if (variables == null)
        {
            return null;
        }

        var job = new SkipJob();
        var parts = new List<string>();

        // seconds since today's midnight, moved along as each request is applied
        long at = (info.Hour * SecondsPerHour) + (info.Minute * 60) + info.Second;
        var start = at;

        var hours = Take(variables, SkipHoursVariable);

        if (hours != 0 && !InRange(SkipHoursVariable, hours, 1, MaxSkipHours))
        {
            hours = 0;
        }

        if (hours > 0)
        {
            at += (long)hours * SecondsPerHour;
            job.Variables.Add(SkipHoursVariable);
            parts.Add($"{hours} hour(s)");
        }

        var days = Take(variables, SkipDaysVariable);

        if (days != 0 && !InRange(SkipDaysVariable, days, 1, MaxSkipDays))
        {
            days = 0;
        }

        if (days > 0)
        {
            at += (long)days * SecondsPerDay;
            job.Variables.Add(SkipDaysVariable);
            parts.Add($"{days} day(s)");
        }

        var toDay = Take(variables, SkipToDayVariable);
        var dayGiven = false;

        if (toDay < 0)
        {
            Reject(SkipToDayVariable, $"skip to day {toDay} ignored: days are numbered from 1");
        }
        else if (toDay > 0)
        {
            // the campaign day the paper clock is on now
            var known = TryGetCampaignDay(info, out var today);
            var ahead = toDay - (today + (int)(at / SecondsPerDay));

            if (!known)
            {
                Reject(SkipToDayVariable, $"skip to day {toDay} ignored: the campaign start date is unavailable");
            }
            else if (ahead is < 1 or > MaxSkipToDayAhead)
            {
                Reject(SkipToDayVariable,
                    $"skip to day {toDay} ignored: it must be 1 to {MaxSkipToDayAhead} days ahead of day {toDay - ahead}");
            }
            else
            {
                // to 00:00 of that day
                at = (at / SecondsPerDay * SecondsPerDay) + ((long)ahead * SecondsPerDay);
                dayGiven = true;
                job.Variables.Add(SkipToDayVariable);
                parts.Add($"to day {toDay}");
            }
        }

        var toHour = Take(variables, SkipToHourVariable);

        if (toHour != 0 && toDay != 0 && !dayGiven)
        {
            // the hour belongs to the day that was rejected: on its own it would name another moment
            Reject(SkipToHourVariable, $"skip to hour {toHour} ignored: it was written with a day that was rejected");
            toHour = 0;
        }
        else if (toHour != 0 && !InRange(SkipToHourVariable, toHour, 1, 24))
        {
            toHour = 0;
        }

        if (toHour > 0)
        {
            var target = Math.Min(toHour, 24) % 24;

            if (dayGiven)
            {
                // a day was named: the hour is that day's, counted from its midnight, where the clock stands now
                at += (long)target * SecondsPerHour;
            }
            else
            {
                // the game's own "skip to hour" arithmetic (FunctorSkipTime): that hour today, or tomorrow when it
                // is not later than now; 24 is midnight, which is then always "tomorrow"
                var intoDay = at % SecondsPerDay;
                var hourAt = (int)(intoDay / SecondsPerHour);
                var hoursAhead = target > hourAt ? target - hourAt : 24 - hourAt + target;

                at += ((long)hoursAhead * SecondsPerHour) - (intoDay % SecondsPerHour);
            }

            job.Variables.Add(SkipToHourVariable);
            parts.Add($"to hour {target}");
        }

        job.Remaining = at - start;

        // A destination named by hour or day is a moment on the clock: kept as such, in whole minutes (it is
        // always on the hour), for a skip that finishes in another location (see OnLocationReady).
        if (dayGiven || toHour > 0)
        {
            job.TargetMinute = (int)((Now(info) + job.Remaining) / 60);
        }

        if (job.Remaining <= 0)
        {
            return null;
        }

        job.Description = string.Join(", ", parts);

        foreach (var name in job.Variables)
        {
            DungeonMakerCommandsContext.WriteStatus(name, DungeonMakerCommandsContext.StatusRunning);
        }

        return job;
    }

    // reads a request and sets it back to 0
    private static int Take(IGameVariableService variables, string name)
    {
        var value = DungeonMakerCommandsContext.IntValueOf(variables, name);

        if (value != 0)
        {
            DungeonMakerCommandsContext.WriteInt(name, 0);
        }

        return value;
    }

    // a request outside its range fails, rather than being cut down to a skip the author didn't ask for
    private static bool InRange(string name, int value, int min, int max)
    {
        if (value >= min && value <= max)
        {
            return true;
        }

        Reject(name, $"{name} {value} ignored: it must be {min} to {max}");

        return false;
    }

    private static void Reject(string name, string reason)
    {
        DungeonMakerCommandsContext.WriteStatus(name, DungeonMakerCommandsContext.StatusFailed);
        Main.Info($"DM clock: {reason}.");
    }

    // the clock in seconds from TimeInfo's origin (its minutes fit an int; its seconds would not)
    private static long Now(TimeInfo info)
    {
        return ((long)info.ComputeMinutes() * 60) + info.Second;
    }

    // every skip request written is taken and fails: a skip is never made during a battle
    private static void RejectInBattle()
    {
        var variables = ServiceRepository.GetService<IGameVariableService>();

        if (variables == null)
        {
            return;
        }

        foreach (var name in SkipVariables)
        {
            var value = Take(variables, name);

            if (value != 0)
            {
                Reject(name, $"{name} {value} ignored: time can't be skipped during a battle");
            }
        }
    }

    private sealed class SkipJob
    {
        internal readonly List<string> Variables = [];
        internal string Description;
        internal long Remaining;

        // the clock minute a skip to a given hour or day ends on; 0 for a skip by a duration
        internal int TargetMinute;
    }
}
