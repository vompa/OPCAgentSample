using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Observability;

namespace OPCAgentSample.Server.Ingestion;

/// <summary>
/// Hintergrunddienst der Datenaufnahme: holt beim Start jede registrierte <see cref="IHistorySource"/> bis zur
/// Simulationsuhr nach (Muster „Lücke auffüllen“) und setzt die Aufnahme periodisch fort. Ist keine Quelle registriert, tut er nichts und loggt das.
/// </summary>
public sealed partial class IngestionService(
    IEnumerable<IHistorySource> sources,
    HistoryIngestor ingestor,
    IDemoClock clock,
    DataStateMonitor dataState,
    IOptions<IngestionOptions> options,
    ILogger<IngestionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AppTelemetry.UseSimulationClock(() => clock.UtcNow);
        var all = sources.ToList();
        if (all.Count == 0)
        {
            LogNoSource();
            return;
        }

        // Der Start des Hosts wartet nicht auf das Aufholen der Historie. Bis die erste Aufnahme gelungen ist, meldet die
        // Weboberfläche „Historie wird geladen“, statt unvollständige Zahlen zu zeigen.
        dataState.BeginLoading();
        await Task.Yield();
        await dataState.CheckAsync(stoppingToken);
        var caughtUp = await IngestAllAsync(all, stoppingToken);
        if (caughtUp) dataState.EndLoading();

        using var timer = new PeriodicTimer(options.Value.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            caughtUp = await IngestAllAsync(all, stoppingToken);
            if (caughtUp) dataState.EndLoading();
        }
    }

    /// <summary>
    /// Nimmt alle Quellen bis zur Simulationsuhr auf; Fehler einer Quelle stoppen weder den Dienst noch die anderen.
    /// Liefert <see langword="true"/>, wenn alle Quellen aufgenommen werden konnten.
    /// </summary>
    public async Task<bool> IngestAllAsync(IReadOnlyList<IHistorySource> all, CancellationToken cancellationToken)
    {
        var allSucceeded = true;
        foreach (var source in all)
        {
            try
            {
                _ = await ingestor.IngestAsync(source, clock.UtcNow, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                allSucceeded = false;
                LogSourceFailed(source.SourceId, ex);
            }
        }

        return allSucceeded;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Keine Historienquelle registriert; die Datenaufnahme ruht.")]
    private partial void LogNoSource();

    [LoggerMessage(Level = LogLevel.Error, Message = "Aufnahme der Quelle {SourceId} ist fehlgeschlagen; neuer Versuch im nächsten Intervall.")]
    private partial void LogSourceFailed(string sourceId, Exception exception);
}
