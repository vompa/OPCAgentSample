using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Startzustand Do 15.10.2026, 09:42 (Spezifikation Abschnitt 7, Mockup GROUPS).</summary>
[Collection(HistoryGroup.Name)]
public sealed class StartStateTests(HistoryFixture fixture)
{
    private KpiBlock Today => fixture.Block(fixture.Shift(15, Shift.Early));

    [Theory]
    [InlineData("RT-01", 101, TrafficLight.Warning)]
    [InlineData("RT-02", 97, TrafficLight.Warning)]
    [InlineData("RT-03", 72, TrafficLight.Problem)]
    [InlineData("RT-04", 89, TrafficLight.Warning)]
    [InlineData("VP-01", 98, TrafficLight.Running)]
    [InlineData("VP-02", 64, TrafficLight.Warning)]
    public void Plan_fulfillment_and_traffic_light_per_station(string station, int percent, TrafficLight light)
    {
        var snapshot = fixture.Live.Station(station, fixture.Now);

        Assert.Equal(percent, OeeResult.Percent(PlanFulfillment.From(Today.Figures(station)).Ratio));
        Assert.Equal(percent, OeeResult.Percent(snapshot.PlanRatio));
        Assert.Equal(light, TrafficLightRules.Evaluate(snapshot.Signals()).Light);
    }

    [Theory]
    [InlineData("OF-01", 78, TrafficLight.Running, "09:50")]
    [InlineData("OF-02", 96, TrafficLight.Warning, "10:15")]
    public void Ovens(string oven, int occupancy, TrafficLight light, string nextRelease)
    {
        var snapshot = fixture.Live.Oven(oven, fixture.Now);

        Assert.Equal(occupancy, OeeResult.Percent(snapshot.OccupancySinceShiftStart));
        Assert.Equal(light, TrafficLightRules.Evaluate(snapshot.Signals()).Light);
        Assert.Equal(fixture.At.Oct(15, nextRelease), snapshot.NextUnloadUtc);
        Assert.Equal(OperatingState.Running, snapshot.State);
    }

    [Fact]
    public void Status_counters_are_one_problem_five_warnings_two_running()
    {
        var lights = HistoryFixture.OeeStations.Select(s => TrafficLightRules.Evaluate(fixture.Live.Station(s, fixture.Now).Signals()).Light)
            .Concat(HistoryFixture.Ovens.Select(o => TrafficLightRules.Evaluate(fixture.Live.Oven(o, fixture.Now).Signals()).Light))
            .ToList();

        Assert.Equal(1, lights.Count(l => l == TrafficLight.Problem));
        Assert.Equal(5, lights.Count(l => l == TrafficLight.Warning));
        Assert.Equal(2, lights.Count(l => l == TrafficLight.Running));
    }

    [Fact]
    public void Presses_total_and_projection_to_shift_end()
    {
        var presses = Today.Figures(HistoryFixture.Presses);
        var elapsed = fixture.Now - fixture.Shift(15, Shift.Early).StartUtc;
        var projection = PlanFulfillment.ProjectToShiftEnd(presses.TotalPieces, elapsed, TimeSpan.FromHours(8));
        var shiftPlan = HistoryFixture.Presses.Sum(s => fixture.Live.Station(s, fixture.Now).PlannedShiftQuantity);

        Assert.Equal(11_150, presses.TotalPieces);
        Assert.Equal(12_400, Math.Round(presses.PlannedQuantity / 100m) * 100);
        Assert.Equal(24_100, Math.Round(projection / 100) * 100);
        Assert.Equal(26_800, shiftPlan);
        Assert.Equal(90, OeeResult.Percent(projection / shiftPlan));
    }

    [Fact]
    public void RT04_runs_with_longer_cycle_time_and_no_standstill()
    {
        var snapshot = fixture.Live.Station("RT-04", fixture.Now);
        var figures = Today.Figures("RT-04");

        Assert.Equal(4.6, Math.Round(snapshot.CycleTimeSeconds!.Value, 1));
        Assert.Equal(4.2, snapshot.TargetCycleSeconds);
        Assert.Equal(9, OeeResult.Percent(snapshot.CycleDeviation!.Value));
        Assert.Equal(1.1, Math.Round(snapshot.BuildUpSeconds!.Value, 1));
        Assert.Equal(2_760, figures.TotalPieces);
        Assert.Equal(3_100, Math.Round(figures.PlannedQuantity / 100m) * 100);
        Assert.Equal(TimeSpan.Zero, figures.AvailabilityLossTime + figures.MinorStopTime);
        Assert.Equal("A-180-A30", snapshot.ArticleId);
    }

    [Theory]
    [InlineData("RT-01", 113.4, "A-125-A60")]
    [InlineData("RT-02", 113.6, "A-125-A60")]
    public void Fill_weight_moving_average_of_the_drifting_tables(string station, double grams, string article)
    {
        var snapshot = fixture.Live.Station(station, fixture.Now);

        Assert.Equal(grams, Math.Round(snapshot.FillWeightMeanGrams!.Value, 1));
        Assert.Equal(112, snapshot.FillWeightTargetGrams);
        Assert.Equal(article, snapshot.ArticleId);
        Assert.Equal("M-2410-17", snapshot.MixtureBatchId);
    }

    [Fact]
    public void RT01_mixture_is_processable_until_1020_while_the_level_lasts_about_70_minutes()
    {
        var mixture = fixture.Live.Station("RT-01", fixture.Now).Materials.Single(m => m.Material == MinuteVariables.MixtureLevelKg);
        var reach = mixture.Reach(fixture.Now);

        Assert.Equal(fixture.At.Oct(15, "10:20"), mixture.ProcessableUntilUtc);
        Assert.True(reach.LimitedByProcessability);
        Assert.Equal(38, reach.Reach.TotalMinutes, 0);
        Assert.InRange(reach.ByLevel.TotalMinutes, 65, 75);
    }

    [Fact]
    public void RT02_mould_at_94_percent_reaches_its_limit_around_1240_and_fabric_lasts_55_minutes()
    {
        var snapshot = fixture.Live.Station("RT-02", fixture.Now);
        var wear = snapshot.Mould!.Wear(fixture.Now);
        var fabric = snapshot.Materials.Single(m => m.Material == MinuteVariables.FabricMagazine).Reach(fixture.Now);

        Assert.Equal("F-125-07", snapshot.Mould.MouldId);
        Assert.Equal(94, OeeResult.Percent(wear.Wear));
        Assert.InRange(wear.LimitReachedUtc!.Value, fixture.At.Oct(15, "12:25"), fixture.At.Oct(15, "12:55"));
        Assert.Equal(55, fabric.Reach.TotalMinutes, 0);
    }

    [Fact]
    public void RT03_runs_order_4711_after_the_gripper_fault()
    {
        var snapshot = fixture.Live.Station("RT-03", fixture.Now);

        Assert.Equal(OperatingState.Running, snapshot.State);
        Assert.Equal(fixture.At.Oct(15, "07:05"), snapshot.StateSinceUtc);
        Assert.Equal("4711", snapshot.OrderId);
        Assert.Equal("A-125-A46", snapshot.ArticleId);
    }

    [Fact]
    public void VP01_packed_1240_cartons_and_cartons_last_1_hour_40()
    {
        var snapshot = fixture.Live.Station("VP-01", fixture.Now);
        var cartons = snapshot.Materials.Single(m => m.Material == MinuteVariables.CartonMagazine).Reach(fixture.Now);

        Assert.Equal(1_240, snapshot.PiecesSinceShiftStart);
        Assert.Equal("K-125-25", snapshot.ArticleId);
        Assert.InRange(cartons.Reach.TotalMinutes, 95, 105);
    }

    [Fact]
    public void VP02_waits_for_upstream_since_0905()
    {
        var snapshot = fixture.Live.Station("VP-02", fixture.Now);

        Assert.Equal(OperatingState.WaitingForUpstream, snapshot.State);
        Assert.Equal(fixture.At.Oct(15, "09:05"), snapshot.StateSinceUtc);
        Assert.Equal("K-180-10", snapshot.ArticleId);
    }

    [Fact]
    public void Buffer_holds_42_plates_with_45_minutes_mean_wait()
    {
        var buffer = fixture.Live.Buffer(fixture.Now);

        Assert.Equal(42, buffer.Plates);
        Assert.Equal(45, buffer.MeanWaitMinutes);
    }

    [Fact]
    public void Hall_climate_is_23_4_degrees_48_percent_in_range_and_rose_1_2_degrees_since_0600()
    {
        var climate = fixture.Live.Climate(fixture.Now);

        Assert.Equal(23.4, Math.Round(climate.Celsius, 1));
        Assert.Equal(48, Math.Round(climate.Humidity));
        Assert.True(climate.InRange);
        Assert.Equal(1.2, Math.Round(climate.Celsius - climate.CelsiusAtShiftStart, 1));
    }
}
