using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TelegramMCPTray.Core;

/// <summary>
/// The Away page: the words for the away auto-reply state and the same checks the daemon makes
/// (src/shared/away.ts), so a mistake is caught before the call. The daemon does the saving.
/// </summary>
public static class Away
{
    /// <summary>A 404 from POST /ui/away, or no away in /ui/state.</summary>
    public const string OlderDaemon = "The daemon is older than this app. Restart it to set away hours.";

    /// <summary>A daemon that knows one away window but not schedules.</summary>
    public const string NoSchedules = "The daemon is older than this app and knows only one away window. Restart it to use schedules.";

    public const int MessageMax = 1000;
    public const int NameMax = 40;
    public const int DelayMax = 600;
    public const int RepeatMax = 1440;
    public const int SchedulesMax = 12;
    public const int QuietMax = 1440;

    static readonly Regex TimeRe = new(@"^([01][0-9]|2[0-3]):[0-5][0-9]\z", RegexOptions.CultureInvariant);

    static readonly string[] DayShort = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    /// <summary>The week as the day chips show it: Monday first.</summary>
    public static readonly int[] WeekOrder = [1, 2, 3, 4, 5, 6, 0];
    public static readonly int[] AllDays = [0, 1, 2, 3, 4, 5, 6];
    public static readonly int[] Weekdays = [1, 2, 3, 4, 5];
    public static readonly int[] Weekends = [0, 6];

    /// <summary>The waits offered, in seconds: at once, 30 s, 1, 2 and 5 minutes.</summary>
    public static readonly int[] Delays = [0, 30, 60, 120, 300];
    /// <summary>How soon a chat can get another reply, in minutes: once per period, after an hour, after 3 hours.</summary>
    public static readonly int[] Repeats = [0, 60, 180];

    /// <summary>Is this chat on away.skip, so it never gets an away reply?</summary>
    public static bool Skips(UiAway? a, long chatId) => a?.Skip?.Any(s => s.ChatId == chatId) == true;

    /// <summary>
    /// away.skip with one chat switched: every entry that points at it is dropped, and with <paramref name="skip"/>
    /// its nickname goes back on once. Entries for other chats go back exactly as saved.
    /// </summary>
    public static List<JsonNode?> SkipWith(UiAway a, long chatId, string nickname, bool skip)
    {
        var list = (a.Skip ?? []).Where(s => s.ChatId != chatId).Select(s => JsonSerializer.SerializeToNode(s.Ref)).ToList();
        if (skip) list.Add(JsonValue.Create(nickname));
        return list;
    }

    public static UiAwaySchedule? Current(UiAway a) =>
        a.Current is { } id ? a.Schedules?.FirstOrDefault(s => s.Id == id) : null;

    /// <summary>The line under the Away title and on Home's Auto-reply card.</summary>
    public static string Status(UiAway a)
    {
        if (!a.Enabled) return "Off";
        if (a.Schedules is { } list && !list.Any(s => s.Enabled)) return "On, but every schedule is switched off";
        if (Current(a) is { } c) return $"{c.Name} until {c.Until}, {DelayText(c.DelaySeconds)}";
        if (a.Active) return $"Away now, until {a.Until}";
        if (a.Schedules is null) return $"Starts at {a.From}";
        if (a.NextChange is not { } next) return "Nothing scheduled";
        var local = next.ToLocalTime();
        return local - DateTimeOffset.Now < TimeSpan.FromDays(1) ? $"Next at {local:HH:mm}" : $"Next on {local:ddd d MMM} at {local:HH:mm}";
    }

    /// <summary>The line for the Status page while a reply could go out; null otherwise.</summary>
    public static string? Badge(UiAway? a) =>
        a is not { Enabled: true, Active: true } ? null
        : Current(a) is { } c ? $"{c.Name} until {c.Until}"
        : $"Away until {a.Until}";

    public static bool IsTime(string s) => TimeRe.IsMatch(s);

    /// <summary>Quiet minutes as typed: a whole number from 0 to a day, or null. No sign, decimals or exponent.</summary>
    public static int? Quiet(string s) =>
        int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= QuietMax ? n : null;

    /// <summary>The first problem with a single window, in the daemon's words (an older daemon's single window).</summary>
    public static string? Check(string from, string until, string message, string quiet = "30")
    {
        if (!IsTime(from)) return "from must be a time like 18:00";
        if (!IsTime(until)) return "until must be a time like 18:00";
        if (from == until) return "From and until can't be the same time.";
        if (message.Trim() == "") return "The message can't be empty.";
        if (message.Length > MessageMax) return $"The message is too long ({MessageMax} characters at most).";
        if (Quiet(quiet) is null) return $"quietMinutes must be a whole number from 0 to {QuietMax}";
        return null;
    }

    /// <summary>The first problem with one schedule as typed, or null.</summary>
    public static string? CheckSchedule(string name, string from, string until, IReadOnlyCollection<int> days,
        DateOnly? startDate, DateOnly? endDate, string message)
    {
        if (name.Trim() == "") return "Give it a name, like Lunch or Holiday.";
        if (name.Trim().Length > NameMax) return $"The name is {NameMax} characters at most.";
        if (!IsTime(from) || !IsTime(until)) return "Pick a start and an end time.";
        if (from == until) return "It can't start and end at the same time.";
        if (days.Count == 0) return "Pick at least one day.";
        if (startDate is { } s && endDate is { } e && s > e) return "The first date must be on or before the last.";
        if (message.Trim() == "") return "The message can't be empty.";
        if (message.Length > MessageMax) return $"The message is too long ({MessageMax} characters at most).";
        return null;
    }

    /// <summary>
    /// What the message would send: {name} becomes the first word of your name (left as is when it is unknown),
    /// then the footer is added unless the text already ends with it. Mirrors withFooter in src/shared/policy.ts.
    /// </summary>
    public static string Preview(string message, string? meName, string footer)
    {
        var first = meName?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var text = first is null ? message : message.Replace("{name}", first, StringComparison.Ordinal);
        var mark = footer.Trim();
        if (mark == "") return text;
        var body = text.TrimEnd();
        return body.EndsWith(mark, StringComparison.Ordinal) ? body : body + footer;
    }

    // ---- words ----------------------------------------------------------------------------

    /// <summary>"Every day", "Weekdays", "Weekends", "Mon, Wed, Fri". Mirrors daysText in away.ts.</summary>
    public static string DaysText(IEnumerable<int> days)
    {
        var set = days.ToHashSet();
        if (set.Count == 7) return "Every day";
        if (set.Count == 5 && Weekdays.All(set.Contains)) return "Weekdays";
        if (set.Count == 2 && Weekends.All(set.Contains)) return "Weekends";
        return string.Join(", ", WeekOrder.Where(set.Contains).Select(d => DayShort[d]));
    }

    public static string DayLetter(int day) => DayShort[day][..1];
    public static string DayName(int day) => CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)day);

    public static string DelayText(int seconds) =>
        seconds == 0 ? "replies at once"
        : seconds < 60 || seconds % 60 != 0 ? $"waits {seconds} s for you"
        : $"waits {seconds / 60} min for you";

    /// <summary>The chip label for a wait.</summary>
    public static string DelayChip(int seconds) =>
        seconds == 0 ? "At once" : seconds < 60 || seconds % 60 != 0 ? $"{seconds} s" : $"{seconds / 60} min";

    public static string RepeatChip(int minutes) =>
        minutes == 0 ? "Once" : minutes % 60 == 0 ? $"Every {minutes / 60} h" : $"Every {minutes} min";

    public static string RepeatText(int minutes) =>
        minutes == 0 ? "One reply per chat each time this schedule runs."
        : $"A chat can get another reply after {(minutes % 60 == 0 ? $"{minutes / 60} hour{(minutes == 60 ? "" : "s")}" : $"{minutes} minutes")}.";

    /// <summary>"1 Dec – 24 Dec 2026", "From 1 Dec 2026", "Until 24 Dec 2026", or null for every week.</summary>
    public static string? DatesText(DateOnly? start, DateOnly? end)
    {
        static string D(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return (start, end) switch
        {
            ({ } s, { } e) when s == e => $"On {D(s)}",
            ({ } s, { } e) when s.Year == e.Year => $"{s.ToString("d MMM", CultureInfo.InvariantCulture)} – {D(e)}",
            ({ } s, { } e) => $"{D(s)} – {D(e)}",
            ({ } s, null) => $"From {D(s)}",
            (null, { } e) => $"Until {D(e)}",
            _ => null,
        };
    }

    public static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string? FormatDate(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A night schedule (it runs past midnight, or starts in the evening) gets the moon; others the sun.</summary>
    public static bool IsNight(string from, string until) =>
        string.CompareOrdinal(from, until) > 0 || string.CompareOrdinal(from, "18:00") >= 0;

    // ---- the pickers ----------------------------------------------------------------------

    /// <summary>Every quarter hour of the day, plus `current` when it falls between them, in order.</summary>
    public static IReadOnlyList<string> TimeOptions(string? current = null)
    {
        var list = Enumerable.Range(0, 96).Select(i => $"{i / 4:00}:{i % 4 * 15:00}").ToList();
        if (current is not null && IsTime(current) && !list.Contains(current))
        {
            list.Add(current);
            list.Sort(StringComparer.Ordinal);
        }
        return list;
    }

    /// <summary>An id no schedule has yet: s1, s2…</summary>
    public static string NewId(IEnumerable<string> taken)
    {
        var set = taken.ToHashSet();
        for (var n = 1; ; n++)
            if (!set.Contains($"s{n}")) return $"s{n}";
    }

    /// <summary>Starting points for a new schedule.</summary>
    public static IReadOnlyList<ScheduleTemplate> Templates(DateOnly today) =>
    [
        new("Lunch break", "Lunch break", "12:00", "13:00", Weekdays, null, null, 30, 60,
            "Hi! I'm on my lunch break right now and will reply as soon as I'm back 🍽️"),
        new("Weekend", "Weekend", "00:00", "23:59", Weekends, null, null, 0, 0,
            "Hi! Thanks for your message. It's the weekend, so I'll get back to you on Monday 🙂"),
        new("Time off", "Time off", "00:00", "23:59", AllDays, today, today.AddDays(6), 0, 0,
            "Hi! I'm away on leave and will reply when I'm back. Thanks for your patience 🌴"),
        new("Blank", "New schedule", "09:00", "17:00", Weekdays, null, null, 30, 60,
            "Hi! Thanks for contacting me. I'm unavailable right now and will reply soon."),
    ];

    // ---- today's timeline -----------------------------------------------------------------

    /// <summary>
    /// The parts of today (as fractions of the day, 0 to 1) each schedule covers, overnight windows from
    /// last night included. Ignores `enabled` so a switched-off schedule can be drawn faded.
    /// </summary>
    public static IReadOnlyList<(double Start, double End)> TodaySpans(UiAwaySchedule s, DateTime now)
    {
        var today = now.Date;
        var spans = new List<(double, double)>();
        foreach (var offset in new[] { -1, 0 })
        {
            var day = today.AddDays(offset);
            if (!RunsOn(s, day)) continue;
            var start = day + TimeSpan.Parse(s.From, CultureInfo.InvariantCulture);
            var end = day.AddDays(string.CompareOrdinal(s.From, s.Until) > 0 ? 1 : 0) + TimeSpan.Parse(s.Until, CultureInfo.InvariantCulture);
            var a = Math.Max((start - today).TotalMinutes, 0);
            var b = Math.Min((end - today).TotalMinutes, 1440);
            if (b > a) spans.Add((a / 1440, b / 1440));
        }
        return spans;
    }

    /// <summary>Does it start on this day (its weekday, and within its dates)? Mirrors runsOn in away.ts.</summary>
    public static bool RunsOn(UiAwaySchedule s, DateTime day)
    {
        var d = DateOnly.FromDateTime(day);
        return s.Days.Contains((int)day.DayOfWeek)
            && (ParseDate(s.StartDate) is not { } start || d >= start)
            && (ParseDate(s.EndDate) is not { } end || d <= end);
    }
}

/// <summary>A starting point offered by "Add a schedule".</summary>
public sealed record ScheduleTemplate(
    string Label, string Name, string From, string Until, IReadOnlyList<int> Days, DateOnly? StartDate, DateOnly? EndDate,
    int DelaySeconds, int RepeatMinutes, string Message);
