using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>
/// Referenzantworten Produktionsleiter (pl1–pl6). Werkszahlen entstehen aus dem gerechneten Hallen-A-Anteil plus den
/// verdichteten Zählern der Hallen B/C (<see cref="PlantComparison"/>); der Hallen-A-Anteil wird jeweils mitgeprüft.
/// </summary>
[Collection(HistoryGroup.Name)]
public sealed class ProductionManagerAnswerTests(HistoryFixture fixture)
{
    private const int Week42 = 14;
    private const int Week41 = 7;

    [Fact]
    public void Weekly_report_96_percent_plan_and_oee_79_versus_78()
    {
        var week = fixture.PlantBlock(Week42);
        var previous = fixture.PlantBlock(Week41);
        var discs = week.Figures([.. HistoryFixture.Presses, PlantComparison.HallBStation, PlantComparison.HallCStation]);

        Assert.Equal(1.41, Math.Round(discs.TotalPieces / 1e6, 2));
        Assert.Equal(1.47, Math.Round((double)discs.PlannedQuantity / 1e6, 2));
        Assert.Equal(96, OeeResult.Percent(PlanFulfillment.From(discs).Ratio));
        Assert.Equal(1.4, Math.Round(100.0 * discs.ScrapPieces / discs.TotalPieces, 1));
        Assert.Equal(79, OeeResult.Percent(OeeCalculator.From(week, HistoryFixture.PlantOee).Oee));
        Assert.Equal(78, OeeResult.Percent(OeeCalculator.From(previous, HistoryFixture.PlantOee).Oee));
    }

    [Theory]
    [InlineData(HistoryFixture.HallA, 77)]
    [InlineData(PlantComparison.HallBStation, 81)]
    [InlineData(PlantComparison.HallCStation, 80)]
    public void Weekly_oee_per_hall(string hall, int percent)
    {
        var week = fixture.PlantBlock(Week42);
        var stations = hall == HistoryFixture.HallA ? HistoryFixture.OeeStations : [hall];

        Assert.Equal(percent, OeeResult.Percent(OeeCalculator.From(week, stations).Oee));
    }

    [Fact]
    public void Two_230_orders_in_hall_B_are_at_risk_and_4711_can_catch_up()
    {
        var o4730 = fixture.Scenario.Order("4730");
        var o4733 = fixture.Scenario.Order("4733");
        var o4711 = fixture.Scenario.Order("4711");
        var produced = fixture.Generator.StationModel("RT-03").CountCycles(o4711.PlannedStartUtc, fixture.Now);
        var remainingCapacity = fixture.Calendar.ProductionShiftsBetween(fixture.Now, o4711.DueUtc)
            .Sum(s => (Min(s.EndUtc, o4711.DueUtc) - Max(s.StartUtc, fixture.Now)).TotalSeconds) / 3.73;

        Assert.Equal(58, OeeResult.Percent((double)o4730.ProducedAtDemo!.Value / o4730.Quantity));
        Assert.Equal(41, OeeResult.Percent((double)o4733.ProducedAtDemo!.Value / o4733.Quantity));
        Assert.All(new[] { o4730, o4733 }, o => Assert.Equal(fixture.At.Oct(20, "14:00"), o.DueUtc));
        Assert.All(new[] { o4730, o4733 }, o => Assert.Equal(PlantLayout.HallB, o.HallId));
        Assert.Equal(76, OeeResult.Percent((double)produced / o4711.Quantity));
        Assert.True(o4711.Quantity - produced < remainingCapacity * 0.5);
    }

    [Theory]
    [InlineData(Shift.Early, 82, 90, 93, 98)]
    [InlineData(Shift.Late, 81, 89, 93, 98)]
    [InlineData(Shift.Night, 73, 79, 93, 99)]
    public void Oee_comparison_per_shift_plantwide(Shift shift, int oee, int availability, int performance, int quality)
    {
        var result = OeeCalculator.From(fixture.PlantBlock(Week42, shift), HistoryFixture.PlantOee);

        Assert.Equal(oee, OeeResult.Percent(result.Oee));
        Assert.Equal(availability, OeeResult.Percent(result.Availability));
        Assert.Equal(performance, OeeResult.Percent(result.Performance));
        Assert.Equal(quality, OeeResult.Percent(result.Quality));
    }

    [Fact]
    public void Night_is_worst_because_of_availability_and_mixture_waits_of_5_8_hours()
    {
        var night = fixture.PlantBlock(Week42, Shift.Night);
        var hallA = OeeCalculator.From(night, HistoryFixture.OeeStations);
        var hallAOthers = new[] { Shift.Early, Shift.Late }.Select(s => OeeCalculator.From(fixture.PlantBlock(Week42, s), HistoryFixture.OeeStations));
        var mixture = DowntimePareto.Build(night, HistoryFixture.PlantAll, fixture.Costs).Single(e => e.Reason == LossReasons.MixtureWait);
        var mixtureHallA = DowntimePareto.Build(night, HistoryFixture.HallAAll, fixture.Costs).Single(e => e.Reason == LossReasons.MixtureWait);

        Assert.Equal(5.8, Math.Round(mixture.Duration.TotalHours, 1));
        Assert.Equal(4.1, Math.Round(mixtureHallA.Duration.TotalHours, 1));
        Assert.All(hallAOthers, o => Assert.True(hallA.Oee < o.Oee && hallA.Availability < o.Availability));
    }

    [Fact]
    public void Costs_of_the_week_are_about_41000_euro()
    {
        var rates = PlantComparison.WithHallRates(fixture.Scenario);
        var plant = CostCalculator.Of(fixture.PlantBlock(Week42), HistoryFixture.PlantAll, rates);
        var previous = CostCalculator.Of(fixture.PlantBlock(Week41), HistoryFixture.PlantAll, rates);

        Assert.Equal(41.0m, Math.Round(plant.TotalEuro / 1000, 1));
        Assert.Equal(14.5m, Math.Round(plant.ScrapEuro / 1000, 1));
        Assert.Equal(26.5m, Math.Round(plant.DowntimeEuro / 1000, 1));
        Assert.Equal(46.0m, Math.Round(previous.TotalEuro / 1000, 1));
        Assert.Equal(-11, OeeResult.Percent((double)((plant.TotalEuro / previous.TotalEuro) - 1)));
    }

    [Theory]
    [InlineData(HistoryFixture.HallA, 19.2)]
    [InlineData(PlantComparison.HallBStation, 11.9)]
    [InlineData(PlantComparison.HallCStation, 9.9)]
    public void Costs_per_hall(string hall, double thousands)
    {
        var stations = hall == HistoryFixture.HallA ? HistoryFixture.HallAAll : [hall];
        var costs = CostCalculator.Of(fixture.PlantBlock(Week42), stations, PlantComparison.WithHallRates(fixture.Scenario));

        Assert.Equal((decimal)thousands, Math.Round(costs.TotalEuro / 1000, 1));
    }

    [Fact]
    public void Biggest_single_cost_item_is_the_fabric_feed_at_RT03_with_9800_euro()
    {
        var week = fixture.Block(fixture.Week(Week42));
        var items = HistoryFixture.HallAAll
            .SelectMany(s => week.Stations.TryGetValue(s, out var b) ? b.Reasons.Keys.Select(r => (s, r)) : [])
            .Select(x => (x.s, x.r, Cost: CostCalculator.ReasonCost(week, x.s, x.r, fixture.Costs)))
            .OrderByDescending(x => x.Cost)
            .ToList();

        Assert.Equal(("RT-03", LossReasons.FabricFeedFault), (items[0].s, items[0].r));
        Assert.Equal(9_800m, Math.Round(items[0].Cost / 100) * 100);
    }

    [Fact]
    public void RT03_overhaul_mtbf_31_hours_versus_about_78_and_mttr_42_versus_38()
    {
        var window = fixture.DailyKpis.Where(d => d.Day >= new DateOnly(2026, 7, 17)).ToList();
        var mtbf = HistoryFixture.Presses.ToDictionary(
            s => s, s => Reliability.From(Reliability.Sum(window.Where(d => d.StationId == s))).Mtbf!.Value.TotalHours);
        var rt03 = Reliability.From(Reliability.Sum(window.Where(d => d.StationId == "RT-03")));
        var hall = Reliability.From(Reliability.Sum(window));
        var fabric = window.Where(d => d.StationId == "RT-03")
            .Sum(d => d.Reasons.TryGetValue(LossReasons.FabricFeedFault, out var r) ? r.Count : 0);

        Assert.Equal(31, Math.Round(mtbf["RT-03"]));
        Assert.Equal(82, Math.Round(mtbf["RT-01"]));
        Assert.Equal(77, Math.Round(mtbf["RT-02"]));
        Assert.Equal(74, Math.Round(mtbf["RT-04"]));
        Assert.Equal(78, Math.Round((mtbf["RT-01"] + mtbf["RT-02"] + mtbf["RT-04"]) / 3));
        Assert.Equal(42, Math.Round(rt03.Mttr!.Value.TotalMinutes));
        Assert.Equal(38, Math.Round(hall.Mttr!.Value.TotalMinutes));
        Assert.Equal(78, OeeResult.Percent((double)fabric / rt03.Failures));
    }

    [Theory]
    [InlineData(7, 48)]
    [InlineData(8, 45)]
    [InlineData(9, 39)]
    [InlineData(10, 33)]
    public void Unplanned_share_falls_since_july_hall_A(int month, int percent) =>
        Assert.Equal(percent, OeeResult.Percent(Reliability.From(Reliability.Sum(fixture.DailyKpis.Where(d => d.Day.Month == month))).UnplannedShare));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
