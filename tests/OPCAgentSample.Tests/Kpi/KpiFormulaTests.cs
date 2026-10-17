using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Kpi;

public sealed class KpiFormulaTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 15, 7, 42, 0, TimeSpan.Zero);

    [Fact]
    public void Oee_is_availability_times_performance_times_quality()
    {
        // 480 min Schicht, 30 min Rüsten (geplant), 45 min Störung, 5.400 Stück à 4 s, davon 54 Ausschuss
        var figures = Figures(scheduled: 480, plannedStop: 30, loss: 45, good: 5_346, scrap: 54, cycle: 4m);

        var oee = OeeCalculator.From(figures);

        Assert.Equal(405.0 / 450, oee.Availability, 6);
        Assert.Equal(5_400 * 4.0 / (405 * 60), oee.Performance, 6);
        Assert.Equal(5_346.0 / 5_400, oee.Quality, 6);
        Assert.Equal(5_346 * 4.0 / (450 * 60), oee.Oee, 6);
    }

    [Fact]
    public void Hall_oee_is_built_from_summed_figures_not_from_averaged_percentages()
    {
        var long_ = Figures(scheduled: 480, plannedStop: 0, loss: 0, good: 7_200, scrap: 0, cycle: 4m);
        var short_ = Figures(scheduled: 120, plannedStop: 0, loss: 60, good: 450, scrap: 0, cycle: 4m);

        var hall = OeeCalculator.From(long_.Add(short_)).Oee;
        var averaged = (OeeCalculator.From(long_).Oee + OeeCalculator.From(short_).Oee) / 2;

        Assert.Equal((7_200 + 450) * 4.0 / (600 * 60), hall, 6);
        Assert.NotEqual(averaged, hall, 3);
    }

    [Fact]
    public void Quality_over_different_units_is_weighted_by_target_time()
    {
        var discs = Figures(scheduled: 480, plannedStop: 0, loss: 0, good: 900, scrap: 100, cycle: 4m);
        var cartons = Figures(scheduled: 480, plannedStop: 0, loss: 0, good: 100, scrap: 0, cycle: 40m);

        var quality = OeeCalculator.From(discs.Add(cartons)).Quality;

        Assert.Equal((900 * 4.0) + (100 * 40.0), quality * ((1_000 * 4.0) + (100 * 40.0)), 6);
    }

    [Fact]
    public void Plan_fulfillment_and_linear_projection_to_shift_end()
    {
        var result = new PlanFulfillmentResult(11_150, 12_395m);

        Assert.Equal(0.8996, result.Ratio, 4);
        Assert.Equal(24_108, PlanFulfillment.ProjectToShiftEnd(11_150, TimeSpan.FromMinutes(222), TimeSpan.FromHours(8)), 0);
    }

    [Fact]
    public void Order_backlog_is_planned_progress_minus_produced()
    {
        var start = s_now.AddHours(-10);
        var backlog = PlanFulfillment.OrderBacklog(10_000, 4_000, start, start.AddHours(20), s_now);

        Assert.Equal(1_000, backlog, 6);
    }

    [Fact]
    public void Speed_loss_pieces_compare_target_and_actual_cycle_time()
    {
        var lost = PlanFulfillment.SpeedLossPieces(TimeSpan.FromMinutes(60), actualCycleSeconds: 4.6, targetCycleSeconds: 4.2);

        Assert.Equal(3_600 * ((1 / 4.2) - (1 / 4.6)), lost, 6);
    }

    [Fact]
    public void Material_reach_is_limited_by_processability_of_the_mixture()
    {
        var reach = MaterialReach.Compute("Mischung", level: 110, consumptionPerMinute: 1.57, s_now.AddMinutes(38), s_now);

        Assert.True(reach.LimitedByProcessability);
        Assert.Equal(TimeSpan.FromMinutes(38), reach.Reach);
        Assert.Equal(70, reach.ByLevel.TotalMinutes, 0);
    }

    [Fact]
    public void Mtbf_mttr_and_unplanned_share_follow_their_definitions()
    {
        var figures = Figures(scheduled: 1_440, plannedStop: 0, loss: 120, good: 1, scrap: 0, cycle: 1m) with
        {
            BreakdownCount = 3,
            BreakdownTime = TimeSpan.FromMinutes(120),
            MaintenanceTime = TimeSpan.FromMinutes(240),
        };

        var reliability = Reliability.From(figures);

        Assert.Equal(TimeSpan.FromMinutes(1_320 / 3.0), reliability.Mtbf);
        Assert.Equal(TimeSpan.FromMinutes(40), reliability.Mttr);
        Assert.Equal(1 / 3.0, reliability.UnplannedShare, 6);
    }

    [Fact]
    public void Mould_wear_and_projection_of_the_stroke_limit()
    {
        var wear = MouldWear.Compute(strokes: 9_400, strokeLimit: 10_000, strokesPerMinute: 3.4, s_now);

        Assert.Equal(0.94, wear.Wear, 6);
        Assert.Equal(s_now.AddMinutes(600 / 3.4), wear.LimitReachedUtc);
    }

    [Fact]
    public void Mould_forecast_counts_only_production_days()
    {
        var day = MouldWear.ForecastLimitDay(19_000, 35_900, 4_100, new DateOnly(2026, 10, 15), new ShiftCalendar());

        Assert.Equal(new DateOnly(2026, 10, 21), day);
    }

    [Fact]
    public void Pareto_sorts_by_duration_and_values_cost_with_the_station_rate()
    {
        var reasons = new Dictionary<string, ReasonFigures>
        {
            [LossReasons.MixtureWait] = new(TimeSpan.FromMinutes(30), 1),
            [LossReasons.FabricFeedFault] = new(TimeSpan.FromMinutes(60), 2),
        };
        var block = new KpiBlock("w", s_now, new Dictionary<string, StationBlock> { ["RT-03"] = new(ProductionFigures.Empty, reasons) });
        var rates = new CostRateTable(new Dictionary<string, decimal> { ["RT-03"] = 30m }, new Dictionary<string, decimal>());

        var pareto = DowntimePareto.Build(block, ["RT-03"], rates);

        Assert.Equal([LossReasons.FabricFeedFault, LossReasons.MixtureWait], pareto.Select(p => p.Reason));
        Assert.Equal(1_800m, pareto[0].CostEuro);
    }

    private static ProductionFigures Figures(double scheduled, double plannedStop, double loss, long good, long scrap, decimal cycle) =>
        ProductionFigures.Empty with
        {
            ScheduledTime = TimeSpan.FromMinutes(scheduled),
            PlannedStopTime = TimeSpan.FromMinutes(plannedStop),
            AvailabilityLossTime = TimeSpan.FromMinutes(loss),
            GoodPieces = good,
            ScrapPieces = scrap,
            IdealSeconds = (good + scrap) * cycle,
            GoodIdealSeconds = good * cycle,
        };
}
