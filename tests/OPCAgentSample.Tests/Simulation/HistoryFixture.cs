using System.Diagnostics;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Erzeugt die Historie bis zur Demo-Uhr einmal je Testlauf und stellt Kennzahlenblöcke bereit.</summary>
public sealed class HistoryFixture
{
    public const string HallA = "A";

    public static readonly string[] Presses = ["RT-01", "RT-02", "RT-03", "RT-04"];
    public static readonly string[] Packaging = ["VP-01", "VP-02"];

    /// <summary>Hallen-OEE: Pressen und Verpackung (Öfen werden über die Belegung bewertet).</summary>
    public static readonly string[] OeeStations = [.. Presses, .. Packaging];

    public HistoryFixture()
    {
        Scenario = HallAScenario.Create();
        Generator = new HistoryGenerator(Scenario);
        var watch = Stopwatch.StartNew();
        Records = [.. Generator.GenerateUntilDemo()];
        GenerationTime = watch.Elapsed;
        Costs = Scenario.CostRates();
        Live = new LiveStateProvider(Generator);
        _dailyKpis = new Lazy<IReadOnlyList<DailyStationKpi>>(() => DailyKpiHistory.Build(Generator));
    }

    private readonly Lazy<IReadOnlyList<DailyStationKpi>> _dailyKpis;

    public LiveStateProvider Live { get; }

    /// <summary>Tageskennzahlen (Historie B) vom 01.07. bis gestern.</summary>
    public IReadOnlyList<DailyStationKpi> DailyKpis => _dailyKpis.Value;

    public ScenarioDefinition Scenario { get; }

    public HistoryGenerator Generator { get; }

    public IReadOnlyList<HistoryRecord> Records { get; }

    public TimeSpan GenerationTime { get; }

    public CostRateTable Costs { get; }

    public ShiftCalendar Calendar => Scenario.Calendar;

    public DateTimeOffset Now => Scenario.DemoTimeUtc;

    public LocalClock At => new(Calendar);

    public KpiBlock Block(ProductionPeriod period, DateTimeOffset? until = null)
    {
        var builder = new KpiBlockBuilder(period, Costs);
        builder.AddRange(Records);
        var end = until ?? (period.EndUtc < Now ? period.EndUtc : Now);
        return builder.Snapshot(end);
    }

    /// <summary>Werksblock einer Woche: Halle A gerechnet (Schichten der Art, falls angegeben) plus Hallen B/C.</summary>
    public KpiBlock PlantBlock(int weekDay, Shift? shift = null)
    {
        var week = Week(weekDay);
        var until = week.EndUtc < Now ? week.EndUtc : Now;
        var hallA = Calendar.ProductionShiftsBetween(week.StartUtc, until)
            .Where(p => shift is null || Calendar.ShiftOf(p.StartUtc) == shift)
            .Select(p => Block(p));
        return KpiBlock.Combine(week.Key, [.. hallA, PlantComparison.Block(week.Key, shift)]);
    }

    public static readonly string[] Ovens = ["OF-01", "OF-02"];

    public static readonly string[] HallAAll = [.. OeeStations, .. Ovens];

    public static readonly string[] PlantOee = [.. OeeStations, PlantComparison.HallBStation, PlantComparison.HallCStation];

    public static readonly string[] PlantAll = [.. HallAAll, PlantComparison.HallBStation, PlantComparison.HallCStation];

    public ProductionPeriod Shift(int day, Shift shift) => Calendar.ShiftPeriod(new DateOnly(2026, 10, day), shift);

    public ProductionPeriod Day(int day) => Calendar.DayPeriod(new DateOnly(2026, 10, day));

    public ProductionPeriod Week(int day) => Calendar.WeekPeriod(new DateOnly(2026, 10, day));

    public IEnumerable<CycleRecord> Cycles(string stationId, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        Records.OfType<CycleRecord>().Where(c => c.StationId == stationId && c.TimestampUtc >= fromUtc && c.TimestampUtc < toUtc);
}

[CollectionDefinition(Name)]
public sealed class HistoryGroup : ICollectionFixture<HistoryFixture>
{
    public const string Name = "history";
}
