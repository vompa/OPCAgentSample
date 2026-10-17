namespace OPCAgentSample.Server.Api;

/// <summary>
/// Antwort von <c>GET /api/reports/summary</c>: aggregierte Betriebs- und Nutzungswerte über alle Besitzer. Enthält
/// bewusst weder Frage- und Antworttexte noch Tool-Argumente oder Besitzer-Kennungen.
/// </summary>
public sealed record ReportsSummary(
    ReportPeriod Period,
    ReportQuestions Questions,
    ReportTokens Tokens,
    ReportCost Cost,
    ReportLatency Latency,
    IReadOnlyList<ReportHistogramBucket> LatencyHistogram,
    ReportTimeSeries TimeSeries,
    IReadOnlyList<ReportToolRow> Tools,
    ReportIngestion Ingestion);

/// <param name="FirstQuestionAtUtc">Zeitpunkt der ältesten gespeicherten Frage; <see langword="null"/>, solange noch nichts gefragt wurde.</param>
/// <param name="ServerStartedAtUtc">Start dieses Serverprozesses; die Zähler „seit Start“ beginnen dort.</param>
public sealed record ReportPeriod(
    DateTimeOffset GeneratedAtUtc, DateTimeOffset? FirstQuestionAtUtc, DateTimeOffset ServerStartedAtUtc, int SeriesHours);

/// <param name="Today">Fragen seit 00:00 Uhr UTC.</param>
/// <param name="Last24Hours">Fragen der letzten 24 Stunden.</param>
/// <param name="AnsweredByClaude">Fragen, für die Tokens verbraucht wurden (die übrigen beantwortete die Referenz-Engine).</param>
public sealed record ReportQuestions(long Total, long Today, long Last24Hours, long AnsweredByClaude);

public sealed record ReportTokens(long Input, long Output, long CacheRead, long CacheWrite)
{
    public long Total => Input + Output + CacheRead + CacheWrite;
}

/// <param name="EstimatedUsd">Schätzung aus den Tokens und den konfigurierten Preisen.</param>
/// <param name="CacheHitRate">Cache gelesen / (Cache gelesen + Eingabe); <see langword="null"/> ohne Tokens.</param>
public sealed record ReportCost(decimal EstimatedUsd, double? CacheHitRate, ReportPrices Prices);

/// <summary>Zeiten in Millisekunden; <see langword="null"/>, solange keine Dauer erfasst ist.</summary>
public sealed record ReportLatency(long Measured, double? AverageMs, double? P50Ms, double? P95Ms);

/// <param name="FromMs">Untergrenze (einschließlich).</param>
/// <param name="ToMs">Obergrenze (ausschließlich); <see langword="null"/> beim letzten, nach oben offenen Behälter.</param>
public sealed record ReportHistogramBucket(int FromMs, int? ToMs, long Count);

/// <summary>Zeitreihen der letzten Stunden, ältester Wert zuerst; <see cref="StartUtc"/> ist der Beginn des ersten Intervalls.</summary>
public sealed record ReportTimeSeries(
    DateTimeOffset StartUtc, int IntervalMinutes, IReadOnlyList<long> Questions, IReadOnlyList<long> InputTokens, IReadOnlyList<long> OutputTokens);

/// <summary>Aufrufe eines MCP-Tools in einer Rolle mit einem Ergebnis (<c>ok</c>, <c>denied</c>, <c>error</c>).</summary>
public sealed record ReportToolRow(string Tool, string Role, string Outcome, long Calls, double? AverageMs, double? P95Ms);

/// <param name="LagSeconds">Simulationsuhr minus Wasserstand der Aufnahme; <see langword="null"/>, solange beides fehlt.</param>
/// <param name="IngestedRecordsSinceStart">Seit Serverstart aufgenommene Datensätze.</param>
/// <param name="QuotaDenialsSinceStart">Seit Serverstart vom Tageskontingent abgelehnte Fragen.</param>
public sealed record ReportIngestion(
    double? LagSeconds,
    DateTimeOffset SimulationClockUtc,
    DateTimeOffset? WatermarkUtc,
    long IngestedRecordsSinceStart,
    int LiveConnections,
    long QuotaDenialsSinceStart);
