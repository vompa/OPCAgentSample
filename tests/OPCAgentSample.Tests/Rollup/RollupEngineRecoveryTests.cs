using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Tests.Server.Ingestion;

namespace OPCAgentSample.Tests.Rollup;

/// <summary>Wiederaufnahme nach einem Neustart, nachgetragene Gründe, Redis geleert und die Zeitumstellung (25-Stunden-Tag).</summary>
[Collection(RollupFixture.Name)]
public sealed class RollupEngineRecoveryTests(RollupFixture fixture)
{
    private static readonly SharedHistory s_history = SharedHistory.Instance;

    private static readonly string[] s_stations = [.. PlantLayout.HallAStations.Select(s => s.Id)];

    private static DateTimeOffset RawStart => s_history.Scenario.HistoryStartUtc;

    [Fact]
    public async Task Resuming_the_running_shift_after_a_restart_gives_the_same_numbers_as_one_pass()
    {
        var reference = await fixture.GetIngestedAsync();
        var shift = s_history.Calendar.ShiftPeriodOf(s_history.Now);
        var midShift = s_history.Calendar.ToUtc(new DateTime(2026, 10, 15, 8, 17, 30));
        await using var redis = await RollupFixture.NewRedisAsync();

        using (var first = RollupIngest.NewEngine(redis.Connection))
        {
            await RollupIngest.RunAsync(first, RawStart, midShift);
        }

        using (var restarted = RollupIngest.NewEngine(redis.Connection))
        {
            await RollupIngest.RunAsync(restarted, midShift, s_history.Now);
        }

        var reader = new RollupReader(redis.Connection);
        foreach (var (level, key) in new[] { (RollupLevel.Shift, shift.Key), (RollupLevel.Day, "2026-10-15"), (RollupLevel.Week, "2026-W42") })
        {
            var expected = await reference.Reader.ReadBlockAsync(s_stations, level, key);
            var actual = await reader.ReadBlockAsync(s_stations, level, key);
            RollupAssert.Equivalent(expected.Block, actual.Block, key);
            Assert.Equal(expected.Block.SourceUntilUtc, actual.Block.SourceUntilUtc);
            Assert.Equal(expected.IsOpen, actual.IsOpen);
        }

        var day = "2026-10-15";
        var expectedFine = await reference.Reader.ReadStatsAsync("RT-04", SeriesNames.CycleTime, "1m", day);
        var actualFine = await reader.ReadStatsAsync("RT-04", SeriesNames.CycleTime, "1m", day);
        Assert.Equal(expectedFine.Keys.Order(), actualFine.Keys.Order());
        Assert.All(expectedFine, p => Assert.Equal(p.Value.Count, actualFine[p.Key].Count));
        var expectedWeek = (await reference.Reader.ReadStatsAsync("RT-04", SeriesNames.CycleTime, "w", "-", ["2026-W42"]))["2026-W42"];
        var actualWeek = (await reader.ReadStatsAsync("RT-04", SeriesNames.CycleTime, "w", "-", ["2026-W42"]))["2026-W42"];
        Assert.Equal(expectedWeek.Count, actualWeek.Count);
        Assert.Equal(expectedWeek.Sum, actualWeek.Sum, 6);
        Assert.Equal(expectedWeek.Hist, actualWeek.Hist);
        Assert.Equal(await reference.Reader.ReadOverallWatermarkAsync(), await reader.ReadOverallWatermarkAsync());
    }

    [Fact]
    public async Task A_reason_entered_later_recomputes_only_the_event_dependent_part_of_the_earlier_shift()
    {
        var shift = s_history.Shift(14, Shift.Night);
        var loss = s_history.Records.OfType<LossEvent>()
            .Where(l => l.EndUtc is not null && l.Category == LossCategory.Breakdown && shift.Contains(l.StartUtc) && l.StationId == "RT-03")
            .OrderByDescending(l => l.EndUtc - l.StartUtc)
            .First();
        var later = s_history.Calendar.ToUtc(new DateTime(2026, 10, 15, 6, 0, 0));
        var corrected = loss with { Reason = LossReasons.Unspecified, Cause = "nachgetragen", TimestampUtc = later.AddMinutes(1) };
        await using var redis = await RollupFixture.NewRedisAsync();
        using var engine = RollupIngest.NewEngine(redis.Connection);
        await RollupIngest.RunAsync(engine, RawStart, later.AddMinutes(10));

        var before = await new RollupReader(redis.Connection).ReadTotalsAsync("RT-03", RollupLevel.Shift, shift.Key);
        await engine.ApplyAsync("test", new TimeWindow(later.AddMinutes(10), later.AddMinutes(20)), [corrected]);
        var after = await new RollupReader(redis.Connection).ReadTotalsAsync("RT-03", RollupLevel.Shift, shift.Key);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(before.Reasons.ContainsKey(loss.Reason));
        Assert.False(after.Reasons.ContainsKey(loss.Reason) && after.Reasons[loss.Reason] == before.Reasons[loss.Reason]);
        Assert.Equal(before.Figures.GoodPieces, after.Figures.GoodPieces);
        Assert.Equal(before.Figures.ScrapPieces, after.Figures.ScrapPieces);
        Assert.Equal(before.ScrapByReason.OrderBy(p => p.Key), after.ScrapByReason.OrderBy(p => p.Key));

        var builder = new KpiBlockBuilder(shift, s_history.Costs);
        builder.AddRange(s_history.Records);
        builder.Add(corrected);
        var expected = builder.Snapshot(shift.EndUtc);
        RollupAssert.Equivalent(expected.Stations["RT-03"].Figures, after.Figures, "RT-03 nach Nachtrag");
        Assert.Equal(
            expected.Stations["RT-03"].Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)),
            after.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)));

        var week = await new RollupReader(redis.Connection).ReadTotalsAsync("RT-03", RollupLevel.Week, "2026-W42");
        Assert.NotNull(week);
        Assert.True(week.Reasons.ContainsKey(LossReasons.Unspecified));
    }

    [Fact]
    public async Task After_a_flush_a_new_run_from_the_start_rebuilds_the_same_state()
    {
        var reference = await fixture.GetIngestedAsync();
        await using var redis = await RollupFixture.NewRedisAsync();
        using var engine = RollupIngest.NewEngine(redis.Connection);
        var cut = s_history.Calendar.ToUtc(new DateTime(2026, 10, 7, 12, 0, 0));
        await RollupIngest.RunAsync(engine, RawStart, cut);

        await redis.FlushAsync();
        await RollupIngest.RunAsync(engine, RawStart, s_history.Now);

        var reader = new RollupReader(redis.Connection);
        var expected = await reference.Reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42");
        var actual = await reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42");
        RollupAssert.Equivalent(expected.Block, actual.Block, "2026-W42");
        var week41 = await reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W41");
        RollupAssert.Equivalent((await reference.Reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W41")).Block, week41.Block, "2026-W41");
    }

    [Fact]
    public async Task The_day_of_the_clock_change_has_25_hours_and_100_quarter_hours()
    {
        var calendar = s_history.Calendar;
        var day = new DateOnly(2026, 10, 24);
        var bounds = PeriodBounds.Of(RollupLevel.Day, PeriodKeys.Day(day), calendar);
        Assert.Equal(TimeSpan.FromHours(25), bounds.Duration);

        var lastQuarter = bounds.EndUtc.AddMinutes(-15);
        var shift = calendar.ShiftPeriodOf(lastQuarter);
        Assert.Equal(PeriodKeys.Day(day) + "N", shift.Key);
        await using var redis = await RollupFixture.NewRedisAsync();
        using var engine = RollupIngest.NewEngine(redis.Connection);
        var start = lastQuarter.AddMinutes(-1);
        var records = new HistoryRecord[]
        {
            new MinuteValue("OF-01", start, MinuteVariables.OccupiedPlaces, 3),
            new MinuteValue("OF-01", lastQuarter, MinuteVariables.OccupiedPlaces, 4),
            new MinuteValue("OF-01", lastQuarter.AddMinutes(14), MinuteVariables.OccupiedPlaces, 5),
        };

        await engine.ApplyAsync("test", new TimeWindow(start, shift.EndUtc), records);

        var reader = new RollupReader(redis.Connection);
        var stats = await reader.ReadStatsAsync("OF-01", MinuteVariables.OccupiedPlaces, "15m", PeriodKeys.Day(day));
        Assert.Equal(["98", "99"], stats.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(1, stats["98"].Count);
        Assert.Equal(2, stats["99"].Count);
        Assert.Equal(4.5, stats["99"].Mean);
        Assert.Equal(5, stats["99"].Max);
        var dayStat = (await reader.ReadStatsAsync("OF-01", MinuteVariables.OccupiedPlaces, "d", "2026", [PeriodKeys.Day(day)]))[PeriodKeys.Day(day)];
        Assert.Equal(3, dayStat.Count);
    }

    [Fact]
    public void Period_bounds_are_contiguous_across_the_clock_change_and_the_ends_of_month_quarter_and_year()
    {
        var calendar = s_history.Calendar;
        foreach (var (level, key) in new[] { (RollupLevel.Month, "2026-10"), (RollupLevel.Quarter, "2026-Q4"), (RollupLevel.Year, "2026") })
        {
            var bounds = PeriodBounds.Of(level, key, calendar);
            var days = new List<TimeWindow>();
            for (var day = DateOnly.FromDateTime(calendar.ToLocal(bounds.StartUtc).DateTime); calendar.DayPeriod(day).StartUtc < bounds.EndUtc; day = day.AddDays(1))
            {
                var period = calendar.DayPeriod(day);
                days.Add(new TimeWindow(period.StartUtc, period.EndUtc));
            }

            Assert.Equal(bounds.StartUtc, days[0].StartUtc);
            Assert.Equal(bounds.EndUtc, days[^1].EndUtc);
            for (var i = 1; i < days.Count; i++) Assert.Equal(days[i - 1].EndUtc, days[i].StartUtc);
        }

        Assert.Equal(TimeSpan.FromDays(7), PeriodBounds.Of(RollupLevel.Week, "2026-W42", calendar).Duration);
        Assert.Equal(TimeSpan.FromDays(7) + TimeSpan.FromHours(1), PeriodBounds.Of(RollupLevel.Week, "2026-W43", calendar).Duration);
        var shiftBounds = PeriodBounds.Of(RollupLevel.Shift, "2026-10-14N", calendar);
        Assert.Equal(calendar.ShiftPeriod(new DateOnly(2026, 10, 14), Shift.Night).StartUtc, shiftBounds.StartUtc);
    }

    [Fact]
    public void Period_keys_follow_the_production_day()
    {
        var calendar = s_history.Calendar;
        var day = new DateOnly(2026, 12, 31);

        Assert.Equal("2026-12", PeriodKeys.Of(RollupLevel.Month, day, calendar));
        Assert.Equal("2026-Q4", PeriodKeys.Of(RollupLevel.Quarter, day, calendar));
        Assert.Equal("2026", PeriodKeys.Of(RollupLevel.Year, day, calendar));
        Assert.Equal("2026-W53", PeriodKeys.Of(RollupLevel.Week, day, calendar));
        Assert.Equal(new DateOnly(2026, 10, 14), PeriodKeys.DayOfShift("2026-10-14N"));
    }
}
