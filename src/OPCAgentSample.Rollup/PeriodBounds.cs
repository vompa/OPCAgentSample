using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Rollup;

/// <summary>Zeitliche Grenzen der Perioden aller Ebenen (Schicht bis Jahr), halboffen in UTC.</summary>
public static class PeriodBounds
{
    public static TimeWindow Of(RollupLevel level, string key, ShiftCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentException.ThrowIfNullOrEmpty(key);
        switch (level)
        {
            case RollupLevel.Shift:
                var shift = calendar.ShiftPeriod(PeriodKeys.DayOfShift(key), ShiftOf(key));
                return new TimeWindow(shift.StartUtc, shift.EndUtc);
            case RollupLevel.Day:
                var day = calendar.DayPeriod(PeriodKeys.ParseDay(key));
                return new TimeWindow(day.StartUtc, day.EndUtc);
            case RollupLevel.Week:
                var week = calendar.WeekPeriod(WeekMonday(key));
                return new TimeWindow(week.StartUtc, week.EndUtc);
            case RollupLevel.Month:
                var month = DateOnly.ParseExact(key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture);
                return Days(calendar, month, month.AddMonths(1));
            case RollupLevel.Quarter:
                var year = int.Parse(key[..4], CultureInfo.InvariantCulture);
                var quarter = int.Parse(key[^1..], CultureInfo.InvariantCulture);
                var first = new DateOnly(year, ((quarter - 1) * 3) + 1, 1);
                return Days(calendar, first, first.AddMonths(3));
            case RollupLevel.Year:
                var january = new DateOnly(int.Parse(key, CultureInfo.InvariantCulture), 1, 1);
                return Days(calendar, january, january.AddYears(1));
            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, null);
        }
    }

    /// <summary>Beginn der Periode als Produktionstag-Grenze: erster Tag (einschließlich) bis zum Tag danach (ausgeschlossen).</summary>
    private static TimeWindow Days(ShiftCalendar calendar, DateOnly firstDay, DateOnly dayAfterLast) =>
        new(calendar.DayPeriod(firstDay).StartUtc, calendar.DayPeriod(dayAfterLast).StartUtc);

    private static Shift ShiftOf(string shiftKey) => shiftKey[^1] switch
    {
        'F' => Shift.Early,
        'S' => Shift.Late,
        'N' => Shift.Night,
        _ => throw new ArgumentException($"Ungültiger Schichtschlüssel '{shiftKey}'.", nameof(shiftKey)),
    };

    private static DateOnly WeekMonday(string key)
    {
        var year = int.Parse(key[..4], CultureInfo.InvariantCulture);
        var week = int.Parse(key[^2..], CultureInfo.InvariantCulture);
        return DateOnly.FromDateTime(ISOWeek.ToDateTime(year, week, DayOfWeek.Monday));
    }
}
