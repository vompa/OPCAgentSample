using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Simulation;

/// <summary>Referenzantworten Hallenleiterin (hl1–hl7, wk1) über Historie und Kennzahlen-Bibliothek.</summary>
[Collection(HistoryGroup.Name)]
public sealed class HallManagerAnswerTests(HistoryFixture fixture)
{
    [Fact]
    public void Hall_oee_today_is_81_with_presses_79_ovens_87_and_packaging_84()
    {
        var today = fixture.Block(fixture.Day(15));
        var ovens = HistoryFixture.Ovens.Select(o => fixture.Live.Oven(o, fixture.Now).OccupancySinceShiftStart).Average();

        Assert.Equal(81, OeeResult.Percent(OeeCalculator.From(today, HistoryFixture.OeeStations).Oee));
        Assert.Equal(79, OeeResult.Percent(OeeCalculator.From(today, HistoryFixture.Presses).Oee));
        Assert.Equal(84, OeeResult.Percent(OeeCalculator.From(today, HistoryFixture.Packaging).Oee));
        Assert.Equal(87, OeeResult.Percent(ovens));
    }

    [Fact]
    public void Scrap_today_is_1_9_percent_driven_by_batch_M_2410_17()
    {
        var presses = fixture.Block(fixture.Day(15)).Figures(HistoryFixture.Presses);
        var weightScrap = fixture.Cycles("RT-01", fixture.Shift(15, Shift.Early).StartUtc, fixture.Now)
            .Concat(fixture.Cycles("RT-02", fixture.Shift(15, Shift.Early).StartUtc, fixture.Now))
            .Count(c => c.Scrap == ScrapReason.Weight);

        Assert.Equal(1.9, Math.Round(100.0 * presses.ScrapPieces / presses.TotalPieces, 1));
        Assert.True(weightScrap > presses.ScrapPieces / 2);
    }

    [Fact]
    public void Quality_drift_since_2200_at_RT01_and_RT02_with_batch_M_2410_17()
    {
        var since = fixture.At.Oct(14, "22:00");
        var before = fixture.At.Oct(14, "21:00");
        foreach (var station in new[] { "RT-01", "RT-02" })
        {
            var drift = CycleStatistics.Of(fixture.Cycles(station, since.AddMinutes(45), fixture.Now).Where(c => c.IsGood));
            var normal = CycleStatistics.Of(fixture.Cycles(station, before, since).Where(c => c.IsGood));
            Assert.InRange(drift.MeanFillWeightGrams - normal.MeanFillWeightGrams, 1.3, 1.7);
            Assert.All(fixture.Cycles(station, since, fixture.Now), c => Assert.Equal("M-2410-17", c.MixtureBatchId));
        }

        Assert.Contains(fixture.Scenario.MixtureBatches, b => b.Id == "M-2410-17" && b.Recipe == "A60");
    }

    [Theory]
    [InlineData("RT-01", 2.8)]
    [InlineData("RT-02", 3.1)]
    [InlineData("RT-03", 1.2)]
    [InlineData("RT-04", 0.9)]
    public void Scrap_rate_last_24_hours(string station, double percent) =>
        Assert.Equal(percent, Math.Round(100 * CycleStatistics.Of(fixture.Cycles(station, fixture.Now.AddHours(-24), fixture.Now)).ScrapRate, 1));

    [Theory]
    [InlineData("RT-03")]
    [InlineData("RT-04")]
    public void Tables_with_other_mixtures_stay_at_112_1_grams(string station) =>
        Assert.Equal(112.1, Math.Round(CycleStatistics.Of(fixture.Cycles(station, fixture.Now.AddHours(-24), fixture.Now).Where(c => c.IsGood)).MeanFillWeightGrams, 1));

    [Fact]
    public void Hall_climate_is_ruled_out_as_cause_of_the_fill_weight_drift()
    {
        var climate = fixture.Records.OfType<MinuteValue>()
            .Where(v => v.StationId == MinuteVariables.HallAClimateSource && v.Variable == MinuteVariables.Temperature)
            .ToList();
        var before = climate.Where(v => v.TimestampUtc >= fixture.At.Oct(14, "21:00") && v.TimestampUtc < fixture.At.Oct(14, "22:00")).Average(v => v.Value);
        var after = climate.Where(v => v.TimestampUtc >= fixture.At.Oct(14, "23:00") && v.TimestampUtc < fixture.At.Oct(15, "00:00")).Average(v => v.Value);
        var humidity = fixture.Records.OfType<MinuteValue>()
            .Where(v => v.StationId == MinuteVariables.HallAClimateSource && v.Variable == MinuteVariables.Humidity);

        Assert.All(climate, v => Assert.InRange(v.Value, fixture.Scenario.ClimateRange.MinCelsius, fixture.Scenario.ClimateRange.MaxCelsius));
        Assert.All(humidity, v => Assert.InRange(v.Value, fixture.Scenario.ClimateRange.MinHumidity, fixture.Scenario.ClimateRange.MaxHumidity));
        Assert.True(after < before, "Die Halle kühlt nachts ab, das Füllgewicht steigt dennoch.");
    }

    [Fact]
    public void Ovens_are_unevenly_occupied_not_too_small()
    {
        var of01 = fixture.Live.Oven("OF-01", fixture.Now);
        var of02 = fixture.Live.Oven("OF-02", fixture.Now);

        Assert.Equal(78, OeeResult.Percent(of01.OccupancySinceShiftStart));
        Assert.Equal(96, OeeResult.Percent(of02.OccupancySinceShiftStart));
        Assert.Equal(of01.HoldTemperatureCelsius, of02.HoldTemperatureCelsius);
        Assert.Equal(42, fixture.Live.Buffer(fixture.Now).Plates);
    }

    [Fact]
    public void RT02_mould_can_be_bundled_with_the_setup_at_1130()
    {
        var snapshot = fixture.Live.Station("RT-02", fixture.Now);
        var wear = snapshot.Mould!.Wear(fixture.Now);
        var atSetup = MouldWear.WearAt(wear, snapshot.Mould.StrokesPerMinute, fixture.At.Oct(15, "11:30") - fixture.Now);
        var setup = fixture.Scenario.Setups.Single(s => s.StationId == "RT-02" && s.StartUtc > fixture.Now);
        var order = fixture.Scenario.Order("4698");

        Assert.Equal(fixture.At.Oct(15, "11:30"), setup.StartUtc);
        Assert.InRange(atSetup, 0.97, 0.985);
        Assert.True(wear.LimitReachedUtc > setup.StartUtc);
        Assert.Equal(fixture.At.Oct(15, "11:20"), order.PlannedEndUtc);
    }

    [Fact]
    public void Order_4698_reaches_its_quantity_at_1120_in_the_live_continuation()
    {
        var model = fixture.Generator.StationModel("RT-02");
        var order = fixture.Scenario.Order("4698");
        var reached = model.QuantityReachedAt(fixture.At.Oct(13, "06:00"), fixture.At.Oct(15, "12:00"), order.Quantity);

        Assert.InRange(reached!.Value, fixture.At.Oct(15, "11:19"), fixture.At.Oct(15, "11:20"));
    }

    [Fact]
    public void F_180_03_forecast_reaches_its_limit_around_wednesday_21_10()
    {
        var mould = fixture.Scenario.Mould("F-180-03");
        var snapshot = fixture.Live.Station("RT-04", fixture.Now);

        var day = MouldWear.ForecastLimitDay(snapshot.Mould!.Strokes, mould.StrokeLimit, mould.ForecastStrokesPerDay, new DateOnly(2026, 10, 15), fixture.Calendar);

        Assert.Equal(4_100, mould.ForecastStrokesPerDay);
        Assert.Equal(new DateOnly(2026, 10, 21), day);
    }
}
