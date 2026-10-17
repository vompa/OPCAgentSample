using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>
/// Kostensätze (Stammdaten, simuliert): € je Stillstandsminute je Station (entgangener Deckungsbeitrag) und
/// € je Ausschussstück je Artikel.
/// </summary>
public sealed class CostRateTable
{
    private readonly IReadOnlyDictionary<string, decimal> _euroPerDowntimeMinute;
    private readonly IReadOnlyDictionary<string, decimal> _euroPerScrapPiece;

    public CostRateTable(
        IReadOnlyDictionary<string, decimal> euroPerDowntimeMinute,
        IReadOnlyDictionary<string, decimal> euroPerScrapPiece)
    {
        _euroPerDowntimeMinute = euroPerDowntimeMinute;
        _euroPerScrapPiece = euroPerScrapPiece;
    }

    public static CostRateTable None { get; } =
        new(new Dictionary<string, decimal>(), new Dictionary<string, decimal>());

    public decimal DowntimeRate(string stationId) => _euroPerDowntimeMinute.GetValueOrDefault(stationId);

    public decimal ScrapCost(string articleId) => _euroPerScrapPiece.GetValueOrDefault(articleId);

    /// <summary>Kosten einer Stillstandsdauer an einer Station (exakt über Sekunden gerechnet).</summary>
    public decimal DowntimeCost(string stationId, TimeSpan duration) =>
        (long)duration.TotalSeconds * DowntimeRate(stationId) / SecondsPerMinute;

    private const decimal SecondsPerMinute = 60m;
}

/// <summary>Kosten eines Zeitbereichs: Stillstand × Kostensatz + Ausschuss × Stückkosten.</summary>
public sealed record CostBreakdown(decimal DowntimeEuro, decimal ScrapEuro)
{
    public decimal TotalEuro => DowntimeEuro + ScrapEuro;
}

public static class CostCalculator
{
    /// <summary>
    /// Kosten der angegebenen Stationen. Bewertet wird jeder ungeplante Stillstand (Verfügbarkeitsverlust und
    /// Kurzstopps); geplante Stopps, Taktverluste und Überproduktion kosten hier nichts.
    /// </summary>
    public static CostBreakdown Of(KpiBlock block, IEnumerable<string> stationIds, CostRateTable rates)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(rates);
        var downtime = 0m;
        var scrap = 0m;
        foreach (var stationId in stationIds)
        {
            var figures = block.Figures(stationId);
            downtime += rates.DowntimeCost(stationId, figures.AvailabilityLossTime + figures.MinorStopTime);
            scrap += figures.ScrapCostEuro;
        }

        return new CostBreakdown(downtime, scrap);
    }

    /// <summary>Stillstandskosten eines Pareto-Grunds an einer Station.</summary>
    public static decimal ReasonCost(KpiBlock block, string stationId, string reason, CostRateTable rates)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(rates);
        return block.Stations.TryGetValue(stationId, out var station) && station.Reasons.TryGetValue(reason, out var figures)
            ? rates.DowntimeCost(stationId, figures.Duration)
            : 0m;
    }
}
