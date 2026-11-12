using System;
using System.Reflection;
using HarmonyLib;
using JetBrains.Annotations;

namespace SolastaUnfinishedBusiness.Patches;

// Lets a Dungeon Maker campaign READ and CONTROL the in-game clock through ordinary campaign variables.
//
// The campaign author creates Int variables in the Dungeon Maker variables list using EXACTLY these names
// (case-sensitive). Any variable that does not exist is simply ignored, so campaigns that do not define them,
// and players who do not run this mod, are unaffected. Players need the mod for the features to work.
//
//  READ-OUTS (the mod keeps them up to date):
//    Clock_Hour    current hour, 0-23
//    Clock_Minute  current minute, 0-59
//    Clock_Day     the day number shown in the in-game JOURNAL ("Day 1" on the first day, then 2, 3, ...). It is
//                  the game's own GameTime.CampaignDay, the value the journal's new-day entries are created with.
//    Clock_DayOfMonth  the calendar day of the month AS THE GAME STORES IT: it wraps back to 0 after 29 (30-day
//                  months), so it does not start at 1. This is what Clock_Day used to hold.
//    Clock_Month   month as stored: wraps back to 0 after 11
//    Clock_Year    year
//    Clock_CampaignDay  days since the campaign began, computed from the campaign definition's start date
//                  (Day 1 = the starting day). In a normal campaign it equals Clock_Day. It differs only in a save
//                  whose stored start date is wrong (the game's own count, and so the journal and Clock_Day, would
//                  then start at the day of the month instead of 1); this one is not affected. Clock_SkipToDay is
//                  numbered like this variable.
//    -> use the usual "variable changed" activators / quest validators to react (e.g. Clock_Hour reaches 12).
//
//  HOUR COUNTER:
//    Clock_CountHours   Bool or Int. Set to true / non-zero to start counting, false / 0 to stop.
//    Clock_HoursElapsed Int. While counting, +1 for every clock-hour boundary crossed (10:59 -> 11:00 adds 1),
//                       however the time passed (rests, travel, battles, skip). Set it to 0 yourself to reset.
//
//  MINUTE COUNTER (same idea, counted in clock-minute boundaries):
//    Clock_CountMinutes   Bool or Int. true / non-zero = counting, false / 0 = stopped.
//    Clock_MinutesElapsed Int. While counting, +1 for every clock-minute boundary crossed (10:30:59 -> 10:31:00
//                         adds 1), however the time passed. Set it to 0 yourself to reset.
//
//  DAY COUNTER (counted in midnight crossings):
//    Clock_CountDays    Bool or Int. true / non-zero = counting, false / 0 = stopped.
//    Clock_DaysElapsed  Int. While counting, +1 every time the clock passes midnight (23:59 -> 00:00 adds 1),
//                       however the time passed. Set it to 0 yourself to reset.
//
//  SKIP TIME (set the variable; the mod performs the skip and sets it back to 0):
//    Clock_SkipHours    set to N (1-168): advance N hours.
//    Clock_SkipDays     set to N (1-180): advance N whole days (N x 24 hours), keeping the time of day. Applied after
//                       Clock_SkipHours if both are set in the same frame.
//    Clock_SkipToHour   set to 1-23: advance to that hour (today, or tomorrow if already past); 24 = midnight.
//    Clock_SkipToDay    set to X: advance to 00:00 on campaign day X, numbered like Clock_CampaignDay
//                       (Day 1 = the starting day). X must be in the future and at most
//                       180 days ahead, otherwise the request is ignored (and logged). Can be combined with
//                       Clock_SkipToHour in the same frame (day is applied first, then the hour).
//
// How it works: GameTime.UpdateTime is the single function the game uses to advance the clock (rests, travel,
// battle rounds, the game's own skip function). We remember the clock minute before it runs and compare after.
// Direct "set time" jumps do not go through it and are not counted (read-outs still refresh on the next tick).
internal static class GameClockBridge
{
    internal const string HourVariable = "Clock_Hour";
    internal const string MinuteVariable = "Clock_Minute";
    internal const string DayVariable = "Clock_Day";
    internal const string DayOfMonthVariable = "Clock_DayOfMonth";
    internal const string MonthVariable = "Clock_Month";
    internal const string YearVariable = "Clock_Year";
    internal const string CampaignDayVariable = "Clock_CampaignDay";
    internal const string CountHoursVariable = "Clock_CountHours";
    internal const string HoursElapsedVariable = "Clock_HoursElapsed";
    internal const string CountDaysVariable = "Clock_CountDays";
    internal const string DaysElapsedVariable = "Clock_DaysElapsed";
    internal const string CountMinutesVariable = "Clock_CountMinutes";
    internal const string MinutesElapsedVariable = "Clock_MinutesElapsed";
    internal const string SkipHoursVariable = "Clock_SkipHours";
    internal const string SkipDaysVariable = "Clock_SkipDays";
    internal const string SkipToHourVariable = "Clock_SkipToHour";
    internal const string SkipToDayVariable = "Clock_SkipToDay";

    private const int MaxSkipHours = 24 * 7;
    private const int MaxSkipDays = 180; // keeps the float seconds value exact (180 x 86400 < 2^24)
    private const int MaxSkipToDayAhead = 180; // keeps the float seconds value exact

    private static bool _busy;
    private static bool _failed;

    private static bool TryGetInt(IGameVariableService service, string name, out GameVariable variable)
    {
        return service.TryFindVariable(name, out variable) &&
               variable != null &&
               variable.Type == GameVariableDefinitions.Type.Int;
    }

    private static void SetInt(IGameVariableService service, string name, int value)
    {
        if (TryGetInt(service, name, out var variable) && variable.IntValue != value)
        {
            variable.IntValue = value;
        }
    }

    private static bool IsCounting(IGameVariableService service, string flagName)
    {
        if (!service.TryFindVariable(flagName, out var variable) || variable == null)
        {
            return false;
        }

        return variable.Type switch
        {
            GameVariableDefinitions.Type.Bool => variable.BoolValue,
            GameVariableDefinitions.Type.Int => variable.IntValue != 0,
            _ => false
        };
    }

    // same arithmetic as the game's own FunctorSkipTime ("skip to hour")
    private static float SecondsUntilHour(TimeInfo info, int targetHour)
    {
        var secondsIntoHour = info.Minute * 60 + info.Second;
        var hoursAhead = targetHour > info.Hour ? targetHour - info.Hour : 24 - info.Hour + targetHour;

        return hoursAhead * 3600f - secondsIntoHour;
    }

    private static readonly FieldInfo CampaignDefinitionField =
        AccessTools.Field(typeof(GameCampaign), "campaignDefinition");

    // Day 1 = the campaign's starting day, using the same 360-day-year / 30-day-month arithmetic as the game
    private static bool TryGetCampaignDay(GameTime time, out int campaignDay)
    {
        campaignDay = 0;

        var campaign = ServiceRepository.GetService<IGameService>()?.Game?.GameCampaign;
        var definition = campaign == null ? null : CampaignDefinitionField?.GetValue(campaign) as CampaignDefinition;
        var info = time.TimeInfo;

        if (definition == null || info == null)
        {
            return false;
        }

        campaignDay = 360 * (info.Year - definition.StartYear) +
                      30 * (info.Month - definition.StartMonth) +
                      (info.Day - definition.StartDay) + 1;

        return true;
    }

    private static int SecondsIntoDay(TimeInfo info)
    {
        return info.Hour * 3600 + info.Minute * 60 + info.Second;
    }

    internal static void Process(GameTime time, int minutesBefore)
    {
        // _busy: our own skips call UpdateTime again; ignore that nested call
        if (_busy || _failed)
        {
            return;
        }

        var service = ServiceRepository.GetService<IGameVariableService>();

        if (service == null || time?.TimeInfo == null)
        {
            return;
        }

        try
        {
            _busy = true;

            // 1. skip requests made by the campaign (left pending while the game clock is paused)
            if (!time.Paused)
            {
                if (TryGetInt(service, SkipHoursVariable, out var skipHours) && skipHours.IntValue > 0)
                {
                    var hours = Math.Min(skipHours.IntValue, MaxSkipHours);

                    skipHours.IntValue = 0;
                    time.UpdateTime(hours * 3600f);
                    Main.Info($"CLOCK skipped {hours} hour(s).");
                }

                if (TryGetInt(service, SkipDaysVariable, out var skipDays) && skipDays.IntValue > 0)
                {
                    var days = Math.Min(skipDays.IntValue, MaxSkipDays);

                    skipDays.IntValue = 0;
                    time.UpdateTime(days * 86400f);
                    Main.Info($"CLOCK skipped {days} day(s).");
                }

                if (TryGetInt(service, SkipToDayVariable, out var skipToDay) && skipToDay.IntValue > 0)
                {
                    var targetDay = skipToDay.IntValue;

                    skipToDay.IntValue = 0;

                    if (!TryGetCampaignDay(time, out var currentDay))
                    {
                        Main.Info($"CLOCK skip to day {targetDay} ignored: the campaign start date is unavailable.");
                    }
                    else if (targetDay - currentDay < 1 || targetDay - currentDay > MaxSkipToDayAhead)
                    {
                        Main.Info(
                            $"CLOCK skip to day {targetDay} ignored: today is day {currentDay} (allowed 1-{MaxSkipToDayAhead} days ahead).");
                    }
                    else
                    {
                        time.UpdateTime((targetDay - currentDay) * 86400f - SecondsIntoDay(time.TimeInfo));
                        Main.Info($"CLOCK skipped to day {targetDay}.");
                    }
                }

                if (TryGetInt(service, SkipToHourVariable, out var skipToHour) && skipToHour.IntValue > 0)
                {
                    var target = Math.Min(skipToHour.IntValue, 24) % 24;

                    skipToHour.IntValue = 0;
                    time.UpdateTime(SecondsUntilHour(time.TimeInfo, target));
                    Main.Info($"CLOCK skipped to hour {target}.");
                }
            }

            var info = time.TimeInfo;

            // 2. hour, minute and day counters (include any skip performed above).
            // ComputeMinutes = minute + 60 * ComputeHours, so the hour before the tick is minutesBefore / 60.
            var hoursCrossed = info.ComputeHours() - minutesBefore / 60;
            var minutesCrossed = info.ComputeMinutes() - minutesBefore;
            // ComputeHours = hour + 24 * ComputeDays, so the day before the tick is minutesBefore / 1440
            var daysCrossed = info.ComputeDays() - minutesBefore / (60 * 24);

            if (hoursCrossed > 0 && IsCounting(service, CountHoursVariable) &&
                TryGetInt(service, HoursElapsedVariable, out var hoursElapsed))
            {
                hoursElapsed.IntValue += hoursCrossed;
            }

            if (daysCrossed > 0 && IsCounting(service, CountDaysVariable) &&
                TryGetInt(service, DaysElapsedVariable, out var daysElapsed))
            {
                daysElapsed.IntValue += daysCrossed;
            }

            if (minutesCrossed > 0 && IsCounting(service, CountMinutesVariable) &&
                TryGetInt(service, MinutesElapsedVariable, out var minutesElapsed))
            {
                minutesElapsed.IntValue += minutesCrossed;
            }

            // 3. read-outs
            SetInt(service, HourVariable, info.Hour);
            SetInt(service, MinuteVariable, info.Minute);
            SetInt(service, DayVariable, time.CampaignDay);
            SetInt(service, DayOfMonthVariable, info.Day);
            SetInt(service, MonthVariable, info.Month);
            SetInt(service, YearVariable, info.Year);
            if (TryGetCampaignDay(time, out var campaignDay))
            {
                SetInt(service, CampaignDayVariable, campaignDay);
            }
        }
        catch (Exception e)
        {
            // runs every frame: report once, then switch itself off rather than spam or disturb the game
            _failed = true;
            Main.Error(e);
        }
        finally
        {
            _busy = false;
        }
    }
}

[UsedImplicitly]
[HarmonyPatch(typeof(GameTime), nameof(GameTime.UpdateTime))]
public static class GameClockVariablesPatcher
{
    [UsedImplicitly]
    public static void Prefix(GameTime __instance, out int __state)
    {
        __state = __instance.TimeInfo?.ComputeMinutes() ?? 0;
    }

    [UsedImplicitly]
    public static void Postfix(GameTime __instance, int __state)
    {
        GameClockBridge.Process(__instance, __state);
    }
}
