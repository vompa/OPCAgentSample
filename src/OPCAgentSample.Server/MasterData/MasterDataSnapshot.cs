using OPCAgentSample.Kpi;

namespace OPCAgentSample.Server.MasterData;

/// <summary>
/// Die Stammdaten der Anlage als unveränderlicher Speicherauszug im Prozess (Artikel, Mischungen, Formen, Aufträge, Kostensätze,
/// Wartung, Rollen, Benutzer, Regeln, Kalender). Sie entstehen beim Start aus den Seed-Dateien, liegen in Redis (<c>md:*</c>) und
/// ändern sich zur Laufzeit nicht; veränderlich sind nur Zähler (<c>ord:*</c>, <c>mould:*</c>) und Favoriten.
/// </summary>
public sealed class MasterDataSnapshot
{
    public required IReadOnlyDictionary<string, Article> Articles { get; init; }

    public required IReadOnlyDictionary<string, Mixture> Mixtures { get; init; }

    public required IReadOnlyDictionary<string, MixtureBatch> MixtureBatches { get; init; }

    public required IReadOnlyList<Mould> Moulds { get; init; }

    public required IReadOnlyList<ProductionOrder> Orders { get; init; }

    public required IReadOnlyList<CostRate> CostRates { get; init; }

    public required IReadOnlyList<MaintenancePlan> MaintenancePlans { get; init; }

    public required IReadOnlyList<MaintenanceHistory> MaintenanceHistory { get; init; }

    public required IReadOnlyDictionary<string, AppRole> Roles { get; init; }

    public required IReadOnlyList<AppUser> Users { get; init; }

    public required IReadOnlyDictionary<string, EscalationRule> EscalationRules { get; init; }

    public required IReadOnlyList<Holiday> Holidays { get; init; }

    public required IReadOnlyList<CalendarDay> CalendarDays { get; init; }

    /// <summary>Körnung (z. B. A60) einer Mischungscharge oder <see langword="null"/>.</summary>
    public string? GritOfBatch(string batchId) =>
        MixtureBatches.TryGetValue(batchId, out var batch) && Mixtures.TryGetValue(batch.MixtureId, out var mixture) ? mixture.Grit : null;

    /// <summary>Kostensätze (€ je Stillstandsminute je Station, € je Ausschussstück je Artikel) für die Kennzahlen-Bibliothek.</summary>
    public CostRateTable CostTable(IReadOnlyDictionary<string, decimal>? extraDowntimeRates = null)
    {
        var downtime = CostRates
            .Where(r => r.Kind == CostRateKind.DowntimeMinute && r.StationId is not null)
            .ToDictionary(r => r.StationId!, r => r.Euro);
        var scrap = CostRates
            .Where(r => r.Kind == CostRateKind.ScrapPiece && r.ArticleId is not null)
            .ToDictionary(r => r.ArticleId!, r => r.Euro);
        foreach (var (station, euro) in extraDowntimeRates ?? new Dictionary<string, decimal>()) downtime[station] = euro;

        return new CostRateTable(downtime, scrap);
    }
}
