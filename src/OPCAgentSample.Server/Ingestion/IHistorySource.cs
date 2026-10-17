using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Server.Ingestion;

/// <summary>
/// Quelle der Historie. In Produktion ist das der OPC-Collector (HistoryRead und Subscriptions), in den Tests ein
/// Generator. Der Server kennt weder Simulator noch Simulation, nur diesen Vertrag.
/// </summary>
public interface IHistorySource
{
    /// <summary>Stabile Kennung der Quelle; unter ihr führt der Hash <c>wm</c> den Wasserstand.</summary>
    string SourceId { get; }

    /// <summary>Frühester Zeitpunkt, ab dem die Quelle Datensätze liefern kann (Beginn des ersten Aufnahmefensters).</summary>
    DateTimeOffset HistoryStartUtc { get; }

    /// <summary>
    /// Alle Datensätze mit Zeitstempel in [<paramref name="fromUtc"/>, <paramref name="untilUtc"/>), sortiert nach Zeit.
    /// Dasselbe Fenster liefert immer dieselben Datensätze (Voraussetzung der idempotenten Aufnahme).
    /// </summary>
    IAsyncEnumerable<HistoryRecord> ReadAsync(DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken = default);
}

/// <summary>Ergebnis einer Aufnahme.</summary>
public sealed record IngestResult(string SourceId, long Records, int Windows, DateTimeOffset FromUtc, DateTimeOffset UntilUtc)
{
    public static IngestResult Nothing(string sourceId, DateTimeOffset watermarkUtc) => new(sourceId, 0, 0, watermarkUtc, watermarkUtc);
}
