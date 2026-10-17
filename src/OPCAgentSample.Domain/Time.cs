using System.Globalization;

namespace OPCAgentSample.Domain;

/// <summary>Schicht im 3-Schicht-Betrieb: Früh 06–14, Spät 14–22, Nacht 22–06.</summary>
public enum Shift
{
    Early,
    Late,
    Night,
}

/// <summary>Ebene eines logischen Zeitbereichs (Spezifikation Abschnitt 15).</summary>
public enum PeriodKind
{
    Hour,
    Shift,
    Day,
    Week,
}

/// <summary>
/// Logischer Zeitbereich (Stunde, Schicht, Produktionstag, Kalenderwoche) mit stabilem Schlüssel und Grenzen in UTC.
/// Der Schlüssel ist Teil der Redis-Schlüssel der Kennzahlenblöcke und daher Vertrag.
/// </summary>
public sealed record ProductionPeriod(PeriodKind Kind, string Key, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public bool Contains(DateTimeOffset instant) => instant >= StartUtc && instant < EndUtc;

    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>
/// Schichtkalender des Werks in Ortszeit Wien. Produktion läuft Mo 06:00 bis Sa 06:00; Feiertage sind frei.
/// Die Nachtschicht gehört zum Produktionstag ihres Beginns (22:00). Ein Produktionstag läuft von 06:00 bis 06:00.
/// Alle Ergebnisse sind in UTC; die Zeitumstellung (z. B. 25.10.2026) wird über die Zeitzone berücksichtigt.
/// </summary>
public sealed class ShiftCalendar
{
    private static readonly TimeSpan s_dayStart = TimeSpan.FromHours(6);

    private readonly HashSet<DateOnly> _holidays;

    public ShiftCalendar(IEnumerable<DateOnly>? holidays = null, TimeZoneInfo? timeZone = null)
    {
        _holidays = [.. holidays ?? AustrianHolidays];
        TimeZone = timeZone ?? TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
    }

    public TimeZoneInfo TimeZone { get; }

    /// <summary>Gesetzliche Feiertage AT im Datenhorizont der Demo (Spezifikation Abschnitt 8).</summary>
    public static IReadOnlyList<DateOnly> AustrianHolidays { get; } =
    [
        new(2026, 10, 26), new(2026, 11, 1), new(2026, 12, 8), new(2026, 12, 25), new(2026, 12, 26),
        new(2027, 1, 1), new(2027, 1, 6),
    ];

    public bool IsHoliday(DateOnly day) => _holidays.Contains(day);

    /// <summary>Produktionstag eines Zeitpunkts (vor 06:00 Ortszeit zählt zum Vortag).</summary>
    public DateOnly ProductionDayOf(DateTimeOffset instant)
    {
        var local = ToLocal(instant);
        var day = DateOnly.FromDateTime(local.DateTime);
        return local.TimeOfDay < s_dayStart ? day.AddDays(-1) : day;
    }

    public Shift ShiftOf(DateTimeOffset instant)
    {
        var hour = ToLocal(instant).Hour;
        return hour switch
        {
            >= 6 and < 14 => Shift.Early,
            >= 14 and < 22 => Shift.Late,
            _ => Shift.Night,
        };
    }

    /// <summary>Wird an diesem Produktionstag produziert (Mo bis Fr, kein Feiertag)?</summary>
    public bool IsProductionDay(DateOnly day) =>
        day.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday && !IsHoliday(day);

    /// <summary>Wird zu diesem Zeitpunkt planmäßig produziert?</summary>
    public bool IsProductionTime(DateTimeOffset instant) => IsProductionDay(ProductionDayOf(instant));

    public ProductionPeriod HourOf(DateTimeOffset instant)
    {
        var local = ToLocal(instant);
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0, DateTimeKind.Unspecified);
        var key = start.ToString("yyyy-MM-dd'T'HH", CultureInfo.InvariantCulture);
        return new ProductionPeriod(PeriodKind.Hour, key, ToUtc(start), ToUtc(start.AddHours(1)));
    }

    public ProductionPeriod ShiftPeriodOf(DateTimeOffset instant) =>
        ShiftPeriod(ProductionDayOf(instant), ShiftOf(instant));

    public ProductionPeriod ShiftPeriod(DateOnly productionDay, Shift shift)
    {
        var dayStart = productionDay.ToDateTime(TimeOnly.MinValue).Add(s_dayStart);
        var start = dayStart.AddHours(8 * (int)shift);
        var key = $"{FormatDay(productionDay)}{ShiftCode(shift)}";
        return new ProductionPeriod(PeriodKind.Shift, key, ToUtc(start), ToUtc(start.AddHours(8)));
    }

    public ProductionPeriod DayPeriodOf(DateTimeOffset instant) => DayPeriod(ProductionDayOf(instant));

    public ProductionPeriod DayPeriod(DateOnly productionDay)
    {
        var start = productionDay.ToDateTime(TimeOnly.MinValue).Add(s_dayStart);
        return new ProductionPeriod(PeriodKind.Day, FormatDay(productionDay), ToUtc(start), ToUtc(start.AddDays(1)));
    }

    /// <summary>ISO-Kalenderwoche des Produktionstags; Grenzen Mo 06:00 bis Mo 06:00 der Folgewoche.</summary>
    public ProductionPeriod WeekPeriodOf(DateTimeOffset instant) => WeekPeriod(ProductionDayOf(instant));

    public ProductionPeriod WeekPeriod(DateOnly productionDay)
    {
        var monday = productionDay.AddDays(-(((int)productionDay.DayOfWeek + 6) % 7));
        var start = monday.ToDateTime(TimeOnly.MinValue).Add(s_dayStart);
        var date = monday.ToDateTime(TimeOnly.MinValue);
        var key = $"{ISOWeek.GetYear(date)}-W{ISOWeek.GetWeekOfYear(date):00}";
        return new ProductionPeriod(PeriodKind.Week, key, ToUtc(start), ToUtc(start.AddDays(7)));
    }

    /// <summary>Alle Schichten eines Zeitraums, in denen planmäßig produziert wird, in zeitlicher Reihenfolge.</summary>
    public IEnumerable<ProductionPeriod> ProductionShiftsBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        for (var day = ProductionDayOf(fromUtc); day <= ProductionDayOf(toUtc); day = day.AddDays(1))
        {
            if (!IsProductionDay(day)) continue;
            foreach (var shift in Enum.GetValues<Shift>())
            {
                var period = ShiftPeriod(day, shift);
                if (period.EndUtc > fromUtc && period.StartUtc < toUtc) yield return period;
            }
        }
    }

    public static string ShiftCode(Shift shift) => shift switch
    {
        Shift.Early => "F",
        Shift.Late => "S",
        Shift.Night => "N",
        _ => throw new ArgumentOutOfRangeException(nameof(shift), shift, null),
    };

    public static string ShiftLabel(Shift shift) => shift switch
    {
        Shift.Early => "Früh",
        Shift.Late => "Spät",
        Shift.Night => "Nacht",
        _ => throw new ArgumentOutOfRangeException(nameof(shift), shift, null),
    };

    public DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, TimeZone);

    /// <summary>Ortszeit (ohne Offset) nach UTC; bei der Zeitumstellung gilt die Standardzeit-Auflösung der Zeitzone.</summary>
    public DateTimeOffset ToUtc(DateTime local) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZone), TimeSpan.Zero);

    private static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Uhr der Simulation. Alle Komponenten lesen die Zeit hier, nie über <see cref="DateTimeOffset.UtcNow"/>.</summary>
public interface ISimulationClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Setzbare und beschleunigbare Uhr. Standard der Demo: Do 15.10.2026, 09:42 Ortszeit Wien (07:42 UTC).
/// Die Simulationszeit läuft ab dem Setzen mit dem Faktor <see cref="Speed"/> weiter.
/// </summary>
public sealed class SimulationClock : ISimulationClock
{
    public static readonly DateTimeOffset DemoStart = new(2026, 10, 15, 7, 42, 0, TimeSpan.Zero);

    private readonly TimeProvider _realTime;
    private readonly Lock _lock = new();
    private DateTimeOffset _anchorSimulated;
    private DateTimeOffset _anchorReal;
    private double _speed;

    public SimulationClock(DateTimeOffset? start = null, double speed = 1.0, TimeProvider? realTime = null)
    {
        _realTime = realTime ?? TimeProvider.System;
        _anchorReal = _realTime.GetUtcNow();
        _anchorSimulated = start ?? DemoStart;
        _speed = ValidSpeed(speed);
    }

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_lock)
            {
                var elapsed = _realTime.GetUtcNow() - _anchorReal;
                return _anchorSimulated + TimeSpan.FromTicks((long)(elapsed.Ticks * _speed));
            }
        }
    }

    public double Speed
    {
        get { lock (_lock) return _speed; }
    }

    /// <summary>Setzt die Simulationszeit; die Geschwindigkeit bleibt.</summary>
    public void Set(DateTimeOffset simulatedUtc)
    {
        lock (_lock)
        {
            _anchorReal = _realTime.GetUtcNow();
            _anchorSimulated = simulatedUtc;
        }
    }

    /// <summary>Ändert die Geschwindigkeit ab jetzt (z. B. 60 = eine Minute Simulation pro Sekunde); 0 hält die Uhr an.</summary>
    public void SetSpeed(double speed)
    {
        var valid = ValidSpeed(speed);
        lock (_lock)
        {
            var now = _realTime.GetUtcNow();
            _anchorSimulated += TimeSpan.FromTicks((long)((now - _anchorReal).Ticks * _speed));
            _anchorReal = now;
            _speed = valid;
        }
    }

    private static double ValidSpeed(double speed) =>
        double.IsFinite(speed) && speed >= 0
            ? speed
            : throw new ArgumentOutOfRangeException(nameof(speed), speed, "Die Geschwindigkeit muss endlich und >= 0 sein.");
}
