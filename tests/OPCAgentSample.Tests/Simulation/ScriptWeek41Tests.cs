using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Drehbuch-Zeilen der KW 41 und Kalender (Spezifikation Abschnitt 6).</summary>
[Collection(HistoryGroup.Name)]
public sealed class ScriptWeek41Tests(HistoryFixture fixture)
{
    [Fact]
    public void RT04_quality_hold_after_mould_change_scraps_340_pieces()
    {
        var scrap = fixture.Cycles("RT-04", fixture.At.Oct(8, "06:00"), fixture.At.Oct(8, "07:20")).Count(c => c.Scrap == ScrapReason.Thickness);
        var hold = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id).Single(e => e.Category == LossCategory.QualityHold);

        Assert.Equal(340, scrap);
        Assert.Equal(340, hold.AffectedPieces);
        Assert.Equal(TimeSpan.FromMinutes(12), hold.EndUtc - hold.StartUtc);
    }

    [Fact]
    public void RT04_friday_night_shift_is_not_staffed()
    {
        var night = fixture.Shift(9, Shift.Night);
        var figures = fixture.Block(night).Figures("RT-04");

        Assert.Equal(0, figures.TotalPieces);
        Assert.Equal(TimeSpan.FromMinutes(480), figures.AvailabilityLossTime);
        Assert.Equal(LossReasons.StaffMissing, Assert.Single(fixture.Block(night).Reasons(["RT-04"])).Key);
    }

    [Fact]
    public void OF01_temperature_deviation_delays_the_running_charge_by_25_minutes()
    {
        var at = fixture.At.Oct(12, "09:50");
        var delayed = fixture.Generator.OvenModel("OF-01").ActiveAt(at).Where(c => c.LoadedUtc <= fixture.At.Oct(12, "09:40")).ToList();
        var zone2 = fixture.Records.OfType<MinuteValue>()
            .Single(v => v.StationId == "OF-01" && v.Variable == MinuteVariables.Zone2Temperature && v.TimestampUtc == at);

        Assert.NotEmpty(delayed);
        Assert.All(delayed, c => Assert.Equal(TimeSpan.FromMinutes(265), c.UnloadedUtc - c.LoadedUtc));
        Assert.InRange(zone2.Value, 170, 176);
    }

    [Fact]
    public void RT03_fabric_feed_pattern_starts_in_week_41()
    {
        var faults = EventKpis.Latest(fixture.Records.OfType<LossEvent>(), e => e.Id)
            .Where(e => e.StationId == "RT-03" && e.Reason == LossReasons.FabricFeedFault && e.StartUtc < fixture.Week(14).StartUtc)
            .OrderBy(e => e.StartUtc)
            .ToList();

        Assert.Equal(fixture.At.Oct(5, "07:40"), faults[0].StartUtc);
        Assert.Equal(TimeSpan.FromMinutes(38), faults[0].EndUtc - faults[0].StartUtc);
        Assert.Contains(faults, f => f.StartUtc == fixture.At.Oct(8, "16:30") && f.EndUtc - f.StartUtc == TimeSpan.FromMinutes(47));
    }

    [Fact]
    public void No_production_on_the_weekend()
    {
        var weekend = new TimeWindow(fixture.At.Oct(10, "06:00"), fixture.At.Oct(12, "06:00"));

        Assert.DoesNotContain(fixture.Records.OfType<CycleRecord>(), c => weekend.Contains(c.TimestampUtc));
        Assert.Contains(fixture.Records.OfType<MinuteValue>(), v => weekend.Contains(v.TimestampUtc));
    }
}
