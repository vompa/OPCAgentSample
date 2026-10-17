using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;

namespace OPCAgentSample.Server.Kpis;

/// <summary>Ein Kennzahlenblock mit Herkunft (<see cref="SourceLabel"/>) und dem Kennzeichen, ob die Periode abgeschlossen ist.</summary>
public sealed record KpiBlockResult(KpiBlock Block, bool IsFinal, string SourceLabel);

/// <summary>
/// Einstieg für alle Leser der Kennzahlenblöcke der Halle A (Weboberfläche, MCP-Tools): Schichten, Tage und Wochen liegen als
/// verdichtete Zähler in Redis; jede Sicht (Station, Bereich, Halle) entsteht daraus durch Addition.
/// </summary>
public sealed class KpiBlockService(RollupReader reader)
{
    public const string SourceLabel = "Redis";

    private static readonly IReadOnlyList<string> s_stations = [.. PlantLayout.HallAStations.Select(s => s.Id)];

    public async Task<KpiBlockResult> GetAsync(ProductionPeriod period, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        cancellationToken.ThrowIfCancellationRequested();
        var level = period.Kind switch
        {
            PeriodKind.Shift => RollupLevel.Shift,
            PeriodKind.Day => RollupLevel.Day,
            PeriodKind.Week => RollupLevel.Week,
            _ => throw new ArgumentException("Stundenblöcke gibt es nicht; Kennzahlen liegen je Schicht, Tag und Woche vor.", nameof(period)),
        };
        var stored = await reader.ReadBlockAsync(s_stations, level, period.Key);
        return new KpiBlockResult(stored.Block, !stored.IsOpen, SourceLabel);
    }
}
