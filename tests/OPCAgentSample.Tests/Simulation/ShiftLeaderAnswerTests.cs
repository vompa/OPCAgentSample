using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Referenzantworten Schichtleiter (Mockup-Objekt A: sl1–sl7) über Historie und Kennzahlen-Bibliothek.</summary>
[Collection(HistoryGroup.Name)]
public sealed class ShiftLeaderAnswerTests(HistoryFixture fixture)
{
    [Fact]
    public void Shift_handover_night_reached_92_percent_of_plan()
    {
        var night = fixture.Block(fixture.Shift(14, Shift.Night));

        Assert.Equal(92, OeeResult.Percent(PlanFulfillment.From(night.Figures(HistoryFixture.Presses)).Ratio));
    }

    [Theory]
    [InlineData("RT-01", 102)]
    [InlineData("RT-02", 98)]
    [InlineData("RT-03", 71)]
    [InlineData("RT-04", 97)]
    [InlineData("VP-01", 99)]
    [InlineData("VP-02", 88)]
    public void Shift_handover_bars_per_station(string station, int percent) =>
        Assert.Equal(percent, OeeResult.Percent(PlanFulfillment.From(fixture.Block(fixture.Shift(14, Shift.Night)).Figures(station)).Ratio));

    [Fact]
    public void Shift_handover_at_0604_RT03_fault_open_since_0512_and_RT01_mixture_until_0720()
    {
        var at = fixture.At.Oct(15, "06:04");
        var rt03 = fixture.Live.Station("RT-03", at);
        var mixture = fixture.Live.Station("RT-01", at).Materials.Single(m => m.Material == MinuteVariables.MixtureLevelKg);
        var fault = EventKpis.Latest(fixture.Records.OfType<LossEvent>().Where(e => e.TimestampUtc <= at), e => e.Id)
            .Single(e => e.StationId == "RT-03" && e.IsOpen && e.Category == LossCategory.Breakdown);

        Assert.Equal(OperatingState.Fault, rt03.State);
        Assert.Equal(fixture.At.Oct(15, "05:12"), fault.StartUtc);
        Assert.Contains("Instandhaltung informiert", fault.Note, StringComparison.Ordinal);
        Assert.Equal(fixture.At.Oct(15, "07:20"), mixture.ProcessableUntilUtc);
    }

    [Fact]
    public void Disruptions_this_shift_are_four_events_with_68_minutes_and_one_open_warning()
    {
        var shift = fixture.Shift(15, Shift.Early);
        var entries = EventKpis.Disruptions(
            fixture.Records.OfType<LossEvent>(), fixture.Records.OfType<AlarmRecord>(), new TimeWindow(shift.StartUtc, shift.EndUtc), fixture.Now);

        Assert.Equal(4, entries.Count);
        Assert.Equal(68, entries.Sum(e => e.StandstillInWindow.TotalMinutes));
        Assert.Equal(65, entries.Single(e => e.StationId == "RT-03").StandstillInWindow.TotalMinutes);
        Assert.Equal(2, entries.Single(e => e.StationId == "RT-02").StandstillInWindow.TotalMinutes);
        Assert.Equal(1, entries.Single(e => e.StationId == "VP-01").StandstillInWindow.TotalMinutes);
        var open = Assert.Single(entries, e => e.IsAlarmOnly);
        Assert.Equal("RT-04", open.StationId);
        Assert.Null(open.EndUtc);
        Assert.Equal(fixture.At.Oct(15, "09:41"), open.StartUtc);
    }

    [Fact]
    public void Material_reach_RT01_first_then_RT02_fabric_VP01_cartons_RT04_and_VP02()
    {
        var reach = HistoryFixture.OeeStations.ToDictionary(
            s => s, s => fixture.Live.Station(s, fixture.Now).ShortestReach(fixture.Now)!);

        Assert.Equal(38, reach["RT-01"].Reach.TotalMinutes, 0);
        Assert.Equal(55, reach["RT-02"].Reach.TotalMinutes, 0);
        Assert.InRange(reach["VP-01"].Reach.TotalMinutes, 95, 105);
        Assert.Equal(150, reach["RT-04"].Reach.TotalMinutes, 0);
        Assert.InRange(reach["VP-02"].Reach.TotalMinutes, 165, 175);
        Assert.True(reach["RT-03"].Reach.TotalMinutes > 120);
        Assert.Equal("RT-01", reach.MinBy(r => r.Value.Reach).Key);
    }

    [Fact]
    public void Shift_target_gap_comes_from_RT03_backlog_and_RT04_cycle_time()
    {
        var order = fixture.Scenario.Order("4711");
        var produced = fixture.Generator.StationModel("RT-03").CountCycles(order.PlannedStartUtc, fixture.Now);
        var backlog = PlanFulfillment.OrderBacklog(order.Quantity, produced, order.PlannedStartUtc, order.PlannedEndUtc, fixture.Now);
        var rt04 = fixture.Live.Station("RT-04", fixture.Now);
        var speedLoss = PlanFulfillment.SpeedLossPieces(fixture.At.Oct(15, "14:00") - fixture.At.Oct(15, "08:30"), rt04.CycleTimeSeconds!.Value, rt04.TargetCycleSeconds);

        Assert.Equal(1_200, Math.Round(backlog / 100) * 100);
        Assert.Equal(76, OeeResult.Percent((double)produced / order.Quantity));
        Assert.InRange(speedLoss, 300, 450);
    }

    [Fact]
    public void RT04_build_up_time_rises_from_0830_and_causes_the_lag()
    {
        var hours = new[] { ("06:00", "07:00"), ("07:00", "08:00"), ("08:00", "09:00"), ("09:00", "09:42") }
            .Select(h => CycleStatistics.Of(fixture.Cycles("RT-04", fixture.At.Oct(15, h.Item1), fixture.At.Oct(15, h.Item2))).MeanBuildUpSeconds)
            .ToList();

        Assert.Equal(0.80, Math.Round(hours[0], 2));
        Assert.Equal(0.80, Math.Round(hours[1], 2));
        Assert.True(hours[2] > hours[1] && hours[3] > hours[2]);
        Assert.Equal(1.1, Math.Round(fixture.Live.Station("RT-04", fixture.Now).BuildUpSeconds!.Value, 1));
    }

    [Fact]
    public void VP02_has_no_own_problem_it_waits_for_the_180_charge_from_OF02()
    {
        var vp02 = fixture.Live.Station("VP-02", fixture.Now);
        var of02 = fixture.Live.Oven("OF-02", fixture.Now);
        var of01 = fixture.Live.Oven("OF-01", fixture.Now);
        var wait = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id).Single(e => e.StationId == "VP-02" && e.IsOpen);

        Assert.Equal(OperatingState.WaitingForUpstream, vp02.State);
        Assert.Equal(LossReasons.OvenWait, wait.Reason);
        Assert.Equal(180, of02.ActiveCharges[0].DiameterMm);
        Assert.Equal("alle Ø 180", of02.AssignmentRule);
        Assert.Equal(of02.Places, of02.OccupiedPlaces);
        Assert.True(of01.OccupiedPlaces < of01.Places);
    }

    [Fact]
    public void Hall_situation_presses_at_90_percent()
    {
        var presses = fixture.Block(fixture.Shift(15, Shift.Early)).Figures(HistoryFixture.Presses);

        Assert.Equal(90, OeeResult.Percent(PlanFulfillment.From(presses).Ratio));
    }
}
