using System.Globalization;
using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Mcp;

namespace OPCAgentSample.Server.Live;

/// <summary>
/// Berechnet den Zustand jeder Station zum Datenstand aus Redis (letzter Zustandswechsel, Ringpuffer der letzten Takte,
/// Minutenreihen) und dem laufenden Schichtblock. Die Ampel kommt aus <see cref="TrafficLightRules"/>, Ist/Soll,
/// Ofenbelegung und Materialreichweite aus der Kennzahlen-Bibliothek; hier steht keine zweite Kennzahl-Logik.
/// </summary>
public sealed class StationStateBuilder(
    RollupReader reader, PlantQueries queries, MasterDataStore masterData, ShiftCalendar calendar, IOptions<PlantOptions> plant)
{
    private static readonly TimeSpan s_consumptionWindow = TimeSpan.FromMinutes(30);
    private const int MinConsumptionSamples = 10;

    public async Task<StationSnapshot> BuildAsync(DateTimeOffset untilUtc, KpiBlock shiftBlock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shiftBlock);
        var shift = calendar.ShiftPeriodOf(untilUtc);
        var data = await masterData.GetAsync(cancellationToken);
        var facts = await StationFacts.LoadAsync(reader, queries, data, calendar, untilUtc, shift, plant.Value);

        var stations = PlantLayout.HallAStations.Select(station => station.Type switch
        {
            StationType.RotaryTable or StationType.Packaging => BuildProduction(station, facts, shiftBlock, untilUtc, shift),
            StationType.Oven => BuildOven(station, facts, untilUtc, shift),
            _ => BuildBuffer(station, facts, untilUtc),
        }).ToList();
        return new StationSnapshot(stations, BuildClimate(facts, untilUtc), untilUtc);
    }

    private StationState BuildProduction(Station station, StationFacts facts, KpiBlock block, DateTimeOffset until, ProductionPeriod shift)
    {
        var isPress = station.Type == StationType.RotaryTable;
        var (state, since, orderId) = facts.StateOf(station.Id, until);
        var figures = block.Figures(station.Id);
        double? planRatio = figures.PlannedQuantity > 0 ? PlanFulfillment.From(figures).Ratio : null;
        var cycles = isPress ? facts.Cycles.GetValueOrDefault(station.Id) : null;
        var articleId = isPress ? cycles?.ArticleId : facts.PackagingArticles.GetValueOrDefault(station.Id);
        var article = articleId is null ? null : facts.Articles.GetValueOrDefault(articleId);

        double? cycleDeviation = cycles is { MeanCycleSeconds: { } mean, TargetCycleSeconds: > 0 } ? (mean / cycles.TargetCycleSeconds) - 1 : null;
        double? fillTarget = article is { FillWeightTargetGrams: > 0 } ? article.FillWeightTargetGrams : null;
        double? fillDeviation = cycles?.FillWeightMeanGrams is { } fillMean && fillTarget is { } target ? fillMean - target : null;

        var mould = isPress ? facts.MouldOf(station.Id) : null;
        var wear = mould is null ? null : MouldWear.Compute(mould.Strokes, mould.StrokeLimit, mould.StrokesPerMinute, until);
        var reach = ShortestReach(station, facts, cycles?.BatchId, until);

        var signals = new StationSignals(state, planRatio, orderId is not null, cycleDeviation, fillDeviation, wear?.Wear, reach?.Reach.TotalMinutes);
        var assessment = TrafficLightRules.Evaluate(signals);
        var draft = new StationState(
            station.Id, station.Area, state, since, orderId, articleId, ArticleLabel(station, article, null, null),
            assessment.Light, [.. assessment.Findings.Select(f => new StationFinding(f.Light, f.Text))],
            $"{OeeResult.Percent(planRatio ?? 0)} %", "Ist/Soll", string.Empty,
            figures.TotalPieces, (double)figures.PlannedQuantity, planRatio,
            cycles?.MeanCycleSeconds, cycles?.TargetCycleSeconds, cycles?.MeanBuildUpSeconds, cycles?.FillWeightMeanGrams, fillTarget,
            mould?.MouldId, wear?.Wear, wear?.LimitReachedUtc, reach?.Reach.TotalMinutes, reach?.Material,
            null, null, null, null, null, null, until);
        return draft with { Message = StationMessages.Compose(draft, station, facts.BreakdownStations.Contains(station.Id), ShiftStartText(shift)) };
    }

    private StationState BuildOven(Station station, StationFacts facts, DateTimeOffset until, ProductionPeriod shift)
    {
        var (state, since, orderId) = facts.StateOf(station.Id, until);
        var places = plant.Value.OvenPlaces;
        var occupancy = facts.OvenOccupancies.GetValueOrDefault(station.Id)?.Ratio ?? 0;
        var occupied = (int?)facts.LastValue(station.Id, MinuteVariables.OccupiedPlaces);
        var zone = facts.LastValue(station.Id, MinuteVariables.Zone2Temperature);
        var assessment = TrafficLightRules.Evaluate(new StationSignals(state, OvenOccupancy: occupancy));
        var draft = new StationState(
            station.Id, station.Area, state, since, orderId, null, ArticleLabel(station, null, occupied, zone),
            assessment.Light, [.. assessment.Findings.Select(f => new StationFinding(f.Light, f.Text))],
            $"{OeeResult.Percent(occupancy)} %", "Belegung", string.Empty,
            0, 0, null, null, null, null, null, null, null, null, null, null, null,
            occupancy, occupied, places, zone, null, null, until);
        return draft with { Message = StationMessages.Compose(draft, station, breakdownInShift: false, ShiftStartText(shift)) };
    }

    private static StationState BuildBuffer(Station station, StationFacts facts, DateTimeOffset until)
    {
        var (state, since, orderId) = facts.StateOf(station.Id, until);
        var plates = (int?)facts.LastValue(station.Id, MinuteVariables.PlateCount);
        var wait = facts.LastValue(station.Id, MinuteVariables.MeanWaitMinutes);
        var message = plates is { } count && wait is { } minutes
            ? $"{count} Platten · Ø {(int)Math.Round(minutes)} min Wartezeit"
            : "Keine Daten";
        return new StationState(
            station.Id, station.Area, state, since, orderId, null, "Platten vor den Öfen",
            TrafficLight.Running, [], plates is { } p ? $"{p} Platten" : "–", "Platten", message,
            0, 0, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            plates, wait, until);
    }

    private ClimateState? BuildClimate(StationFacts facts, DateTimeOffset until)
    {
        var celsius = facts.LastValue(MinuteVariables.HallAClimateSource, MinuteVariables.Temperature);
        var humidity = facts.LastValue(MinuteVariables.HallAClimateSource, MinuteVariables.Humidity);
        if (celsius is not { } temperature || humidity is not { } relative) return null;

        var options = plant.Value;
        var inRange = temperature >= options.ClimateMinCelsius && temperature <= options.ClimateMaxCelsius
            && relative >= options.ClimateMinHumidity && relative <= options.ClimateMaxHumidity;
        return new ClimateState(temperature, relative, facts.ClimateAtShiftStart ?? temperature, inRange, until);
    }

    /// <summary>Kürzeste Reichweite aller Materialien der Station (Füllstand und Verbrauch aus den Minutenwerten der letzten halben Stunde).</summary>
    private MaterialReachResult? ShortestReach(Station station, StationFacts facts, string? batchId, DateTimeOffset until)
    {
        var results = new List<MaterialReachResult>();
        foreach (var variable in MaterialVariables(station))
        {
            var series = facts.Series(station.Id, variable);
            if (series.Count == 0) continue;

            var level = series[^1].Value;
            var consumption = ConsumptionPerMinute(series);
            if (consumption <= 0) continue;

            DateTimeOffset? processableUntil = variable == MinuteVariables.MixtureLevelKg ? LastDelivery(series)?.Add(plant.Value.MixturePotLife) : null;
            results.Add(MaterialReach.Compute(MaterialLabel(variable, batchId is null ? null : facts.BatchGrit.GetValueOrDefault(batchId)), level, consumption, processableUntil, until));
        }

        return results.OrderBy(r => r.Reach).FirstOrDefault();
    }

    /// <summary>Verbrauch je Minute: Summe der Rückgänge in den letzten Minutenwerten (Nachfüllen zählt nicht).</summary>
    private static double ConsumptionPerMinute(IReadOnlyList<(DateTimeOffset At, double Value)> series)
    {
        var window = series.Skip(Math.Max(0, series.Count - (int)s_consumptionWindow.TotalMinutes)).ToList();
        if (window.Count < MinConsumptionSamples) return 0;

        var used = 0.0;
        for (var i = 1; i < window.Count; i++)
        {
            used += Math.Max(0, window[i - 1].Value - window[i].Value);
        }

        return used / (window.Count - 1);
    }

    /// <summary>Zeitpunkt der letzten Mischungslieferung: der letzte Sprung des Füllstands nach oben.</summary>
    private DateTimeOffset? LastDelivery(IReadOnlyList<(DateTimeOffset At, double Value)> series)
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

    private static string MaterialLabel(string variable, string? grit) => variable switch
    {
        MinuteVariables.MixtureLevelKg => grit is null ? "Mischung" : $"Mischung {grit}",
        MinuteVariables.FabricMagazine => "Gewebe",
        MinuteVariables.LabelMagazine => "Etiketten",
        MinuteVariables.CartonMagazine => "Kartons",
        _ => variable,
    };

    private string ArticleLabel(Station station, ArticleInfo? article, int? occupied, double? zoneTemperature)
    {
        if (station.Type == StationType.Oven)
        {
            return occupied is { } count && zoneTemperature is { } temperature
                ? string.Create(CultureInfo.InvariantCulture, $"Charge {count}/{plant.Value.OvenPlaces} · {Math.Round(temperature):0} °C")
                : "Keine Daten";
        }

        if (article is null) return string.Empty;
        return station.Type == StationType.Packaging
            ? string.Create(CultureInfo.InvariantCulture, $"Ø {article.DiameterMm} · {article.PiecesPerCarton}er-Karton")
            : string.Create(CultureInfo.InvariantCulture, $"Ø {article.DiameterMm} · {article.Grit}");
    }

    private string ShiftStartText(ProductionPeriod shift) =>
        calendar.ToLocal(shift.StartUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
}
