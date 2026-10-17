using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Kpi;

public sealed class KpiBlockBuilderTests
{
    private static readonly DateTimeOffset s_shiftStart = new(2026, 10, 14, 4, 0, 0, TimeSpan.Zero);
    private static readonly ProductionPeriod s_shift = new(PeriodKind.Shift, "2026-10-14F", s_shiftStart, s_shiftStart.AddHours(8));
    private static readonly CostRateTable s_costs = new(
        new Dictionary<string, decimal> { ["RT-01"] = 10m },
        new Dictionary<string, decimal> { ["A"] = 2m });

    [Fact]
    public void Counts_pieces_ideal_time_and_scrap_cost_inside_the_period()
    {
        var builder = new KpiBlockBuilder(s_shift, s_costs);
        builder.Add(Plan());
        builder.Add(Cycle(s_shiftStart.AddMinutes(1), scrap: null));
        builder.Add(Cycle(s_shiftStart.AddMinutes(2), ScrapReason.Weight));
        builder.Add(Cycle(s_shiftStart.AddHours(9), scrap: null));

        var figures = builder.Snapshot(s_shift.EndUtc).Figures("RT-01");

        Assert.Equal(1, figures.GoodPieces);
        Assert.Equal(1, figures.ScrapPieces);
        Assert.Equal(8m, figures.IdealSeconds);
        Assert.Equal(4m, figures.GoodIdealSeconds);
        Assert.Equal(2m, figures.ScrapCostEuro);
        Assert.Equal(TimeSpan.FromHours(8), figures.ScheduledTime);
        Assert.Equal(6_000m, figures.PlannedQuantity);
    }

    [Fact]
    public void Planned_quantity_and_schedule_grow_pro_rata_until_the_watermark()
    {
        var builder = new KpiBlockBuilder(s_shift, s_costs);
        builder.Add(Plan());

        var figures = builder.Snapshot(s_shiftStart.AddHours(2)).Figures("RT-01");

        Assert.Equal(TimeSpan.FromHours(2), figures.ScheduledTime);
        Assert.Equal(1_500m, figures.PlannedQuantity);
    }

    [Fact]
    public void Open_event_runs_until_watermark_and_end_version_replaces_it()
    {
        var builder = new KpiBlockBuilder(s_shift, s_costs);
        builder.Add(Plan());
        var start = s_shiftStart.AddMinutes(30);
        builder.Add(Loss("L1", LossCategory.Breakdown, start, null, start));

        var open = builder.Snapshot(start.AddMinutes(10)).Figures("RT-01");
        Assert.Equal(TimeSpan.FromMinutes(10), open.AvailabilityLossTime);

        builder.Add(Loss("L1", LossCategory.Breakdown, start, start.AddMinutes(25), start.AddMinutes(25)));
        var closed = builder.Snapshot(start.AddMinutes(60)).Figures("RT-01");
        Assert.Equal(TimeSpan.FromMinutes(25), closed.AvailabilityLossTime);
        Assert.Equal(1, closed.BreakdownCount);
    }

    [Fact]
    public void End_version_outside_the_period_removes_a_previously_open_event()
    {
        var later = new ProductionPeriod(PeriodKind.Shift, "2026-10-14S", s_shift.EndUtc, s_shift.EndUtc.AddHours(8));
        var builder = new KpiBlockBuilder(later, s_costs);
        var start = s_shiftStart.AddMinutes(30);
        builder.Add(Loss("L1", LossCategory.Breakdown, start, null, start));
        builder.Add(Loss("L1", LossCategory.Breakdown, start, start.AddMinutes(20), start.AddMinutes(20)));

        Assert.Empty(builder.Snapshot(later.EndUtc).Reasons(["RT-01"]));
    }

    [Fact]
    public void Event_across_the_shift_boundary_is_clipped_and_counted_where_it_starts()
    {
        var next = new ProductionPeriod(PeriodKind.Shift, "2026-10-14S", s_shift.EndUtc, s_shift.EndUtc.AddHours(8));
        var start = s_shift.EndUtc.AddMinutes(-48);
        var loss = Loss("L1", LossCategory.Breakdown, start, start.AddMinutes(113), start.AddMinutes(113));
        var first = Build(s_shift, loss, Plan());
        var second = Build(next, loss, Plan(next));

        Assert.Equal(new ReasonFigures(TimeSpan.FromMinutes(48), 1), first.Reasons(["RT-01"])[LossReasons.FabricFeedFault]);
        Assert.Equal(new ReasonFigures(TimeSpan.FromMinutes(65), 0), second.Reasons(["RT-01"])[LossReasons.FabricFeedFault]);
    }

    [Fact]
    public void Planned_stops_reduce_planning_time_and_minor_stops_stay_in_run_time()
    {
        var builder = new KpiBlockBuilder(s_shift, s_costs);
        builder.Add(Plan());
        var t = s_shiftStart.AddHours(1);
        builder.Add(Loss("S", LossCategory.Setup, t, t.AddMinutes(30), t.AddMinutes(30)));
        builder.Add(Loss("U", LossCategory.SetupOverrun, t.AddMinutes(30), t.AddMinutes(48), t.AddMinutes(48)));
        builder.Add(Loss("K", LossCategory.MinorStop, t.AddHours(2), t.AddHours(2).AddMinutes(2), t.AddHours(2).AddMinutes(2)));
        builder.Add(Loss("O", LossCategory.Overproduction, t.AddHours(3), t.AddHours(4), t.AddHours(4), pieces: 900));

        var figures = builder.Snapshot(s_shift.EndUtc).Figures("RT-01");

        Assert.Equal(TimeSpan.FromMinutes(450), figures.PlanningTime);
        Assert.Equal(TimeSpan.FromMinutes(18), figures.AvailabilityLossTime);
        Assert.Equal(TimeSpan.FromMinutes(2), figures.MinorStopTime);
        Assert.Equal(TimeSpan.FromMinutes(432), figures.RunTime);
        Assert.Equal(900, figures.OverproductionPieces);
    }

    [Fact]
    public void Blocks_combine_by_addition()
    {
        var next = new ProductionPeriod(PeriodKind.Shift, "2026-10-14S", s_shift.EndUtc, s_shift.EndUtc.AddHours(8));
        var a = Build(s_shift, Plan(), Cycle(s_shiftStart.AddMinutes(5), null));
        var b = Build(next, Plan(next), Cycle(next.StartUtc.AddMinutes(5), ScrapReason.Label));

        var day = KpiBlock.Combine("2026-10-14", [a, b]).Figures("RT-01");

        Assert.Equal(TimeSpan.FromHours(16), day.ScheduledTime);
        Assert.Equal(1, day.GoodPieces);
        Assert.Equal(1, day.ScrapPieces);
        Assert.Equal(12_000m, day.PlannedQuantity);
    }

    private static KpiBlock Build(ProductionPeriod period, params HistoryRecord[] records)
    {
        var builder = new KpiBlockBuilder(period, s_costs);
        builder.AddRange(records);
        return builder.Snapshot(period.EndUtc);
    }

    private static ShiftPlanRecord Plan(ProductionPeriod? period = null)
    {
        var p = period ?? s_shift;
        return new ShiftPlanRecord("RT-01", p.StartUtc, p.Key, p.StartUtc, p.EndUtc, 6_000);
    }

    private static CycleRecord Cycle(DateTimeOffset at, ScrapReason? scrap) =>
        new("RT-01", at, "4711", "A", "M", 4.1, 4.0, 420, 0.8, 112, 1.0, scrap);

    private static LossEvent Loss(string id, LossCategory category, DateTimeOffset start, DateTimeOffset? end, DateTimeOffset at, int pieces = 0) =>
        new("RT-01", at, id, Assemblies.FabricFeed2, category, LossReasons.FabricFeedFault, "Test", start, end, pieces, true, null);
}
