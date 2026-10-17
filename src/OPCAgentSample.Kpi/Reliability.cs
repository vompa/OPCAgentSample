using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>
/// Zuverlässigkeit: MTBF = Laufzeit / Anzahl Störungen; MTTR = Störungsdauer / Anzahl Störungen
/// (Störung = Kategorie Breakdown, gezählt nach Beginn). Anteil ungeplant = Störungsdauer / (Störungsdauer +
/// geplante Wartung), die Wartung auch außerhalb der Schichtzeit (z. B. Wochenende).
/// </summary>
public sealed record ReliabilityResult(TimeSpan? Mtbf, TimeSpan? Mttr, double UnplannedShare, int Failures);

public static class Reliability
{
    public static ReliabilityResult From(ProductionFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        var failures = figures.BreakdownCount;
        var mtbf = failures > 0 ? figures.RunTime / failures : (TimeSpan?)null;
        var mttr = failures > 0 ? figures.BreakdownTime / failures : (TimeSpan?)null;
        var maintenanceRelated = figures.BreakdownTime + figures.MaintenanceTime;
        var share = maintenanceRelated > TimeSpan.Zero ? figures.BreakdownTime / maintenanceRelated : 0;
        return new ReliabilityResult(mtbf, mttr, share, failures);
    }

    /// <summary>Verdichtet Tageskennzahlen (Historie B) zu einer Summe.</summary>
    public static ProductionFigures Sum(IEnumerable<DailyStationKpi> days) =>
        ProductionFigures.Sum(days.Select(day => day.Figures));
}
