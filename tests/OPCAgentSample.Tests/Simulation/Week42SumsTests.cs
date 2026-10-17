using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Summen KW 42 Halle A (Spezifikation Abschnitt 6) und OEE gestern; Referenz „Zeitverluste“ und „Tagesbesprechung“.</summary>
[Collection(HistoryGroup.Name)]
public sealed class Week42SumsTests(HistoryFixture fixture)
{
    private IReadOnlyList<ParetoEntry> Pareto => DowntimePareto.Build(fixture.Block(fixture.Week(14)), HistoryFixture.HallAAll, fixture.Costs);

    [Theory]
    [InlineData(LossReasons.FabricFeedFault, 384, 14, 6.4)]
    [InlineData(LossReasons.MixtureWait, 246, 4, 4.1)]
    [InlineData(LossReasons.OvenWait, 210, 5, 3.5)]
    [InlineData(LossReasons.SetupOverrun, 194, 11, 3.2)]
    [InlineData(LossReasons.UnplannedMouldChange, 120, 3, 2.0)]
    [InlineData(LossReasons.LabelsEmpty, 66, 23, 1.1)]
    public void Downtime_by_reason_matches_the_reference(string reason, double minutes, int count, double hours)
    {
        var entry = Pareto.Single(e => e.Reason == reason);

        Assert.Equal(minutes, entry.Duration.TotalMinutes);
        Assert.Equal(count, entry.Count);
        Assert.Equal(hours, Math.Round(entry.Duration.TotalHours, 1, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Pareto_order_puts_the_six_reference_reasons_on_top()
    {
        Assert.Equal(
            [LossReasons.FabricFeedFault, LossReasons.MixtureWait, LossReasons.OvenWait, LossReasons.SetupOverrun,
             LossReasons.UnplannedMouldChange, LossReasons.LabelsEmpty],
            Pareto.Take(6).Select(e => e.Reason));
        Assert.All(Pareto.Skip(6), e => Assert.True(e.Duration < TimeSpan.FromMinutes(66), e.Reason));
    }

    [Fact]
    public void Nine_of_fourteen_fabric_feed_faults_are_at_RT03_always_gripper_2()
    {
        var byStation = DowntimePareto.ByStation(fixture.Block(fixture.Week(14)), HistoryFixture.Presses, LossReasons.FabricFeedFault, fixture.Costs);
        var rt03Events = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id)
            .Where(e => e.StationId == "RT-03" && e.Reason == LossReasons.FabricFeedFault && e.StartUtc >= fixture.Week(14).StartUtc);

        Assert.Equal(9, byStation["RT-03"].Count);
        Assert.All(rt03Events, e => Assert.Equal(Assemblies.FabricFeed2, e.Assembly));
    }

    [Fact]
    public void Setup_overrun_averages_about_18_minutes()
    {
        var entry = Pareto.Single(e => e.Reason == LossReasons.SetupOverrun);

        Assert.Equal(18, Math.Round(entry.Duration.TotalMinutes / entry.Count, MidpointRounding.AwayFromZero));
    }

    [Theory]
    [InlineData(Shift.Early, 82)]
    [InlineData(Shift.Late, 80)]
    [InlineData(Shift.Night, 72)]
    public void Yesterdays_shift_oee(Shift shift, int expected) =>
        Assert.Equal(expected, OeeResult.Percent(OeeCalculator.From(fixture.Block(fixture.Shift(14, shift)), HistoryFixture.OeeStations).Oee));

    [Fact]
    public void Yesterdays_day_oee_is_78_percent() =>
        Assert.Equal(78, OeeResult.Percent(OeeCalculator.From(fixture.Block(fixture.Day(14)), HistoryFixture.OeeStations).Oee));

    [Fact]
    public void Night_differs_in_availability_while_performance_and_quality_are_comparable()
    {
        var oee = new[] { Shift.Early, Shift.Late, Shift.Night }
            .ToDictionary(s => s, s => OeeCalculator.From(fixture.Block(fixture.Shift(14, s)), HistoryFixture.OeeStations));

        Assert.True(oee[Shift.Night].Availability < oee[Shift.Late].Availability - 0.05);
        Assert.True(oee[Shift.Night].Availability < oee[Shift.Early].Availability - 0.05);
        Assert.InRange(oee.Values.Max(o => o.Performance) - oee.Values.Min(o => o.Performance), 0, 0.03);
        Assert.InRange(oee.Values.Max(o => o.Quality) - oee.Values.Min(o => o.Quality), 0, 0.03);
    }

    [Fact]
    public void Main_night_loss_yesterday_is_waiting_for_mixture_with_2_3_hours()
    {
        var pareto = DowntimePareto.Build(fixture.Block(fixture.Shift(14, Shift.Night)), HistoryFixture.HallAAll, fixture.Costs);

        Assert.Equal(LossReasons.MixtureWait, pareto[0].Reason);
        Assert.Equal(2.3, Math.Round(pareto[0].Duration.TotalHours, 1, MidpointRounding.AwayFromZero));
    }
}
