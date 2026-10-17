using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Tests.Simulation;

namespace OPCAgentSample.Tests.Kpi;

/// <summary>
/// Spezifikation Abschnitt 15: laufende Blöcke werden inkrementell fortgeschrieben, beim Abschluss neu berechnet;
/// beide Wege müssen dieselben Zahlen liefern. Grundlage ist die erzeugte Historie bis zur Demo-Uhr.
/// </summary>
[Collection(HistoryGroup.Name)]
public sealed class IncrementalEqualsFullTests(HistoryFixture fixture)
{
    [Fact]
    public void Shift_blocks_written_record_by_record_add_up_to_the_recomputed_week()
    {
        var week = fixture.Week(14);
        var shifts = fixture.Calendar.ProductionShiftsBetween(week.StartUtc, fixture.Now).ToList();
        var shiftBuilders = shifts.Select(s => new KpiBlockBuilder(s, fixture.Costs)).ToList();
        foreach (var record in fixture.Records)
        {
            foreach (var builder in shiftBuilders) builder.Add(record);
        }

        var incremental = KpiBlock.Combine(week.Key, shiftBuilders.Select(b => b.Snapshot(Min(b.Period.EndUtc, fixture.Now))));
        var full = fixture.Block(week);

        AssertSame(full, incremental);
    }

    [Fact]
    public void Running_block_equals_recomputation_at_every_watermark()
    {
        var day = fixture.Day(14);
        var running = new KpiBlockBuilder(day, fixture.Costs);
        var watermarks = Enumerable.Range(1, 12).Select(h => day.StartUtc.AddHours(2 * h)).ToList();
        var next = 0;
        foreach (var record in fixture.Records.Where(r => r.TimestampUtc < day.EndUtc))
        {
            while (next < watermarks.Count && record.TimestampUtc >= watermarks[next])
            {
                AssertSame(Recompute(day, watermarks[next]), running.Snapshot(watermarks[next]));
                next++;
            }

            running.Add(record);
        }

        Assert.Equal(watermarks.Count - 1, next);
    }

    private KpiBlock Recompute(ProductionPeriod period, DateTimeOffset watermark)
    {
        var builder = new KpiBlockBuilder(period, fixture.Costs);
        builder.AddRange(fixture.Records.Where(r => r.TimestampUtc < watermark));
        return builder.Snapshot(watermark);
    }

    private static void AssertSame(KpiBlock expected, KpiBlock actual)
    {
        Assert.Equal(expected.Stations.Keys.Order(), actual.Stations.Keys.Order());
        foreach (var (station, block) in expected.Stations)
        {
            Assert.Equal(block.Figures, actual.Stations[station].Figures);
            Assert.Equal(Sorted(block.Reasons), Sorted(actual.Stations[station].Reasons));
        }
    }

    private static List<(string, ReasonFigures)> Sorted(IReadOnlyDictionary<string, ReasonFigures> reasons) =>
        [.. reasons.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => (r.Key, r.Value))];

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
