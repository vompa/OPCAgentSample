using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Reichweite eines Materials an einer Station: Füllstand, Verbrauch und – bei Mischungen – die Verarbeitbarkeit.</summary>
public sealed record MaterialReachRow(
    string StationId, string Material, string Unit, double Level, double ConsumptionPerMinute,
    MaterialReachResult? Result, DateTimeOffset? ProcessableUntilUtc)
{
    public double? ReachMinutes => Result is { } r && r.Reach != TimeSpan.MaxValue ? r.Reach.TotalMinutes : null;

    public string RatingCode => ReachMinutes switch
    {
        null => Rating.Ok,
        < 15 => Rating.Crit,
        < 60 => Rating.Warn,
        _ => Rating.Ok,
    };
}

/// <summary>
/// Materialreichweite aller Materialien der Rundtische und Verpackungen aus den Minutenwerten der letzten halben Stunde:
/// Reichweite = Füllstand / Verbrauch, bei Mischungen begrenzt zusätzlich „verarbeitbar bis“ (letzte Lieferung + Topfzeit).
/// Die Rechnung (<see cref="MaterialReach.Compute"/>) und die Grenzwerte (<see cref="TrafficLightThresholds"/>) liegen in der
/// Kennzahlen-Bibliothek; hier werden nur die Messwerte gelesen.
/// </summary>
public sealed class MaterialReachService(PlantQueries queries, IOptions<PlantOptions> plant)
{
    private const int ConsumptionMinutes = 30;
    private const int MinSamples = 10;
    private static readonly TimeSpan s_window = TimeSpan.FromMinutes(31);
    private static readonly TimeSpan s_mixtureWindow = TimeSpan.FromHours(4);

    public async Task<IReadOnlyList<MaterialReachRow>> GetAsync(IEnumerable<Station> stations, DateTimeOffset until)
    {
        var list = stations.Where(s => s.Type is StationType.RotaryTable or StationType.Packaging).ToList();
        if (list.Count == 0) return [];

        var variables = new[]
        {
            MinuteVariables.MixtureLevelKg, MinuteVariables.FabricMagazine, MinuteVariables.LabelMagazine, MinuteVariables.CartonMagazine,
        };
        var ids = list.Select(s => s.Id).ToList();
        var series = await queries.MinuteSeriesAsync(ids, variables, until - s_mixtureWindow, until);
        var batches = await queries.LatestBatchesAsync(list.Where(s => s.Type == StationType.RotaryTable).Select(s => s.Id).ToList(), until);

        var rows = new List<MaterialReachRow>();
        foreach (var station in list)
        {
            foreach (var variable in MaterialVariables(station))
            {
                if (!series.TryGetValue((station.Id, variable), out var points) || points.Count == 0) continue;

                var recent = points.Where(p => p.At >= until - s_window).ToList();
                var level = points[^1].Value;
                var consumption = ConsumptionPerMinute(recent);
                var processable = variable == MinuteVariables.MixtureLevelKg ? LastDelivery(points)?.Add(plant.Value.MixturePotLife) : null;
                var label = Label(variable, batches.TryGetValue(station.Id, out var batch) ? batch.Grit : null);
                MaterialReachResult? result = consumption > 0 ? MaterialReach.Compute(label, level, consumption, processable, until) : null;
                rows.Add(new MaterialReachRow(station.Id, label, Unit(variable), level, consumption, result, processable));
            }
        }

        return rows;
    }

    /// <summary>Kürzeste Reichweite je Station (Stationen ohne Verbrauch fehlen).</summary>
    public static IReadOnlyDictionary<string, MaterialReachRow> Shortest(IEnumerable<MaterialReachRow> rows) =>
        rows.Where(r => r.ReachMinutes is not null)
            .GroupBy(r => r.StationId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.ReachMinutes).First());

    private static double ConsumptionPerMinute(List<MinutePoint> recent)
    {
        var window = recent.Skip(Math.Max(0, recent.Count - ConsumptionMinutes)).ToList();
        if (window.Count < MinSamples) return 0;

        var used = 0.0;
        for (var i = 1; i < window.Count; i++)
        {
            used += Math.Max(0, window[i - 1].Value - window[i].Value);
        }

        return used / (window.Count - 1);
    }

    private DateTimeOffset? LastDelivery(List<MinutePoint> series)
    {
        for (var i = series.Count - 1; i > 0; i--)
        {
            if (series[i].Value - series[i - 1].Value > plant.Value.MixtureDeliveryJumpKg) return series[i].At;
        }

        return null;
    }

    private static string[] MaterialVariables(Station station) => station.Type == StationType.RotaryTable
        ? [MinuteVariables.MixtureLevelKg, MinuteVariables.FabricMagazine, MinuteVariables.LabelMagazine]
        : [MinuteVariables.CartonMagazine, MinuteVariables.LabelMagazine];

    private static string Label(string variable, string? grit) => variable switch
    {
        MinuteVariables.MixtureLevelKg => grit is null ? "Mischung" : $"Mischung {grit}",
        MinuteVariables.FabricMagazine => "Gewebe",
        MinuteVariables.LabelMagazine => "Etiketten",
        MinuteVariables.CartonMagazine => "Kartons",
        _ => variable,
    };

    private static string Unit(string variable) => variable == MinuteVariables.MixtureLevelKg ? "kg" : "Stück";
}
