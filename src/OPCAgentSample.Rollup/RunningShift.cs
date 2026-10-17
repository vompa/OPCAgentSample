using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Rollup;

/// <summary>Die offenen Fächer einer Reihe in der laufenden Schicht (15 Minuten und, bei Rundtischen, 1 Minute).</summary>
internal sealed class SeriesBuckets(SeriesInfo info)
{
    private const long MinuteTicks = TimeSpan.TicksPerMinute;
    private const long QuarterHourTicks = 15 * TimeSpan.TicksPerMinute;

    public SeriesInfo Info { get; } = info;

    public Dictionary<int, ProcessStat> Fine { get; } = [];

    public Dictionary<int, ProcessStat> Coarse { get; } = [];

    public HashSet<int> DirtyFine { get; } = [];

    public HashSet<int> DirtyCoarse { get; } = [];

    public bool IsDirty => DirtyCoarse.Count > 0 || DirtyFine.Count > 0;

    /// <param name="at">Zeitpunkt des Werts.</param>
    /// <param name="value">Messwert (Summe, Mittel, Minimum, Maximum).</param>
    /// <param name="binValue">Wert, der ins Histogramm einsortiert wird (z. B. Abweichung vom Soll in Prozent).</param>
    /// <param name="dayStart">Beginn des Produktionstags (das Fach zählt ab hier).</param>
    /// <param name="fineFrom">Frühester Zeitpunkt, ab dem das 1-Minuten-Raster geführt wird.</param>
    public void Observe(DateTimeOffset at, double value, double binValue, DateTimeOffset dayStart, DateTimeOffset fineFrom)
    {
        var bin = Info.Spec?.BinOf(binValue) ?? -1;
        var ticks = (at - dayStart).Ticks;
        Add(Coarse, DirtyCoarse, (int)(ticks / QuarterHourTicks), value, bin);
        if (Info.HasFine && at >= fineFrom) Add(Fine, DirtyFine, (int)(ticks / MinuteTicks), value, bin);
    }

    /// <summary>Statistik der ganzen Schicht: Summe aller 15-Minuten-Fächer.</summary>
    public ProcessStat ShiftStat()
    {
        var stat = new ProcessStat(Info.Bins);
        foreach (var bucket in Coarse.Values) stat.Merge(bucket);
        return stat;
    }

    private void Add(Dictionary<int, ProcessStat> buckets, HashSet<int> dirty, int slot, double value, int bin)
    {
        if (!buckets.TryGetValue(slot, out var stat)) buckets[slot] = stat = new ProcessStat(Info.Bins);
        stat.Add(value, bin);
        dirty.Add(slot);
    }

    public void ClearDirty()
    {
        DirtyFine.Clear();
        DirtyCoarse.Clear();
    }
}

/// <summary>
/// Der laufende Schichtblock im Speicher: der <see cref="KpiBlockBuilder"/> der Kennzahlen-Bibliothek (eine Rechenfunktion je
/// Kennzahl), der Ausschuss je Grund, die Planfenster und die offenen Fächer der Prozessstatistik.
/// </summary>
internal sealed class RunningShift(ProductionPeriod period, DateOnly day, DateTimeOffset dayStartUtc, KpiBlockBuilder builder)
{
    private readonly Dictionary<(string Station, string Variable), SeriesBuckets> _series = [];

    public ProductionPeriod Period { get; } = period;

    public DateOnly Day { get; } = day;

    public string DayKey { get; } = PeriodKeys.Day(day);

    public DateTimeOffset DayStartUtc { get; } = dayStartUtc;

    public KpiBlockBuilder Builder { get; } = builder;

    /// <summary>Ausschuss je Station und Grund in dieser Schicht.</summary>
    public Dictionary<(string Station, string Reason), long> Scrap { get; } = [];

    public Dictionary<string, PlanWindow> Plans { get; } = [];

    public IEnumerable<SeriesBuckets> AllSeries => _series.Values;

    public SeriesBuckets Series(string station, string variable)
    {
        if (!_series.TryGetValue((station, variable), out var buckets))
        {
            _series[(station, variable)] = buckets = new SeriesBuckets(SeriesCatalog.For(station, variable));
        }

        return buckets;
    }
}
