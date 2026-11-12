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
//    Requests written during the same gadget update are ONE skip to ONE destination, worked out in the order
//    above. With Clock_SkipToDay the hour is an hour OF THAT DAY: Clock_SkipToDay 5 with Clock_SkipToHour 9 ends
//    on day 5 at 09:00, and with Clock_SkipToHour 24 on day 5 at 00:00 (24 is midnight, and day 5 has one).
//    Each has an optional String status variable, e.g. Clock_SkipHours_Status: Running, Succeeded, Failed or
//    Cancelled. A skip can't be cancelled once written.
//
// How the skip runs: the game's own "skip time" gadget function never moves the clock more than 23 hours at once,
// and one clock update signals at most one dawn and one new day however long it is. A skip of several days in one
// update (what the first version did) therefore gave one dawn and one journal entry in all. So a skip here
// advances in steps of a few hours, each an ordinary clock update, a few steps per gadget update: every dawn, new
// day, journal entry and hour passes as it would have, and a long skip can't freeze the game.
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
    private static string _lastError = string.Empty;

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
            _skipRequested |= DungeonMakerCommandsContext.IntValueOf(variables, name) > 0;
        }
    }

    internal static void OnLocationUnloading()
    {
        if (_job != null)
        {
            EndJob(DungeonMakerCommandsContext.StatusCancelled);
            Main.Info("DM clock: the skip was cut short because the location was left.");
        }

        _skipRequested = false;
    }

    // from the commands context: a variable it has no use for was written
    internal static void OnVariableChanged(string name)
    {
        if (Array.IndexOf(SkipVariables, name) >= 0)
        {
            _skipRequested = true;
        }
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

        if (_job == null && _skipRequested)
        {
            _skipRequested = false;
            _job = BuildJob(time);
        }

        // the clock is paused: UpdateTime would do nothing, so the skip waits for it
        if (_job == null || time.Paused)
        {
            return;
        }

        for (var step = 0; step < StepsPerTick && _job.Remaining > 0; step++)
        {
            var seconds = (int)Math.Min(_job.Remaining, StepSeconds);

            time.UpdateTime(seconds);
            _job.Remaining -= seconds;
        }

        if (_job.Remaining > 0)
        {
            return;
        }

        Main.Info($"DM clock: skipped {_job.Description}.");
        EndJob(DungeonMakerCommandsContext.StatusSucceeded);
    }

    #endregion

    private static void EndJob(string status)
    {
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

        if (hours > 0)
        {
            hours = Math.Min(hours, MaxSkipHours);
            at += (long)hours * SecondsPerHour;
            job.Variables.Add(SkipHoursVariable);
            parts.Add($"{hours} hour(s)");
        }

        var days = Take(variables, SkipDaysVariable);

        if (days > 0)
        {
            days = Math.Min(days, MaxSkipDays);
            at += (long)days * SecondsPerDay;
            job.Variables.Add(SkipDaysVariable);
            parts.Add($"{days} day(s)");
        }

        var toDay = Take(variables, SkipToDayVariable);
        var dayGiven = false;

        if (toDay > 0)
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

    private static void Reject(string name, string reason)
    {
        DungeonMakerCommandsContext.WriteStatus(name, DungeonMakerCommandsContext.StatusFailed);
        Main.Info($"DM clock: {reason}.");
    }

    private sealed class SkipJob
    {
        internal readonly List<string> Variables = [];
        internal string Description;
        internal long Remaining;
    }
}
