using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Persistence;

// Aggregates the three things worth showing on a calendar: birthdays (recurring MM-DD), calendar
// events (dated, some recurring yearly) and project deadlines. Lives in Infrastructure so both the
// CalendarView and the startup/lead-time notification path read the same agenda.
public sealed class CalendarService
{
    private readonly BirthdayRepository _birthdayRepo;
    private readonly CalendarEventRepository _eventRepo;
    private readonly PersonalProjectRepository _projectRepo;

    public CalendarService(SqliteConnectionFactory connectionFactory)
    {
        _birthdayRepo = new BirthdayRepository(connectionFactory);
        _eventRepo = new CalendarEventRepository(connectionFactory);
        _projectRepo = new PersonalProjectRepository(connectionFactory);
    }

    // All items whose effective date falls in [from, to] inclusive. Recurring birthdays/events are
    // materialised inside the range, so a wide window shows one row per occurrence.
    public IReadOnlyList<AgendaItem> GetRange(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;
        var items = new List<AgendaItem>();

        foreach (var birthday in _birthdayRepo.GetAll())
        {
            var anniversary = NextAnniversaryIn(birthday.Month, birthday.Day, fromDate, toDate);
            if (anniversary is not { } date) continue;
            items.Add(new AgendaItem
            {
                Date = date,
                Title = BirthdayTitle(birthday.Name),
                Subtitle = birthday.BirthYear is int year ? TurningAgeText(year, date.Year) : string.Empty,
                Source = AgendaSource.Birthday,
                RefId = birthday.Id
            });
        }

        foreach (var calendarEvent in _eventRepo.GetAll())
        {
            if (calendarEvent.RecurringYearly)
            {
                var occurrence = NextAnniversaryIn(calendarEvent.Date.Month, calendarEvent.Date.Day, fromDate, toDate);
                if (occurrence is not { } date) continue;
                items.Add(ToItem(calendarEvent, date, isFirstDay: true));
                continue;
            }

            // Multi-day: one row per covered day (clamped to the window), hour only on day one.
            var start = calendarEvent.Date.Date;
            var end = calendarEvent.EndDate is { } endDate && endDate.Date > start ? endDate.Date : start;
            for (var day = MaxDay(start, fromDate); day <= MinDay(end, toDate); day = day.AddDays(1))
                items.Add(ToItem(calendarEvent, day, isFirstDay: day == start));
        }

        foreach (var project in _projectRepo.GetAll())
        {
            if (project.Deadline is not { } deadline) continue;
            if (project.Status == ProjectStatus.Archived) continue;
            var when = deadline.Date;
            if (when < fromDate || when > toDate) continue;
            items.Add(new AgendaItem
            {
                Date = when,
                Title = project.Title,
                Subtitle = project.Status == ProjectStatus.Done ? "Échéance (projet terminé)" : "Échéance de projet",
                Source = AgendaSource.Deadline,
                RefId = project.Id
            });
        }

        return items
            .OrderBy(i => i.Date)
            .ThenBy(i => i.Hour ?? -1)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // Everything from "today - backDays" to +horizonDays: the window the day panel and the toast share.
    public IReadOnlyList<AgendaItem> GetUpcoming(int horizonDays = 30, int backDays = 0) =>
        GetRange(DateTime.Today.AddDays(-backDays), DateTime.Today.AddDays(horizonDays));

    // The next occurrence of a month/day at or after today, for the "J-N" badges and lead-time
    // checks. A Feb-29 birthday that a non-leap year can't hold lands on Feb-28 (kept visible,
    // never silently dropped).
    public DateTime? NextOccurrenceOnMonthDay(int month, int day)
    {
        var today = DateTime.Today;
        for (var year = today.Year; year <= today.Year + 1; year++)
        {
            var candidate = SafeDate(year, month, day);
            if (candidate >= today) return candidate;
        }
        return null;
    }

    private static DateTime? NextAnniversaryIn(int month, int day, DateTime fromDate, DateTime toDate)
    {
        for (var year = fromDate.Year; year <= toDate.Year; year++)
        {
            var candidate = SafeDate(year, month, day);
            if (candidate >= fromDate && candidate <= toDate) return candidate;
        }
        return null;
    }

    // Clamp an impossible date (29 Feb in a non-leap year) to the last day of the month.
    private static DateTime SafeDate(int year, int month, int day)
    {
        var max = DateTime.DaysInMonth(year, month);
        return new DateTime(year, month, Math.Min(Math.Max(day, 1), max));
    }

    private static string TurningAgeText(int birthYear, int anniversaryYear)
    {
        var age = anniversaryYear - birthYear;
        if (age <= 0) return string.Empty;
        return age == 1 ? "1 an" : $"{age} ans";
    }

    // French elision: "Anniversaire d'Alyssa", not "de Alyssa". Applied to a vowel-initial name or
    // a mute h; the handful of aspirated-h names (Hubert) read acceptably either way, and guessing
    // wrong there is far less visible than the hiatus "de Alyssa".
    public static string BirthdayTitle(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "Anniversaire";
        var first = char.ToLowerInvariant(trimmed[0]);
        var elide = "aeiouyàâäéèêëïîôöùûüÿh".Contains(first);
        return elide ? $"Anniversaire d'{trimmed}" : $"Anniversaire de {trimmed}";
    }

    // The bare name back out of a BirthdayTitle, for the month grid's chips: the chip is a ~130px
    // fragment and the colored dot already says "birthday", so the "Anniversaire de " lead-in is
    // dead weight there — "Louison" reads better than "Anniversaire de Lui…". The day panel, the
    // upcoming list and the toast keep the full title, where there is room to spell the kind out.
    public static string BirthdayChipName(string title) => title switch
    {
        _ when title.StartsWith("Anniversaire d'", StringComparison.Ordinal) => title["Anniversaire d'".Length..],
        _ when title.StartsWith("Anniversaire de ", StringComparison.Ordinal) => title["Anniversaire de ".Length..],
        _ => title,
    };

    private static AgendaItem ToItem(CalendarEvent calendarEvent, DateTime date, bool isFirstDay) => new()
    {
        Date = date,
        Hour = isFirstDay ? calendarEvent.StartHour : null,
        Minute = isFirstDay ? calendarEvent.StartMinute : null,
        Title = calendarEvent.Title,
        Subtitle = calendarEvent.Location,
        Source = AgendaSource.Event,
        EventKind = calendarEvent.Kind,
        RefId = calendarEvent.Id,
        IsSpanContinuation = !isFirstDay,
    };

    private static DateTime MaxDay(DateTime a, DateTime b) => a >= b ? a : b;
    private static DateTime MinDay(DateTime a, DateTime b) => a <= b ? a : b;
}
