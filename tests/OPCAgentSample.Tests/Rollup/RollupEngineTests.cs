using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Tests.Server.Ingestion;

namespace OPCAgentSample.Tests.Rollup;

/// <summary>
/// Die Verdichtung in Redis gegen die Rechenfunktion der Kennzahlen-Bibliothek (Spezifikation Abschnitt 15): Jede Schicht stimmt mit
/// dem <see cref="KpiBlockBuilder"/> über die Rohdatensätze überein, und jede Periode ist die Summe ihrer Kinder.
/// </summary>
[Collection(RollupFixture.Name)]
public sealed class RollupEngineTests(RollupFixture fixture)
{
    private static readonly SharedHistory s_history = SharedHistory.Instance;

    private static readonly string[] s_stations = [.. PlantLayout.HallAStations.Select(s => s.Id)];

    [Fact]
    public async Task Every_shift_in_redis_equals_the_builder_over_the_generator_records()
    {
        var rollup = await fixture.GetIngestedAsync();
        var shifts = s_history.Calendar.ProductionShiftsBetween(s_history.Scenario.HistoryStartUtc, s_history.Now).ToList();

        Assert.NotEmpty(shifts);
        foreach (var shift in shifts)
        {
            var stored = await rollup.Reader.ReadBlockAsync(s_stations, RollupLevel.Shift, shift.Key);
            RollupAssert.Equivalent(s_history.Block(shift), stored.Block, shift.Key);
            Assert.Equal(shift.EndUtc > s_history.Now, stored.IsOpen);
        }
    }

    [Fact]
    public async Task Every_period_equals_the_sum_of_its_children()
    {
        var rollup = await fixture.GetIngestedAsync();
        var calendar = s_history.Calendar;
        var firstDay = s_history.Scenario.DailyHistoryStart;
        var lastDay = calendar.ProductionDayOf(s_history.Now);
        var stations = s_stations.Concat(["HALLE-B", "HALLE-C"]).ToList();

        var days = new List<DateOnly>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1)) days.Add(day);

        // Tage = Summe ihrer Schichten (Tage ohne Schichten sind direkt gelieferte Tageswerte).
        var dayTotals = new Dictionary<(string, string), StationTotals>();
        foreach (var day in days)
        {
            var dayKey = PeriodKeys.Day(day);
            var rows = await rollup.Reader.ReadCountersAsync(stations.Select(s => (s, RollupLevel.Day, dayKey)));
            foreach (var row in rows) dayTotals[(row.Station, dayKey)] = row.Totals;

            var shiftRows = await rollup.Reader.ReadCountersAsync(
                from station in stations from shift in Enum.GetValues<Shift>() select (station, RollupLevel.Shift, calendar.ShiftPeriod(day, shift).Key));
            foreach (var group in shiftRows.GroupBy(r => r.Station))
            {
                var sum = new StationTotals();
                foreach (var row in group) sum.Add(row.Totals);
                RollupAssert.Equivalent(sum, dayTotals[(group.Key, dayKey)], $"{group.Key} {dayKey}");
            }
        }

        // Woche, Monat, Quartal, Jahr = Summe ihrer Tage.
        var checkedParents = 0;
        foreach (var level in RollupLevels.AboveDay)
        {
            foreach (var group in days.GroupBy(d => PeriodKeys.Of(level, d, calendar)))
            {
                var rows = await rollup.Reader.ReadCountersAsync(stations.Select(s => (s, level, group.Key)));
                foreach (var row in rows)
                {
                    var sum = new StationTotals();
                    foreach (var day in group)
                    {
                        if (dayTotals.TryGetValue((row.Station, PeriodKeys.Day(day)), out var part)) sum.Add(part);
                    }

                    RollupAssert.Equivalent(sum, row.Totals, $"{row.Station} {level} {group.Key}");
                    checkedParents++;
                }
            }
        }

        Assert.True(checkedParents > 100, $"Nur {checkedParents} Eltern geprüft.");
    }

    [Fact]
    public async Task Week_42_in_redis_equals_the_reference_and_is_still_open()
    {
        var rollup = await fixture.GetIngestedAsync();

        var week = await rollup.Reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42");

        RollupAssert.Equivalent(s_history.Week42(), week.Block, "2026-W42");
        Assert.True(week.IsOpen);
        Assert.Equal(s_history.Now, week.Block.SourceUntilUtc);
        var oee = OeeCalculator.From(week.Block, SharedHistory.OeeStations);
        Assert.Equal(77, OeeResult.Percent(oee.Oee));
    }

    [Fact]
    public async Task Counter_halls_and_the_quarter_of_daily_history_are_stored_as_days()
    {
        var rollup = await fixture.GetIngestedAsync();

        var hallB = await rollup.Reader.ReadTotalsAsync("HALLE-B", RollupLevel.Day, "2026-10-13");
        var early = await rollup.Reader.ReadTotalsAsync("RT-03", RollupLevel.Day, "2026-08-03");
        var quarter = await rollup.Reader.ReadTotalsAsync("RT-03", RollupLevel.Quarter, "2026-Q3");

        Assert.NotNull(hallB);
        Assert.NotNull(early);
        Assert.NotNull(quarter);
        Assert.True(quarter.Figures.GoodPieces >= early.Figures.GoodPieces);
        Assert.Empty(early.ScrapByReason);
    }

    [Fact]
    public async Task Process_statistics_match_a_naive_calculation_on_the_raw_values()
    {
        var rollup = await fixture.GetIngestedAsync();
        var day = s_history.Calendar.DayPeriodOf(s_history.Now);
        var cycles = s_history.Records.OfType<CycleRecord>().Where(c => c.StationId == "RT-04" && day.Contains(c.TimestampUtc)).ToList();

        var quarterHours = await rollup.Reader.ReadStatsAsync("RT-04", SeriesNames.BuildUp, "15m", day.Key);

        Assert.Equal(cycles.Count, quarterHours.Values.Sum(s => s.Count));
        var first = cycles.Where(c => c.TimestampUtc < day.StartUtc.AddMinutes(15)).Select(c => c.PressForceBuildUpSeconds).ToList();
        var stat = quarterHours["0"];
        Assert.Equal(first.Count, stat.Count);
        Assert.Equal(first.Average(), stat.Mean, 9);
        Assert.Equal(first.Min(), stat.Min);
        Assert.Equal(first.Max(), stat.Max);

        var fine = await rollup.Reader.ReadStatsAsync("RT-04", SeriesNames.BuildUp, "1m", day.Key);
        Assert.Equal(cycles.Count, fine.Values.Sum(s => s.Count));
        var week = await rollup.Reader.ReadStatsAsync("RT-04", SeriesNames.BuildUp, "w", "-", ["2026-W42"]);
        var weekCycles = s_history.Records.OfType<CycleRecord>()
            .Count(c => c.StationId == "RT-04" && s_history.Calendar.WeekPeriodOf(s_history.Now).Contains(c.TimestampUtc));
        Assert.Equal(weekCycles, week["2026-W42"].Count);
    }

    [Fact]
    public async Task Tolerance_band_counts_from_the_histogram_equal_a_naive_count()
    {
        var rollup = await fixture.GetIngestedAsync();
        var week = s_history.Calendar.WeekPeriodOf(s_history.Now);
        var cycles = s_history.Records.OfType<CycleRecord>().Where(c => c.StationId == "RT-01" && week.Contains(c.TimestampUtc)).ToList();
        var naive = cycles.LongCount(c =>
        {
            var deviation = ((c.CycleTimeSeconds / c.TargetCycleSeconds) - 1) * 100;
            return deviation < -5 || deviation > 5;
        });

        var stats = await rollup.Reader.ReadStatsAsync("RT-01", SeriesNames.CycleDeviation, "w", "-", ["2026-W42"]);
        var spec = ProcessBands.CycleDeviationPercent;

        Assert.Equal(cycles.Count, stats["2026-W42"].Count);
        Assert.Equal(naive, spec.Outside(stats["2026-W42"].Hist));
    }

    [Fact]
    public async Task Scrap_by_reason_is_stored_with_the_shift_counters()
    {
        var rollup = await fixture.GetIngestedAsync();
        var shift = s_history.Shift(14, Shift.Night);
        var expected = s_history.Records.OfType<CycleRecord>()
            .Where(c => c.StationId == "RT-03" && shift.Contains(c.TimestampUtc) && c.Scrap is not null)
            .GroupBy(c => c.Scrap!.Value.ToString())
            .ToDictionary(g => g.Key, g => (long)g.Count());

        var stored = await rollup.Reader.ReadTotalsAsync("RT-03", RollupLevel.Shift, shift.Key);

        Assert.NotNull(stored);
        Assert.Equal(expected.OrderBy(p => p.Key), stored.ScrapByReason.OrderBy(p => p.Key));
    }

    [Fact]
    public async Task Events_streams_ring_buffer_and_trace_are_stored()
    {
        var rollup = await fixture.GetIngestedAsync();
        var from = s_history.Calendar.WeekPeriodOf(s_history.Now).StartUtc;

        var losses = await rollup.Reader.ReadLossesAsync(from, s_history.Now);
        var expected = s_history.Records.OfType<LossEvent>().GroupBy(l => l.Id).Select(g => g.Last())
            .Count(l => l.StartUtc < s_history.Now && (l.EndUtc is null || l.EndUtc > from));
        var cycles = await rollup.Reader.ReadRecentCyclesAsync("RT-03", s_history.Now);
        var state = await rollup.Reader.ReadStateAtAsync("RT-03", s_history.Now);
        var expectedState = s_history.Records.OfType<StateChange>().Last(s => s.StationId == "RT-03" && s.TimestampUtc <= s_history.Now);

        Assert.Equal(expected, losses.Count);
        Assert.Equal(Retention.RingBufferCycles, cycles.Count);
        Assert.Equal(s_history.Records.OfType<CycleRecord>().Last(c => c.StationId == "RT-03").CycleTimeSeconds, cycles[^1].CycleTimeSeconds);
        Assert.Equal(expectedState.State, state!.State);
        Assert.Equal(expectedState.OrderId, state.OrderId);
        Assert.Equal(s_history.Now, await rollup.Reader.ReadOverallWatermarkAsync());
    }

    [Fact]
    public async Task Order_and_mould_counters_equal_the_cycles()
    {
        var rollup = await fixture.GetIngestedAsync();
        var order = s_history.Records.OfType<CycleRecord>().Where(c => c.OrderId is not null).GroupBy(c => c.OrderId!).First();

        var stored = await rollup.Redis.Connection.GetDatabase().HashGetAsync(RollupKeys.OrderCount(order.Key), "c");

        Assert.Equal(order.Count(), (long)stored);
    }

    [Fact]
    public async Task Retention_stands_at_the_keys_and_the_ring_buffer_holds_exactly_1000_cycles()
    {
        var rollup = await fixture.GetIngestedAsync();
        var database = rollup.Redis.Connection.GetDatabase();
        var day = s_history.Calendar.DayPeriodOf(s_history.Now).Key;

        async Task<double> DaysLeftAsync(string key) => (await database.KeyTimeToLiveAsync(key))?.TotalDays ?? double.NaN;

        Assert.InRange(await DaysLeftAsync(RollupKeys.Counters("RT-03", RollupLevel.Shift, "2026-10-14N")), 99, 100);
        Assert.InRange(await DaysLeftAsync(RollupKeys.Counters("RT-03", RollupLevel.Day, "2026-10-14")), 729, 730);
        Assert.InRange(await DaysLeftAsync(RollupKeys.Counters("RT-03", RollupLevel.Week, "2026-W42")), 729, 730);
        Assert.InRange(await DaysLeftAsync(RollupKeys.Counters("RT-03", RollupLevel.Month, "2026-10")), 1824, 1825);
        Assert.InRange(await DaysLeftAsync(RollupKeys.ReasonDuration("RT-03", RollupLevel.Quarter, "2026-Q4")), 1824, 1825);
        Assert.InRange(await DaysLeftAsync(RollupKeys.Stats("RT-03", SeriesNames.CycleTime, "1m", day)), 2.9, 3);
        Assert.InRange(await DaysLeftAsync(RollupKeys.Stats("RT-03", SeriesNames.CycleTime, "15m", day)), 30.9, 31);
        Assert.InRange(await DaysLeftAsync(RollupKeys.LossStream("A")), 99, 100);
        Assert.Equal(Retention.RingBufferCycles, await database.StreamLengthAsync(RollupKeys.RingBuffer("RT-03")));
        Assert.False(await database.KeyExistsAsync(RollupKeys.Stats("OF-01", MinuteVariables.OccupiedPlaces, "1m", day)));
    }
}
