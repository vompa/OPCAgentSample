using System.Diagnostics;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Live;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Observability;

namespace OPCAgentSample.Server.Ingestion;

/// <summary>
/// Nimmt Historien-Datensätze einer Quelle auf. Das Zeitfenster wird in Schicht-Fenster geteilt; jedes Fenster verdichtet die
/// <see cref="RollupEngine"/> in einer <c>MULTI</c>/<c>EXEC</c>-Transaktion in Redis und setzt den Wasserstand der Quelle – die
/// Aufnahme ist dadurch wiederholbar: Ein abgebrochener Lauf setzt beim letzten Fenster fort, eine Lücke nach einem Ausfall wird
/// ab dem Wasserstand nachgeladen. Danach bildet der Live-Pfad Zustände, Meldungen und aktive Alarme neu.
/// </summary>
public sealed partial class HistoryIngestor(
    RollupEngine engine,
    RollupReader reader,
    MasterDataStore masterData,
    LivePath live,
    ShiftCalendar calendar,
    ILogger<HistoryIngestor> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Holt alle Datensätze vom Wasserstand der Quelle bis <paramref name="untilUtc"/> (exklusiv).</summary>
    public async Task<IngestResult> IngestAsync(IHistorySource source, DateTimeOffset untilUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Wurde Redis geleert, fehlen auch die Stammdaten: Sie entstehen aus dem Seed neu, die Historie ab dem Beginn der Quelle.
            if (!await masterData.IsSeededAsync()) await masterData.ReseedAsync(cancellationToken);

            var from = await reader.ReadWatermarkAsync(source.SourceId) ?? source.HistoryStartUtc;
            AppTelemetry.RecordWatermark(from);
            if (untilUtc <= from) return IngestResult.Nothing(source.SourceId, from);

            var watch = Stopwatch.StartNew();
            var total = 0L;
            var windows = 0;
            for (var cursor = from; cursor < untilUtc;)
            {
                var end = Earlier(untilUtc, calendar.ShiftPeriodOf(cursor).EndUtc);
                using (var span = AppTelemetry.Source.StartActivity(AppTelemetry.Spans.CollectorIngestWindow))
                {
                    span?.SetTag("ingest.source", source.SourceId);
                    var records = await ReadAsync(source, new TimeWindow(cursor, end), cancellationToken);
                    await engine.ApplyAsync(source.SourceId, new TimeWindow(cursor, end), records, untilUtc, cancellationToken);
                    span?.SetTag("ingest.records", records.Count);
                    AppTelemetry.Ingested(source.SourceId, records.Count);
                    total += records.Count;
                }

                windows++;
                cursor = end;
                AppTelemetry.RecordWatermark(cursor);
            }

            await live.CompleteAsync(untilUtc, cancellationToken);
            LogIngested(source.SourceId, total, windows, watch.Elapsed.TotalSeconds);
            return new IngestResult(source.SourceId, total, windows, from, untilUtc);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<List<HistoryRecord>> ReadAsync(IHistorySource source, TimeWindow window, CancellationToken cancellationToken)
    {
        var records = new List<HistoryRecord>();
        await foreach (var record in source.ReadAsync(window.StartUtc, window.EndUtc, cancellationToken))
        {
            records.Add(record);
        }

        return records;
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    [LoggerMessage(Level = LogLevel.Information, Message = "Quelle {SourceId}: {Records} Datensätze in {Windows} Fenstern aufgenommen ({Seconds:F1} s).")]
    private partial void LogIngested(string sourceId, long records, int windows, double seconds);
}
