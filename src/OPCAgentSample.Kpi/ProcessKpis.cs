using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>Belegte und verfügbare Platzminuten eines Ofens; Belegung = belegt / verfügbar (zeitgewichtet, addierbar).</summary>
public sealed record OccupancyFigures(double OccupiedPlaceMinutes, double CapacityPlaceMinutes)
{
    public static OccupancyFigures Empty { get; } = new(0, 0);

    public double Ratio => CapacityPlaceMinutes > 0 ? OccupiedPlaceMinutes / CapacityPlaceMinutes : 0;

    public OccupancyFigures Add(OccupancyFigures other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(OccupiedPlaceMinutes + other.OccupiedPlaceMinutes, CapacityPlaceMinutes + other.CapacityPlaceMinutes);
    }
}

public static class OvenOccupancy
{
    /// <summary>Ofenbelegung aus den Minutenwerten „BelegtePlaetze“ eines Ofens im Fenster.</summary>
    public static OccupancyFigures From(IEnumerable<MinuteValue> values, string ovenId, int places, TimeWindow window)
    {
        ArgumentNullException.ThrowIfNull(values);
        var occupied = 0.0;
        var minutes = 0;
        foreach (var value in values)
        {
            if (value.StationId != ovenId || value.Variable != MinuteVariables.OccupiedPlaces || !window.Contains(value.TimestampUtc)) continue;
            occupied += value.Value;
            minutes++;
        }

        return new OccupancyFigures(occupied, (double)places * minutes);
    }
}

/// <summary>Reichweite eines Materials: begrenzt durch Füllstand oder durch die Verarbeitbarkeit der Mischung.</summary>
public sealed record MaterialReachResult(string Material, TimeSpan ByLevel, TimeSpan? ByProcessability)
{
    public TimeSpan Reach => ByProcessability is { } p && p < ByLevel ? p : ByLevel;

    public bool LimitedByProcessability => ByProcessability is { } p && p < ByLevel;
}

public static class MaterialReach
{
    /// <summary>
    /// Reichweite = Füllstand / Verbrauch je Minute; bei Mischungen zusätzlich die Zeit bis „verarbeitbar bis“.
    /// Die kleinere Zeit begrenzt.
    /// </summary>
    public static MaterialReachResult Compute(
        string material, double level, double consumptionPerMinute, DateTimeOffset? processableUntilUtc, DateTimeOffset nowUtc)
    {
        var byLevel = consumptionPerMinute > 0 ? TimeSpan.FromMinutes(level / consumptionPerMinute) : TimeSpan.MaxValue;
        TimeSpan? byProcessability = processableUntilUtc is { } until
            ? (until > nowUtc ? until - nowUtc : TimeSpan.Zero)
            : null;
        return new MaterialReachResult(material, byLevel, byProcessability);
    }
}

/// <summary>Statistik über Taktwerte (Mittelwerte, Ausschussquote); additiv, damit Stunden zu Schichten wachsen.</summary>
public sealed class CycleStatistics
{
    private readonly Dictionary<ScrapReason, long> _scrapByReason = [];

    public long Count { get; private set; }

    public long Scrap { get; private set; }

    public double SumCycleSeconds { get; private set; }

    public double SumFillWeightGrams { get; private set; }

    public double SumBuildUpSeconds { get; private set; }

    public double SumPressForceKn { get; private set; }

    public IReadOnlyDictionary<ScrapReason, long> ScrapByReason => _scrapByReason;

    public double MeanCycleSeconds => Count > 0 ? SumCycleSeconds / Count : 0;

    public double MeanFillWeightGrams => Count > 0 ? SumFillWeightGrams / Count : 0;

    public double MeanBuildUpSeconds => Count > 0 ? SumBuildUpSeconds / Count : 0;

    public double MeanPressForceKn => Count > 0 ? SumPressForceKn / Count : 0;

    public double ScrapRate => Count > 0 ? (double)Scrap / Count : 0;

    public static CycleStatistics Of(IEnumerable<CycleRecord> cycles)
    {
        ArgumentNullException.ThrowIfNull(cycles);
        var statistics = new CycleStatistics();
        foreach (var cycle in cycles)
        {
            statistics.Add(cycle);
        }

        return statistics;
    }

    public void Add(CycleRecord cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        Count++;
        SumCycleSeconds += cycle.CycleTimeSeconds;
        SumFillWeightGrams += cycle.FillWeightGrams;
        SumBuildUpSeconds += cycle.PressForceBuildUpSeconds;
        SumPressForceKn += cycle.PressForceKn;
        if (cycle.Scrap is { } reason)
        {
            Scrap++;
            _scrapByReason[reason] = _scrapByReason.GetValueOrDefault(reason) + 1;
        }
    }

    public void Add(CycleStatistics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Count += other.Count;
        Scrap += other.Scrap;
        SumCycleSeconds += other.SumCycleSeconds;
        SumFillWeightGrams += other.SumFillWeightGrams;
        SumBuildUpSeconds += other.SumBuildUpSeconds;
        SumPressForceKn += other.SumPressForceKn;
        foreach (var (reason, count) in other._scrapByReason)
        {
            _scrapByReason[reason] = _scrapByReason.GetValueOrDefault(reason) + count;
        }
    }
}

/// <summary>Standzeit einer Pressform: Verschleiß = Hübe / Standzeitgrenze, Prognose der Grenze beim aktuellen Hubtempo.</summary>
public sealed record MouldWearResult(int Strokes, int StrokeLimit, double Wear, DateTimeOffset? LimitReachedUtc);

public static class MouldWear
{
    public static MouldWearResult Compute(int strokes, int strokeLimit, double strokesPerMinute, DateTimeOffset nowUtc)
    {
        var wear = strokeLimit > 0 ? (double)strokes / strokeLimit : 0;
        DateTimeOffset? limit = strokesPerMinute > 0
            ? nowUtc + TimeSpan.FromMinutes(Math.Max(0, strokeLimit - strokes) / strokesPerMinute)
            : null;
        return new MouldWearResult(strokes, strokeLimit, wear, limit);
    }

    /// <summary>
    /// Produktionstag, an dem die Standzeitgrenze bei der Prognose „Hübe je Produktionstag“ erreicht wird
    /// (Wochenenden und Feiertage zählen nicht; der laufende Tag zählt voll).
    /// </summary>
    public static DateOnly ForecastLimitDay(int strokes, int strokeLimit, double strokesPerProductionDay, DateOnly today, ShiftCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(strokesPerProductionDay);
        double remaining = strokeLimit - strokes;
        var day = today;
        while (true)
        {
            if (calendar.IsProductionDay(day)) remaining -= strokesPerProductionDay;
            if (remaining <= 0) return day;
            day = day.AddDays(1);
        }
    }

    /// <summary>Verschleiß zu einem späteren Zeitpunkt bei gleichbleibendem Hubtempo.</summary>
    public static double WearAt(MouldWearResult current, double strokesPerMinute, TimeSpan after)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current.StrokeLimit > 0 ? (current.Strokes + (strokesPerMinute * after.TotalMinutes)) / current.StrokeLimit : 0;
    }
}
