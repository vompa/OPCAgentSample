using System.Globalization;
using OPCAgentSample.Domain;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Schlüssel der Perioden in Redis. Schicht, Tag und Woche kommen aus dem <see cref="ShiftCalendar"/> (<c>2026-10-14N</c>,
/// <c>2026-10-14</c>, <c>2026-W42</c>); Monat (<c>2026-10</c>), Quartal (<c>2026-Q4</c>) und Jahr (<c>2026</c>) richten sich nach dem
/// Produktionstag. Der Schlüssel ist Vertrag: Er steht in den Redis-Schlüsseln und in den Antworten der Tools.
/// </summary>
public static class PeriodKeys
{
    public static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string key) => DateOnly.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Month(DateOnly day) => day.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string Quarter(DateOnly day) => string.Create(CultureInfo.InvariantCulture, $"{day.Year}-Q{((day.Month - 1) / 3) + 1}");

    public static string Year(DateOnly day) => day.Year.ToString(CultureInfo.InvariantCulture);

    /// <summary>Schlüssel der Periode <paramref name="level"/> (Tag oder höher), in der der Produktionstag liegt.</summary>
    public static string Of(RollupLevel level, DateOnly productionDay, ShiftCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return level switch
        {
            RollupLevel.Day => Day(productionDay),
            RollupLevel.Week => calendar.WeekPeriod(productionDay).Key,
            RollupLevel.Month => Month(productionDay),
            RollupLevel.Quarter => Quarter(productionDay),
            RollupLevel.Year => Year(productionDay),
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Eine Schicht hat keinen Schlüssel aus dem Produktionstag allein."),
        };
    }

    /// <summary>Produktionstag, zu dem ein Schichtschlüssel (<c>2026-10-14N</c>) gehört.</summary>
    public static DateOnly DayOfShift(string shiftKey) => ParseDay(shiftKey[..10]);
}
