using OPCAgentSample.Domain.History;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Tests.Server.Ingestion;

/// <summary>Der Seed der Stammdaten stimmt mit dem Szenario der Simulation überein; das Szenario ist die Wahrheit.</summary>
public sealed class DemoSeedConsistencyTests
{
    private static readonly SharedHistory s_history = SharedHistory.Instance;
    private static readonly MasterDataSnapshot s_master = MasterDataSeed.Build();

    [Fact]
    public void Articles_carry_the_scenario_values_including_cartons()
    {
        var articles = s_master.Articles;

        foreach (var expected in s_history.Scenario.Articles)
        {
            var actual = articles[expected.Id];
            Assert.Equal((expected.DiameterMm, expected.Grit), (actual.DiameterMm, actual.Grit));
            Assert.Equal((decimal)expected.ThicknessMm, actual.ThicknessMm);
            Assert.Equal((decimal)expected.FillWeightTargetGrams, actual.FillWeightTargetGrams);
            Assert.Equal((decimal)expected.FillWeightToleranceGrams, actual.FillWeightToleranceGrams);
        }

        foreach (var expected in s_history.Scenario.PackagingArticles)
        {
            Assert.Equal(expected.PiecesPerCarton, articles[expected.Id].PiecesPerCarton);
        }

        Assert.Equal(s_history.Scenario.Articles.Count + s_history.Scenario.PackagingArticles.Count, articles.Count);
    }

    [Fact]
    public void Mixture_batches_match_the_scenario()
    {
        var batches = s_master.MixtureBatches;

        Assert.Equal(s_history.Scenario.MixtureBatches.Count, batches.Count);
        foreach (var expected in s_history.Scenario.MixtureBatches)
        {
            Assert.Equal(expected.Recipe, batches[expected.Id].MixtureId);
            Assert.Equal((int)Math.Round(expected.BulkDensityKgPerLitre * 1000), batches[expected.Id].BulkDensityGramsPerLiter);
            Assert.Equal(expected.MixedUtc, batches[expected.Id].ProducedAtUtc);
        }
    }

    [Fact]
    public void Orders_match_the_scenario_and_the_quantity_produced_by_the_generator()
    {
        var orders = s_master.Orders.ToDictionary(o => o.Id);
        var cycles = s_history.Records.OfType<CycleRecord>().Where(c => c.OrderId is not null).GroupBy(c => c.OrderId!).ToDictionary(g => g.Key, g => g.Count());
        var completed = s_history.Records.OfType<OrderEvent>().Where(e => e.Kind == OrderEventKind.Completed).Select(e => e.OrderId).ToHashSet();

        Assert.Equal(s_history.Scenario.Orders.Count, orders.Count);
        foreach (var expected in s_history.Scenario.Orders)
        {
            var actual = orders[expected.Id];
            Assert.Equal((expected.HallId, expected.StationId, expected.ArticleId, expected.Quantity), (actual.HallId, actual.StationId, actual.ArticleId, actual.TargetQuantity));
            Assert.Equal(expected.DueUtc, actual.DueUtc);
            if (expected.ProducedAtDemo is { } produced)
            {
                Assert.Equal(produced, actual.ProducedQuantity);
                continue;
            }

            Assert.Equal(cycles.GetValueOrDefault(expected.Id), actual.ProducedQuantity);
            Assert.Equal(completed.Contains(expected.Id), actual.State == OrderState.Completed);
            if (actual.State == OrderState.Completed) Assert.Equal(expected.Quantity, actual.ProducedQuantity);
        }
    }

    [Fact]
    public void Moulds_match_the_scenario_and_the_strokes_counted_from_the_cycles()
    {
        var moulds = s_master.Moulds.ToDictionary(m => m.Id);

        foreach (var expected in s_history.Scenario.Moulds)
        {
            var actual = moulds[expected.Id];
            Assert.Equal((expected.StationId, expected.Nests, expected.StrokeLimit, expected.ForecastStrokesPerDay), (actual.StationId, actual.Nests, actual.StrokeLimit, actual.AverageStrokesPerDay));
            Assert.Equal(expected.MountedUtc, actual.MountedAtUtc);
            var strokes = s_history.Records.OfType<CycleRecord>().Count(c => c.StationId == expected.StationId && c.TimestampUtc >= expected.MountedUtc) / expected.Nests;
            Assert.Equal(strokes, actual.StrokesSinceChange);
        }
    }

    [Fact]
    public void Cost_rates_equal_the_calibrated_rates_of_the_scenario()
    {
        var rates = s_master.CostRates;
        var expected = s_history.Costs;

        Assert.All(rates.Where(r => r.Kind == CostRateKind.DowntimeMinute), r => Assert.Equal(expected.DowntimeRate(r.StationId!), r.Euro));
        Assert.All(rates.Where(r => r.Kind == CostRateKind.ScrapPiece), r => Assert.Equal(expected.ScrapCost(r.ArticleId!), r.Euro));
        Assert.Equal(8, rates.Count(r => r.Kind == CostRateKind.DowntimeMinute));
        Assert.All(rates, r => Assert.True(r.IsCalibrated));
    }

    [Fact]
    public void Cost_table_hands_the_kpi_library_the_same_rates()
    {
        var table = s_master.CostTable();

        Assert.Equal(33.7m, table.DowntimeRate("RT-03"));
        Assert.Equal(s_history.Costs.ScrapCost("A-125-A60"), table.ScrapCost("A-125-A60"));
        Assert.Equal(s_history.Costs.ScrapCost("K-180-10"), table.ScrapCost("K-180-10"));
    }
}
