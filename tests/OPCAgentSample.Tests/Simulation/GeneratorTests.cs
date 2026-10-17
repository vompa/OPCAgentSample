using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Reproduzierbarkeit, Laufzeit, Umfang und Live-Fortsetzung des Generators.</summary>
[Collection(HistoryGroup.Name)]
public sealed class GeneratorTests(HistoryFixture fixture)
{
    private const int OtherSeed = 77;

    [Fact]
    public void Same_seed_gives_the_same_checksum()
    {
        var again = HistoryChecksum.Of(new HistoryGenerator(HallAScenario.Create()).GenerateUntilDemo());

        Assert.Equal(HistoryChecksum.Of(fixture.Records).Value, again.Value);
        Assert.Equal(fixture.Records.Count, again.Count);
    }

    [Fact]
    public void Other_seed_changes_the_noise_but_not_the_script_sums_or_the_start_state()
    {
        var other = new HistoryGenerator(HallAScenario.Create(), OtherSeed).GenerateUntilDemo().ToList();
        var week = fixture.Week(14);

        Assert.NotEqual(HistoryChecksum.Of(fixture.Records).Value, HistoryChecksum.Of(other).Value);
        Assert.Equal(ScriptedSums(fixture.Records, week), ScriptedSums(other, week));
        Assert.Equal(Pieces(fixture.Records, fixture.Shift(15, Shift.Early)), Pieces(other, fixture.Shift(15, Shift.Early)));
        Assert.Equal(Pieces(fixture.Records, fixture.Shift(14, Shift.Night)), Pieces(other, fixture.Shift(14, Shift.Night)));
    }

    [Fact]
    public void A_window_is_an_exact_slice_of_the_full_run()
    {
        var split = fixture.At.Oct(14, "17:23");
        var parts = fixture.Generator.Generate(fixture.Scenario.HistoryStartUtc, split)
            .Concat(fixture.Generator.Generate(split, fixture.Now));

        Assert.Equal(HistoryChecksum.Of(fixture.Records).Value, HistoryChecksum.Of(parts).Value);
    }

    [Fact]
    public void Full_run_until_demo_counts_every_cycle_value()
    {
        var cycles = fixture.Records.OfType<CycleRecord>().ToList();
        var minuteValues = fixture.Records.Count(r => r is MinuteValue);

        // Keine Laufzeitprüfung: sie hängt von der Last der Gesamtsuite ab (einzeln ca. 2,5 s)
        Assert.InRange(cycles.Count, 500_000, 700_000);
        Assert.InRange(minuteValues, 800_000, 1_000_000);
        Assert.Equal(cycles.Count, cycles.Select(c => (c.StationId, c.TimestampUtc)).Distinct().Count());
        Assert.Equal(fixture.Records.OrderBy(r => r.TimestampUtc).Select(r => r.TimestampUtc), fixture.Records.Select(r => r.TimestampUtc));
        Assert.All(fixture.Records, r => Assert.True(r.TimestampUtc >= fixture.Scenario.HistoryStartUtc && r.TimestampUtc < fixture.Now));
    }

    [Fact]
    public void Calibrated_utilizations_stay_plausible()
    {
        var segments = fixture.Generator.Stations
            .SelectMany(s => s.RunsBetween(fixture.Scenario.HistoryStartUtc, fixture.Now).SelectMany(r => r.Segments.Select(seg => (s.StationId, seg))))
            .ToList();

        Assert.All(segments, x => Assert.InRange(x.seg.Utilization, 0.6, 1.0));
    }

    [Fact]
    public void Completed_orders_match_their_master_quantity()
    {
        var events = fixture.Records.OfType<OrderEvent>().ToList();
        foreach (var completed in events.Where(e => e.Kind == OrderEventKind.Completed))
        {
            Assert.Equal(fixture.Scenario.Order(completed.OrderId).Quantity, completed.Quantity);
            Assert.Contains(events, e => e.OrderId == completed.OrderId && e.Kind == OrderEventKind.QuantityReached);
        }

        Assert.Equal(8_400, events.Single(e => e.OrderId == "4702" && e.Kind == OrderEventKind.Completed).Quantity);
        Assert.Equal(fixture.At.Oct(15, "08:55"), events.Single(e => e.OrderId == "4702" && e.Kind == OrderEventKind.Completed).TimestampUtc);
    }

    [Fact]
    public void Live_continuation_lets_the_RT04_drift_grow_and_VP02_resume_at_1040()
    {
        var later = fixture.At.Oct(15, "10:42");
        var rt04 = fixture.Live.Station("RT-04", later);
        var vp02 = fixture.Live.Station("VP-02", fixture.At.Oct(15, "10:45"));
        var cycles = fixture.Generator.Cycles("RT-04", fixture.Now, later).ToList();

        Assert.True(rt04.BuildUpSeconds > 1.3);
        Assert.True(rt04.CycleTimeSeconds > 4.8);
        Assert.NotEqual(OperatingState.WaitingForUpstream, vp02.State);
        Assert.InRange(cycles.Count, 600, 800);
        Assert.All(cycles, c => Assert.True(c.TimestampUtc >= fixture.Now && c.TimestampUtc < later));
    }

    [Fact]
    public void Daily_kpis_cover_a_quarter_and_raw_days_equal_the_kpi_library()
    {
        var days = fixture.DailyKpis;
        var stations = DailyKpiHistory.Stations(fixture.Scenario);
        var wednesday = days.Where(d => d.Day == new DateOnly(2026, 10, 14) && HistoryFixture.OeeStations.Contains(d.StationId));

        Assert.Equal(new DateOnly(2026, 7, 1), days.Min(d => d.Day));
        Assert.Equal(new DateOnly(2026, 10, 14), days.Max(d => d.Day));
        Assert.Equal(106 * stations.Count, days.Count);
        Assert.Equal(78, OeeResult.Percent(OeeCalculator.From(ProductionFigures.Sum(wednesday.Select(d => d.Figures))).Oee));
        Assert.Equal(fixture.Block(fixture.Day(14)).Figures("RT-03"), days.Single(d => d.Day == new DateOnly(2026, 10, 14) && d.StationId == "RT-03").Figures);
    }

    private static List<(string, double, int)> ScriptedSums(IEnumerable<HistoryRecord> records, ProductionPeriod week) =>
        [.. EventKpis.Latest(records.OfType<LossEvent>(), e => e.Id)
            .Where(e => !e.Id.StartsWith("K-", StringComparison.Ordinal) && e.StartUtc >= week.StartUtc && e.EndUtc is not null)
            .GroupBy(e => e.Reason)
            .Select(g => (g.Key, g.Sum(e => (e.EndUtc!.Value - e.StartUtc).TotalMinutes), g.Count()))
            .OrderBy(x => x.Key, StringComparer.Ordinal)];

    private static Dictionary<string, int> Pieces(IEnumerable<HistoryRecord> records, ProductionPeriod period) =>
        records.OfType<CycleRecord>().Where(c => period.Contains(c.TimestampUtc))
            .GroupBy(c => c.StationId).ToDictionary(g => g.Key, g => g.Count());
}
