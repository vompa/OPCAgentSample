using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Abschnitt einer Schicht mit konstantem Nutzungsgrad; <see cref="Target"/> ist die kalibrierte Stückzahl.</summary>
internal sealed record RateSegment(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Utilization, int? Target);

/// <summary>
/// Ergebnis einer Schicht an einer Produktionsstation: Kurzstopps des Grundrauschens, Anlauf, Taktzeitpunkte und
/// Ausschussmarken. Die Taktzeitpunkte sind unabhängig vom Seed (nur die Lage der Kurzstopps hängt am Seed),
/// deshalb treffen kalibrierte Stückzahlen bei jedem Seed.
/// </summary>
internal sealed class ShiftRun
{
    private readonly int[] _scrapPrefix;

    public ShiftRun(
        ProductionPeriod shift,
        int plannedQuantity,
        TimeWindow startupWindow,
        IReadOnlyList<PlannedLoss> noiseStops,
        IReadOnlyList<RateSegment> segments,
        CycleProfile profile,
        long[] cycleTicks,
        bool[] scrap,
        ScrapReason?[] fixedReasons)
    {
        Shift = shift;
        PlannedQuantity = plannedQuantity;
        StartupWindow = startupWindow;
        NoiseStops = noiseStops;
        Segments = segments;
        Profile = profile;
        CycleTicks = cycleTicks;
        Scrap = scrap;
        FixedReasons = fixedReasons;
        _scrapPrefix = new int[scrap.Length + 1];
        for (var i = 0; i < scrap.Length; i++)
        {
            _scrapPrefix[i + 1] = _scrapPrefix[i] + (scrap[i] ? 1 : 0);
        }
    }

    public ProductionPeriod Shift { get; }

    public CycleProfile Profile { get; }

    public int PlannedQuantity { get; }

    public TimeWindow StartupWindow { get; }

    public IReadOnlyList<PlannedLoss> NoiseStops { get; }

    public IReadOnlyList<RateSegment> Segments { get; }

    public long[] CycleTicks { get; }

    public bool[] Scrap { get; }

    public ScrapReason?[] FixedReasons { get; }

    /// <summary>Anzahl Takte vor dem Zeitpunkt (Zeitstempel &lt; <paramref name="instant"/>).</summary>
    public int CountBefore(DateTimeOffset instant)
    {
        var index = Array.BinarySearch(CycleTicks, instant.UtcTicks);
        return index >= 0 ? index : ~index;
    }

    public int CountBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        Math.Max(0, CountBefore(toUtc) - CountBefore(fromUtc));

    public int ScrapBefore(DateTimeOffset instant) => _scrapPrefix[CountBefore(instant)];

    public int ScrapBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        Math.Max(0, ScrapBefore(toUtc) - ScrapBefore(fromUtc));

    public PlannedLoss? NoiseStopAt(DateTimeOffset instant) =>
        NoiseStops.FirstOrDefault(stop => instant >= stop.StartUtc && instant < stop.EndUtc);
}
