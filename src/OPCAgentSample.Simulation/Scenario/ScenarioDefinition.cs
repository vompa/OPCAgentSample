using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Script;

namespace OPCAgentSample.Simulation.Scenario;

/// <summary>
/// Vollständiges Szenario der Halle A: Stammdaten (der Server spielt sie aus seinen Seed-Dateien nach Redis ein), Modellparameter und
/// Drehbuch. Unveränderlich; der Generator rechnet daraus mit einem Seed reproduzierbar die Historie.
/// </summary>
public sealed class ScenarioDefinition
{
    public required ShiftCalendar Calendar { get; init; }

    /// <summary>Beginn der Rohdaten-Historie (Mo 05.10.2026 00:00 Ortszeit).</summary>
    public required DateTimeOffset HistoryStartUtc { get; init; }

    /// <summary>Demo-Uhr: Do 15.10.2026 09:42 Ortszeit.</summary>
    public required DateTimeOffset DemoTimeUtc { get; init; }

    // Stammdaten
    public required IReadOnlyList<Article> Articles { get; init; }

    public required IReadOnlyList<PackagingArticle> PackagingArticles { get; init; }

    public required IReadOnlyList<MixtureBatch> MixtureBatches { get; init; }

    public required IReadOnlyList<ProductionOrder> Orders { get; init; }

    public required IReadOnlyList<Mould> Moulds { get; init; }

    public required IReadOnlyList<ProductionStationSpec> ProductionStations { get; init; }

    public required IReadOnlyList<OvenSpec> Ovens { get; init; }

    public required IReadOnlyList<ConsumableSpec> Consumables { get; init; }

    public required ClimateRange ClimateRange { get; init; }

    public required ModelParameters Model { get; init; }

    public required DateOnly DailyHistoryStart { get; init; }

    public required IReadOnlyList<ReliabilityTarget> ReliabilityTargets { get; init; }

    public required IReadOnlyList<UnplannedShareTarget> UnplannedShares { get; init; }

    // Drehbuch
    public required IReadOnlyList<ScriptedLoss> Losses { get; init; }

    public required IReadOnlyList<ScriptedSetup> Setups { get; init; }

    public required IReadOnlyList<OrderAssignment> OrderAssignments { get; init; }

    public required IReadOnlyList<BatchAssignment> BatchAssignments { get; init; }

    public required IReadOnlyList<OutputTarget> OutputTargets { get; init; }

    public required IReadOnlyList<ScrapWindow> ScrapWindows { get; init; }

    public required IReadOnlyList<FillWeightShift> FillWeightShifts { get; init; }

    public required IReadOnlyList<BuildUpDrift> BuildUpDrifts { get; init; }

    public required IReadOnlyList<MixtureDelivery> MixtureDeliveries { get; init; }

    public required IReadOnlyList<ScriptedAlarm> Alarms { get; init; }

    public required IReadOnlyList<ProfilePoint> BufferPlates { get; init; }

    public required IReadOnlyList<ProfilePoint> BufferMeanWait { get; init; }

    public required IReadOnlyList<OvenDelay> OvenDelays { get; init; }

    public required IReadOnlyList<ShiftPlanOverride> PlanOverrides { get; init; }

    public required IReadOnlyList<QuietWindow> QuietWindows { get; init; }

    public Article Article(string id) =>
        Articles.FirstOrDefault(a => a.Id == id) ?? throw new ArgumentException($"Unbekannter Artikel '{id}'.", nameof(id));

    public PackagingArticle PackagingArticle(string id) =>
        PackagingArticles.FirstOrDefault(a => a.Id == id)
        ?? throw new ArgumentException($"Unbekannter Verpackungsartikel '{id}'.", nameof(id));

    public ProductionStationSpec Station(string id) =>
        ProductionStations.FirstOrDefault(s => s.StationId == id)
        ?? throw new ArgumentException($"Unbekannte Produktionsstation '{id}'.", nameof(id));

    public ProductionOrder Order(string id) =>
        Orders.FirstOrDefault(o => o.Id == id) ?? throw new ArgumentException($"Unbekannter Auftrag '{id}'.", nameof(id));

    public Mould Mould(string id) =>
        Moulds.FirstOrDefault(m => m.Id == id) ?? throw new ArgumentException($"Unbekannte Form '{id}'.", nameof(id));

    /// <summary>Kostensätze für die Kennzahlen-Bibliothek (Stillstand je Station, Ausschuss je Artikel).</summary>
    public CostRateTable CostRates()
    {
        var downtime = ProductionStations.ToDictionary(s => s.StationId, s => s.DowntimeEuroPerMinute);
        foreach (var oven in Ovens) downtime[oven.StationId] = oven.DowntimeEuroPerMinute;
        var scrap = Articles.ToDictionary(a => a.Id, a => a.ScrapCostEuro);
        foreach (var article in PackagingArticles) scrap[article.Id] = article.ScrapCostEuro;
        return new CostRateTable(downtime, scrap);
    }
}

/// <summary>Benannte Modellkonstanten des Grundrauschens und der Prozesswerte.</summary>
public sealed record ModelParameters(
    int MinorStopsPerShift,
    TimeSpan MinorStopTotalPerShift,
    TimeSpan MinStartupLoss,
    TimeSpan MaxStartupLoss,
    double StartupSpeedFactor,
    double StartupCycleStretch,
    double CycleNoiseSigma,
    double CycleNoiseLimit,
    double FillWeightSigmaGrams,
    double ThicknessSigmaMm,
    double PressForceSigma,
    double BuildUpNominalSeconds,
    double BuildUpSigmaSeconds,
    double BaseScrapRate,
    double StartupScrapRate,
    double PackagingScrapRate,
    double ClimateMeanCelsius,
    double ClimateAmplitudeCelsius,
    double ClimateColdestHour,
    double HumidityMean,
    double HumidityAmplitude);
