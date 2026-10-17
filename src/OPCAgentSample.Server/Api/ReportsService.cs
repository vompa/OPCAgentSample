using Microsoft.Extensions.Options;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Features.ChatHistory;
using OPCAgentSample.Server.Observability;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// Berechnet die Zusammenfassung der Report-Seite aus den Nutzungszählern (<see cref="UsageStore"/>, Zähler statt Abfragen über alle
/// Fragen) und den Zuständen des Servers; das Ergebnis wird kurz zwischengespeichert, damit offene Report-Seiten Redis nicht bei
/// jedem Aufruf belasten. Antwortzeiten und Laufzeiten der Tools sind aus Histogrammen geschätzt.
/// </summary>
public sealed class ReportsService(
    UsageStore usage,
    RollupReader reader,
    IDemoClock demoClock,
    IOptions<ReportsOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    internal const int SeriesHours = 24;
    internal const int SeriesIntervalMinutes = 60;

    /// <summary>Untergrenzen der Behälter des Histogramms der Antwortzeiten in Millisekunden; der letzte Behälter ist offen.</summary>
    internal static int[] HistogramEdgesMs => UsageStore.LatencyEdgesMs;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();
    private ReportsSummary? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public void Dispose() => _gate.Dispose();

    public async Task<ReportsSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (_cached is { } fresh && now - _cachedAt < options.Value.CacheDuration) return fresh;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (_cached is { } again && now - _cachedAt < options.Value.CacheDuration) return again;

            _cached = await BuildAsync(now);
            _cachedAt = now;
            return _cached;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private async Task<ReportsSummary> BuildAsync(DateTimeOffset now)
    {
        var settings = options.Value;
        var utc = now.UtcDateTime;
        var seriesStart = new DateTimeOffset(DateTime.SpecifyKind(utc.Date.AddHours(utc.Hour + 1 - SeriesHours), DateTimeKind.Utc));
        var hours = Enumerable.Range(0, SeriesHours).Select(i => seriesStart.AddHours(i)).ToList();

        var totals = await usage.ReadTotalsAsync();
        var today = await usage.ReadDayQuestionsAsync(now);
        var perHour = await usage.ReadHoursAsync(hours);
        var tools = await usage.ReadToolsAsync();
        var watermark = await reader.ReadOverallWatermarkAsync();

        var tokens = new ReportTokens(totals.InputTokens, totals.OutputTokens, totals.CacheReadTokens, totals.CacheWriteTokens);
        var prices = settings.Prices;
        return new ReportsSummary(
            new ReportPeriod(now, totals.FirstQuestionAtUtc, _startedAt, SeriesHours),
            new ReportQuestions(totals.Questions, today, perHour.Sum(h => h.Questions), totals.AnsweredByClaude),
            tokens,
            new ReportCost(EstimateCostUsd(tokens, prices), CacheHitRate(tokens), prices),
            new ReportLatency(
                totals.Measured, totals.Measured > 0 ? totals.DurationSumMs / totals.Measured : null,
                UsageStore.Percentile(totals.LatencyBins, 0.5), UsageStore.Percentile(totals.LatencyBins, 0.95)),
            Histogram(totals.LatencyBins),
            new ReportTimeSeries(
                seriesStart, SeriesIntervalMinutes, [.. perHour.Select(h => h.Questions)], [.. perHour.Select(h => h.Input)], [.. perHour.Select(h => h.Output)]),
            [.. tools
                .OrderByDescending(t => t.Calls).ThenBy(t => t.Tool, StringComparer.Ordinal).ThenBy(t => t.Role, StringComparer.Ordinal)
                .ThenBy(t => t.Outcome, StringComparer.Ordinal)
                .Select(t => new ReportToolRow(
                    t.Tool, t.Role, t.Outcome, t.Calls, t.Calls > 0 ? t.DurationSumMs / t.Calls : null, UsageStore.Percentile(t.LatencyBins, 0.95)))],
            new ReportIngestion(
                AppTelemetry.CollectorLagSeconds, demoClock.UtcNow, watermark,
                AppTelemetry.IngestedRecordsSinceStart, AppTelemetry.OpenSseConnections, AppTelemetry.QuotaDenialsSinceStart));
    }

    /// <summary>Geschätzte Kosten in USD aus den Tokens und den Preisen je 1 Mio. Tokens.</summary>
    internal static decimal EstimateCostUsd(ReportTokens tokens, ReportPrices prices) =>
        (tokens.Input * prices.InputPerMillion
            + tokens.Output * prices.OutputPerMillion
            + tokens.CacheRead * prices.CacheReadPerMillion
            + tokens.CacheWrite * prices.CacheWritePerMillion) / 1_000_000m;

    /// <summary>Anteil des aus dem Cache gelesenen Teils der Eingabe; <see langword="null"/>, wenn nichts gelesen oder eingegeben wurde.</summary>
    internal static double? CacheHitRate(ReportTokens tokens) =>
        tokens.CacheRead + tokens.Input is var total and > 0 ? (double)tokens.CacheRead / total : null;

    private static List<ReportHistogramBucket> Histogram(IReadOnlyList<long> counts)
    {
        var edges = HistogramEdgesMs;
        var buckets = new List<ReportHistogramBucket> { new(0, edges[0], counts[0]) };
        for (var i = 0; i < edges.Length; i++)
        {
            int? upper = i + 1 < edges.Length ? edges[i + 1] : null;
            buckets.Add(new ReportHistogramBucket(edges[i], upper, counts[i + 1]));
        }

        return buckets;
    }
}
