using OPCAgentSample.Domain.History;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulation.Script;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Puffer vor den Öfen: Plattenzahl und Wartezeit als Verlauf aus dem Drehbuch (linear interpoliert).</summary>
internal sealed class BufferModel(ScenarioDefinition scenario)
{
    public const string StationId = "PU-OFEN";
    private const double OldestToMeanRatio = 1.8;

    public int PlatesAt(DateTimeOffset instant) => (int)Math.Round(Interpolate(scenario.BufferPlates, instant));

    public double MeanWaitAt(DateTimeOffset instant) => Math.Round(Interpolate(scenario.BufferMeanWait, instant), 1);

    public double OldestWaitAt(DateTimeOffset instant) => Math.Round(MeanWaitAt(instant) * OldestToMeanRatio, 1);

    public List<HistoryRecord> Generate(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var records = new List<HistoryRecord>();
        var minute = TimeSpan.FromMinutes(1);
        for (var t = ProductionStationModel.MinuteCeiling(fromUtc); t < untilUtc; t += minute)
        {
            records.Add(new MinuteValue(StationId, t, MinuteVariables.PlateCount, PlatesAt(t)));
            records.Add(new MinuteValue(StationId, t, MinuteVariables.MeanWaitMinutes, MeanWaitAt(t)));
            records.Add(new MinuteValue(StationId, t, MinuteVariables.OldestWaitMinutes, OldestWaitAt(t)));
        }

        return records;
    }

    private static double Interpolate(IReadOnlyList<ProfilePoint> points, DateTimeOffset instant)
    {
        if (instant <= points[0].AtUtc) return points[0].Value;
        for (var i = 1; i < points.Count; i++)
        {
            if (instant > points[i].AtUtc) continue;
            var (a, b) = (points[i - 1], points[i]);
            var share = (instant - a.AtUtc) / (b.AtUtc - a.AtUtc);
            return a.Value + ((b.Value - a.Value) * share);
        }

        return points[^1].Value;
    }
}

/// <summary>
/// Hallenklima mit Tag-Nacht-Verlauf (kühlster Punkt am frühen Morgen) und gegenläufiger Luftfeuchte. Liegt die
/// ganze Woche im Sollbereich und dient Claude als Gegenhypothese zur Füllgewichtsdrift.
/// </summary>
internal sealed class ClimateModel(ScenarioDefinition scenario, int seed)
{
    private const double TemperatureSigma = 0.015;
    private const double HumiditySigma = 0.1;
    private const double HoursPerDay = 24;

    public (double Celsius, double Humidity) At(DateTimeOffset instant, bool withNoise = true)
    {
        var model = scenario.Model;
        var local = scenario.Calendar.ToLocal(instant);
        var hour = local.TimeOfDay.TotalHours;
        var phase = Math.Cos(2 * Math.PI * (hour - model.ClimateColdestHour) / HoursPerDay);
        var noise = withNoise ? MinuteNoise.For(seed, MinuteVariables.HallAClimateSource, instant) : null;
        var celsius = model.ClimateMeanCelsius - (model.ClimateAmplitudeCelsius * phase) + (noise?.Next(TemperatureSigma) ?? 0);
        var humidity = model.HumidityMean + (model.HumidityAmplitude * phase) + (noise?.Next(HumiditySigma) ?? 0);
        return (Math.Round(celsius, 2), Math.Round(humidity, 1));
    }

    public List<HistoryRecord> Generate(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var records = new List<HistoryRecord>();
        var minute = TimeSpan.FromMinutes(1);
        for (var t = ProductionStationModel.MinuteCeiling(fromUtc); t < untilUtc; t += minute)
        {
            var (celsius, humidity) = At(t);
            records.Add(new MinuteValue(MinuteVariables.HallAClimateSource, t, MinuteVariables.Temperature, celsius));
            records.Add(new MinuteValue(MinuteVariables.HallAClimateSource, t, MinuteVariables.Humidity, humidity));
        }

        return records;
    }
}
