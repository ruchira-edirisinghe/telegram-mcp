using TelegramMCPTray.Core;
using Xunit;

namespace TelegramMCPTray.Tests;

/// <summary>The Away page words and checks (same words as the daemon's src/shared/away.ts).</summary>
public class AwayTests
{
    static UiAway A(bool enabled = true, bool active = false) =>
        new(enabled, "18:00", "09:00", "m", 30, active, null, "p");

    static UiAwaySkip Skip(string json, long? id) => new(System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(), id);

    [Fact]
    public void Skip_list_is_read_and_switched_one_chat_at_a_time()
    {
        var a = A() with { Skip = [Skip("\"pahan\"", 5216006911), Skip("491741918", 491741918), Skip("\"@gone\"", null)] };
        Assert.True(Away.Skips(a, 5216006911));
        Assert.False(Away.Skips(a, 42));
        Assert.False(Away.Skips(A(), 5216006911)); // an older daemon: no list

        var off = Away.SkipWith(a, 5216006911, "pahan", skip: false);
        Assert.Equal(["491741918", "\"@gone\""], off.Select(n => n!.ToJsonString()));
        var on = Away.SkipWith(a, 42, "leo", skip: true);
        Assert.Equal(["\"pahan\"", "491741918", "\"@gone\"", "\"leo\""], on.Select(n => n!.ToJsonString()));
        // Switching on a chat already listed by id keeps one entry for it, by nickname.
        Assert.Equal(["\"pahan\"", "\"@gone\"", "\"luigi\""],
            Away.SkipWith(a, 491741918, "luigi", skip: true).Select(n => n!.ToJsonString()));
    }

    [Fact]
    public void Status_says_off_away_now_or_when_it_starts()
    {
        Assert.Equal("Off", Away.Status(A(enabled: false)));
        Assert.Equal("Away now, until 09:00", Away.Status(A(active: true)));
        Assert.Equal("Starts at 18:00", Away.Status(A()));
    }

    [Fact]
    public void Badge_shows_only_while_away()
    {
        Assert.Equal("Away until 09:00", Away.Badge(A(active: true)));
        Assert.Null(Away.Badge(A()));
        Assert.Null(Away.Badge(A(enabled: false)));
        Assert.Null(Away.Badge(null));
    }

    [Theory]
    [InlineData("18:00", true)]
    [InlineData("00:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("9:00", false)]
    [InlineData("18:60", false)]
    [InlineData("18:00\n", false)]
    [InlineData("", false)]
    public void IsTime_is_24_hour_hh_mm(string s, bool expected) => Assert.Equal(expected, Away.IsTime(s));

    [Fact]
    public void Check_returns_the_daemons_words()
    {
        Assert.Null(Away.Check("18:00", "09:00", "hi"));
        Assert.Equal("from must be a time like 18:00", Away.Check("6pm", "09:00", "hi"));
        Assert.Equal("until must be a time like 18:00", Away.Check("18:00", "9", "hi"));
        Assert.Equal("From and until can't be the same time.", Away.Check("18:00", "18:00", "hi"));
        Assert.Equal("The message can't be empty.", Away.Check("18:00", "09:00", "  "));
        Assert.Null(Away.Check("18:00", "09:00", new string('x', 1000)));
        Assert.Equal("The message is too long (1000 characters at most).", Away.Check("18:00", "09:00", new string('x', 1001)));
        Assert.Null(Away.Check("18:00", "09:00", "hi", "0"));
        Assert.Equal("quietMinutes must be a whole number from 0 to 1440", Away.Check("18:00", "09:00", "hi", "1441"));
        Assert.Equal("quietMinutes must be a whole number from 0 to 1440", Away.Check("18:00", "09:00", "hi", ""));
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData(" 45 ", 45)]
    [InlineData("0", 0)]
    [InlineData("1440", 1440)]
    [InlineData("1441", null)]
    [InlineData("-5", null)]
    [InlineData("+5", null)]
    [InlineData("1.5", null)]
    [InlineData("1e3", null)]
    [InlineData("", null)]
    [InlineData("half an hour", null)]
    public void Quiet_is_a_whole_number_of_minutes_up_to_a_day(string s, int? expected) => Assert.Equal(expected, Away.Quiet(s));

    [Fact]
    public void Preview_fills_in_the_first_name_and_adds_the_footer_once()
    {
        const string foot = "\n\n-- bot";
        Assert.Equal("Hi Chamodh!" + foot, Away.Preview("Hi {name}!  ", "Chamodh Perera", foot));
        Assert.Equal("Hi {name}!" + foot, Away.Preview("Hi {name}!", null, foot));
        Assert.Equal("Hi X" + foot, Away.Preview("Hi X" + foot, "A", foot));
        Assert.Equal("Hi A A", Away.Preview("Hi {name} {name}", "A B", "  "));
    }

    // ---- schedules ---------------------------------------------------------------------------

    static UiAwaySchedule S(string id = "day", string from = "08:00", string until = "21:00", int delay = 30, bool enabled = true,
        int[]? days = null, string? start = null, string? end = null, bool active = false) =>
        new(id, id == "day" ? "Daytime" : "Night", enabled, from, until, days ?? [0, 1, 2, 3, 4, 5, 6], start, end, delay, 60, "m", active, "p");

    static UiAway WithSchedules(bool enabled, string? current, params UiAwaySchedule[] list) =>
        new(enabled, "08:00", "21:00", "m", 30, current is not null, DateTimeOffset.Parse("2026-11-03T15:30:00Z"), "p", current, null, 0, list);

    [Fact]
    public void Status_and_badge_name_the_schedule_replying_now()
    {
        var day = S(active: true);
        var night = S("night", "21:00", "08:00", 0);
        Assert.Equal("Daytime until 21:00, waits 30 s for you", Away.Status(WithSchedules(true, "day", day, night)));
        Assert.Equal("Daytime until 21:00", Away.Badge(WithSchedules(true, "day", day, night)));
        Assert.Equal("Off", Away.Status(WithSchedules(false, null, day, night)));
        Assert.Equal("On, but every schedule is switched off", Away.Status(WithSchedules(true, null, S(enabled: false))));
        Assert.StartsWith("Next at ", Away.Status(WithSchedules(true, null, day)));
        Assert.Null(Away.Badge(WithSchedules(true, null, day)));
    }

    [Fact]
    public void CheckSchedule_catches_what_the_daemon_would_refuse()
    {
        int[] week = [1, 2, 3, 4, 5];
        Assert.Null(Away.CheckSchedule("Lunch", "12:00", "13:00", week, null, null, "brb"));
        Assert.Equal("Give it a name, like Lunch or Holiday.", Away.CheckSchedule(" ", "12:00", "13:00", week, null, null, "brb"));
        Assert.Equal("It can't start and end at the same time.", Away.CheckSchedule("L", "12:00", "12:00", week, null, null, "brb"));
        Assert.Equal("Pick at least one day.", Away.CheckSchedule("L", "12:00", "13:00", [], null, null, "brb"));
        Assert.Equal("The first date must be on or before the last.",
            Away.CheckSchedule("L", "12:00", "13:00", week, new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 1), "brb"));
        Assert.Equal("The message can't be empty.", Away.CheckSchedule("L", "12:00", "13:00", week, null, null, " "));
        Assert.Equal("The name is 40 characters at most.", Away.CheckSchedule(new string('n', 41), "12:00", "13:00", week, null, null, "m"));
    }

    [Fact]
    public void Words_for_days_waits_repeats_and_dates()
    {
        Assert.Equal("Every day", Away.DaysText([0, 1, 2, 3, 4, 5, 6]));
        Assert.Equal("Weekdays", Away.DaysText([5, 4, 3, 2, 1]));
        Assert.Equal("Weekends", Away.DaysText([6, 0]));
        Assert.Equal("Mon, Wed, Sun", Away.DaysText([0, 1, 3]));
        Assert.Equal("replies at once", Away.DelayText(0));
        Assert.Equal("waits 30 s for you", Away.DelayText(30));
        Assert.Equal("waits 2 min for you", Away.DelayText(120));
        Assert.Equal(["At once", "30 s", "1 min", "2 min", "5 min"], Away.Delays.Select(Away.DelayChip));
        Assert.Equal(["Once", "Every 1 h", "Every 3 h"], Away.Repeats.Select(Away.RepeatChip));
        Assert.Equal("A chat can get another reply after 1 hour.", Away.RepeatText(60));
        Assert.Null(Away.DatesText(null, null));
        Assert.Equal("1 Dec – 24 Dec 2026", Away.DatesText(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 24)));
        Assert.Equal("On 1 Dec 2026", Away.DatesText(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 1)));
        Assert.Equal("From 1 Dec 2026", Away.DatesText(new DateOnly(2026, 12, 1), null));
        Assert.Equal(new DateOnly(2026, 12, 1), Away.ParseDate("2026-12-01"));
        Assert.Null(Away.ParseDate("1 Dec"));
        Assert.Equal("2026-12-01", Away.FormatDate(new DateOnly(2026, 12, 1)));
        Assert.True(Away.IsNight("21:00", "08:00"));
        Assert.True(Away.IsNight("19:00", "23:00"));
        Assert.False(Away.IsNight("08:00", "21:00"));
    }

    [Fact]
    public void TimeOptions_are_quarter_hours_plus_the_saved_time()
    {
        var all = Away.TimeOptions();
        Assert.Equal(96, all.Count);
        Assert.Equal("00:00", all[0]);
        Assert.Equal("23:45", all[^1]);
        var odd = Away.TimeOptions("08:10");
        Assert.Equal(97, odd.Count);
        Assert.Equal("08:10", odd[odd.ToList().IndexOf("08:00") + 1]);
        Assert.Equal(96, Away.TimeOptions("nonsense").Count);
    }

    [Fact]
    public void NewId_and_templates()
    {
        Assert.Equal("s1", Away.NewId(["day", "night"]));
        Assert.Equal("s3", Away.NewId(["s1", "s2"]));
        var t = Away.Templates(new DateOnly(2026, 11, 3));
        Assert.Equal(["Lunch break", "Weekend", "Time off", "Blank"], t.Select(x => x.Label));
        Assert.All(t, x => Assert.Null(Away.CheckSchedule(x.Name, x.From, x.Until, x.Days.ToList(), x.StartDate, x.EndDate, x.Message)));
        Assert.Equal(new DateOnly(2026, 11, 9), t[2].EndDate);
    }

    [Fact]
    public void TodaySpans_include_last_nights_tail()
    {
        var now = new DateTime(2026, 11, 3, 15, 0, 0); // a Tuesday
        Assert.Equal([(8 / 24.0, 21 / 24.0)], Away.TodaySpans(S(), now));
        Assert.Equal([(0.0, 8 / 24.0), (21 / 24.0, 1.0)], Away.TodaySpans(S("night", "21:00", "08:00"), now));
        // Weekdays only: Monday night's tail shows on Tuesday, and Tuesday night starts.
        Assert.Equal(2, Away.TodaySpans(S("night", "21:00", "08:00", days: [1, 2, 3, 4, 5]), now).Count);
        // Not today's weekday, and outside its dates: nothing.
        Assert.Empty(Away.TodaySpans(S(days: [0]), now));
        Assert.Empty(Away.TodaySpans(S(start: "2026-11-04"), now));
        Assert.Single(Away.TodaySpans(S(start: "2026-11-03", end: "2026-11-03"), now));
    }
}
