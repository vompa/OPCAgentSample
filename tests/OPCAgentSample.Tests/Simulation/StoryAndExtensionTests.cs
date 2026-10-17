using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Überproduktion mit Ursachenkette und die vier Historie-Erweiterungen (Spezifikation Abschnitt 6 und 13).</summary>
[Collection(HistoryGroup.Name)]
public sealed class StoryAndExtensionTests(HistoryFixture fixture)
{
    [Fact]
    public void Overproduction_RT01_after_order_4688_adds_2050_pieces_without_order()
    {
        var start = fixture.At.Oct(6, "19:10");
        var end = fixture.At.Oct(6, "22:00");
        var cycles = fixture.Cycles("RT-01", start, end).ToList();
        var states = fixture.Records.OfType<StateChange>().Where(s => s.StationId == "RT-01" && s.TimestampUtc == start).ToList();
        var reached = fixture.Records.OfType<OrderEvent>().Single(o => o.OrderId == "4688" && o.Kind == OrderEventKind.QuantityReached);

        Assert.Equal(2_050, cycles.Count);
        Assert.All(cycles, c => Assert.Null(c.OrderId));
        Assert.Equal(OperatingState.RunningWithoutOrder, Assert.Single(states).State);
        Assert.InRange(reached.TimestampUtc, start.AddMinutes(-1), start);
        Assert.Equal(2_050, fixture.Block(fixture.Week(7)).Figures("RT-01").OverproductionPieces);
    }

    [Fact]
    public void Overproduction_RT02_monday_night_is_green_in_the_dashboard_at_118_percent()
    {
        var night = fixture.Shift(12, Shift.Night);
        var figures = fixture.Block(night).Figures("RT-02");
        var snapshot = fixture.Live.Station("RT-02", night.EndUtc.AddMinutes(-1));

        Assert.Equal(118, OeeResult.Percent(PlanFulfillment.From(figures).Ratio));
        Assert.Equal(3_600, figures.OverproductionPieces);
        Assert.Equal(3_600, fixture.Cycles("RT-02", fixture.At.Oct(13, "01:48"), night.EndUtc).Count(c => c.OrderId == "4705"));
        var planSignals = new StationSignals(snapshot.State, snapshot.PlanRatio, HasRunningOrder: snapshot.OrderId is not null);
        Assert.Equal("4705", snapshot.OrderId);
        Assert.Equal(118, OeeResult.Percent(snapshot.PlanRatio));
        Assert.Equal(TrafficLight.Running, TrafficLightRules.Evaluate(planSignals).Light);
        Assert.True(fixture.Scenario.Order("4705").NeedUtc - night.StartUtc >= TimeSpan.FromDays(2));
    }

    [Fact]
    public void Cause_chain_full_buffer_blocks_RT04_and_starves_VP02()
    {
        var plates = fixture.Records.OfType<MinuteValue>()
            .Where(v => v.StationId == "PU-OFEN" && v.Variable == MinuteVariables.PlateCount && v.TimestampUtc >= fixture.Week(14).StartUtc)
            .ToList();
        var peak = plates.MaxBy(v => v.Value)!;
        var waits = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id)
            .Where(e => e.Reason == LossReasons.OvenWait && e.StartUtc >= peak.TimestampUtc.AddHours(-1) && e.StartUtc < peak.TimestampUtc.AddHours(6))
            .ToList();

        Assert.Equal(58, peak.Value);
        Assert.InRange(peak.TimestampUtc, fixture.At.Oct(13, "00:00"), fixture.At.Oct(13, "06:00"));
        Assert.Contains(waits, w => w.StationId == "RT-04" && w.EndUtc - w.StartUtc == TimeSpan.FromMinutes(70));
        Assert.Contains(waits, w => w.StationId == "VP-02" && w.EndUtc - w.StartUtc == TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void Traceability_cartons_with_batch_M_2410_17_are_not_shipped_yet()
    {
        var lots = fixture.Records.OfType<PressLot>().Where(l => l.MixtureBatchId == "M-2410-17").Select(l => l.LotId).ToHashSet();
        var charges = fixture.Records.OfType<OvenCharge>().Where(c => c.PressLotIds.Any(lots.Contains)).Select(c => c.ChargeId).ToHashSet();
        var pallets = EventKpis.Latest(fixture.Records.OfType<PalletRecord>(), p => p.PalletId)
            .Where(p => p.OvenChargeIds.Any(charges.Contains))
            .ToList();
        var earlier = EventKpis.Latest(fixture.Records.OfType<PalletRecord>(), p => p.PalletId)
            .Where(p => p.TimestampUtc < fixture.At.Oct(14, "12:00"));

        Assert.Contains(lots, l => l.StartsWith("L-RT-01-2026-10-14N", StringComparison.Ordinal));
        Assert.Contains(lots, l => l.StartsWith("L-RT-02-2026-10-14N", StringComparison.Ordinal));
        Assert.NotEmpty(charges);
        Assert.NotEmpty(pallets);
        Assert.All(pallets, p => Assert.Equal("VP-01", p.StationId));
        Assert.All(pallets, p => Assert.Null(p.ShippedUtc));
        Assert.Contains(earlier, p => p.ShippedUtc is not null);
    }

    [Fact]
    public void Startup_losses_of_15_to_20_minutes_after_every_shift_change()
    {
        var startups = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id)
            .Where(e => e.Reason == LossReasons.StartupLoss && e.EndUtc is not null)
            .ToList();

        Assert.NotEmpty(startups);
        Assert.All(startups, e => Assert.InRange((e.EndUtc!.Value - e.StartUtc).TotalMinutes, 15, 20));
        Assert.All(startups, e => Assert.Equal(LossCategory.SpeedLoss, e.Category));
        var shifts = fixture.Calendar.ProductionShiftsBetween(fixture.Scenario.HistoryStartUtc, fixture.Now.AddHours(-1)).Count();
        Assert.True(startups.Count >= shifts * 5);
    }

    [Fact]
    public void Night_repairs_take_longer_because_of_on_call_maintenance()
    {
        var breakdowns = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id)
            .Where(e => e.Reason == LossReasons.FabricFeedFault && e.EndUtc is not null && e.EndUtc - e.StartUtc < TimeSpan.FromMinutes(60))
            .ToList();
        var night = breakdowns.Where(e => fixture.Calendar.ShiftOf(e.StartUtc) == Shift.Night).Average(e => (e.EndUtc!.Value - e.StartUtc).TotalMinutes);
        var day = breakdowns.Where(e => fixture.Calendar.ShiftOf(e.StartUtc) != Shift.Night).Average(e => (e.EndUtc!.Value - e.StartUtc).TotalMinutes);

        Assert.True(night > day * 1.15, $"Nacht {night:0.0} min, Tag {day:0.0} min");
    }

    [Fact]
    public void Hall_climate_follows_a_day_night_cycle()
    {
        var temperatures = fixture.Records.OfType<MinuteValue>()
            .Where(v => v.StationId == MinuteVariables.HallAClimateSource && v.Variable == MinuteVariables.Temperature
                && v.TimestampUtc >= fixture.At.Oct(14, "00:00") && v.TimestampUtc < fixture.At.Oct(15, "00:00"))
            .ToList();
        var coldest = fixture.Calendar.ToLocal(temperatures.MinBy(v => v.Value)!.TimestampUtc).Hour;
        var warmest = fixture.Calendar.ToLocal(temperatures.MaxBy(v => v.Value)!.TimestampUtc).Hour;

        Assert.InRange(coldest, 3, 7);
        Assert.InRange(warmest, 15, 19);
    }

    [Fact]
    public void Ground_noise_has_about_two_minor_stops_per_station_and_shift()
    {
        var minor = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id)
            .Count(e => e.Id.StartsWith("K-", StringComparison.Ordinal) && e.EndUtc is not null);
        var stationShifts = HistoryFixture.OeeStations.Length
            * fixture.Calendar.ProductionShiftsBetween(fixture.Scenario.HistoryStartUtc, fixture.Now).Count();

        Assert.InRange((double)minor / stationShifts, 1.7, 2.0);
    }
}
