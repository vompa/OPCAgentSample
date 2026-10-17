using System.Diagnostics;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Snapshot;

namespace OPCAgentSample.Tests.Server.Ingestion;

/// <summary>
/// Die Historie des Szenarios, einmal je Testlauf erzeugt (rund 1,5 Mio. Datensätze) und von allen Aufnahme-Tests
/// gemeinsam genutzt. <see cref="Records"/> sind die Roh-Datensätze des Generators; <see cref="All"/> enthält zusätzlich die
/// Tageskennzahlen (Historie B, Hallen B und C) in zeitlicher Reihenfolge – das ist, was eine Quelle liefert.
/// </summary>
public sealed class SharedHistory
{
    public static readonly string[] OeeStations = ["RT-01", "RT-02", "RT-03", "RT-04", "VP-01", "VP-02"];

    private static readonly Lazy<SharedHistory> s_instance = new(() => new SharedHistory(), LazyThreadSafetyMode.ExecutionAndPublication);

    private SharedHistory()
    {
        Scenario = HallAScenario.Create();
        Generator = new HistoryGenerator(Scenario);
        Costs = Scenario.CostRates();

        var watch = Stopwatch.StartNew();
        Records = [.. Generator.GenerateUntilDemo()];
        All = GeneratedHistorySource.Combine(Scenario, Generator, Records);
        GenerationTime = watch.Elapsed;
    }

    public static SharedHistory Instance => s_instance.Value;

    public ScenarioDefinition Scenario { get; }

    public HistoryGenerator Generator { get; }

    public CostRateTable Costs { get; }

    public ShiftCalendar Calendar => Scenario.Calendar;

    public DateTimeOffset Now => Scenario.DemoTimeUtc;

    /// <summary>Roh-Datensätze des Generators bis zur Demo-Uhr.</summary>
    public IReadOnlyList<HistoryRecord> Records { get; }

    /// <summary>Alles, was die Testquelle liefert: Roh-Datensätze und Tageskennzahlen, nach Zeit sortiert.</summary>
    public IReadOnlyList<HistoryRecord> All { get; }

    public TimeSpan GenerationTime { get; }

    /// <summary>Kennzahlenblock aus den Datensätzen des Generators (Referenzrechnung der Kennzahlen-Bibliothek).</summary>
    public KpiBlock Block(ProductionPeriod period, DateTimeOffset? until = null)
    {
        var builder = new KpiBlockBuilder(period, Costs);
        builder.AddRange(Records);
        return builder.Snapshot(until ?? (period.EndUtc < Now ? period.EndUtc : Now));
    }

    public KpiBlock Week42() => Block(Calendar.WeekPeriod(new DateOnly(2026, 10, 14)));

    public ProductionPeriod Shift(int day, Shift shift) => Calendar.ShiftPeriod(new DateOnly(2026, 10, day), shift);
}

/// <summary>Testquelle: liefert die Historie des Generators in Fenstern, wie es der Collector per HistoryRead täte.</summary>
public sealed class GeneratorHistorySource : IHistorySource
{
    public const string Id = "halle-a";

    private readonly IReadOnlyList<HistoryRecord> _records;

    /// <param name="history">Die Historie des Szenarios.</param>
    /// <param name="startUtc">Frühester Zeitpunkt der Quelle; Standard: Beginn der Tageskennzahlen (Historie B).</param>
    public GeneratorHistorySource(SharedHistory history, DateTimeOffset? startUtc = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        _records = history.All;
        HistoryStartUtc = startUtc ?? history.Calendar.DayPeriod(history.Scenario.DailyHistoryStart).StartUtc;
    }

    public string SourceId => Id;

    public DateTimeOffset HistoryStartUtc { get; }

    public IAsyncEnumerable<HistoryRecord> ReadAsync(
        DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken = default) =>
        Window(fromUtc, untilUtc).ToAsyncEnumerable();

    private IEnumerable<HistoryRecord> Window(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        for (var i = LowerBound(fromUtc); i < _records.Count && _records[i].TimestampUtc < untilUtc; i++)
        {
            yield return _records[i];
        }
    }

    /// <summary>Index des ersten Datensatzes mit Zeitstempel ab <paramref name="fromUtc"/> (die Liste ist sortiert).</summary>
    private int LowerBound(DateTimeOffset fromUtc)
    {
        int low = 0, high = _records.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_records[middle].TimestampUtc < fromUtc) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}
