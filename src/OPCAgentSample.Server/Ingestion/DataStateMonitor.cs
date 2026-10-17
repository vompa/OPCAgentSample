using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Ingestion;

/// <summary>Zustand der Daten für die Statusleiste: wird die Historie noch geladen, und gibt es einen Hinweis zur Zeitbasis?</summary>
/// <param name="HistoryLoading">Die Historie wird beim Start noch aufgenommen; Zahlen sind unvollständig.</param>
/// <param name="Warning">Hinweistext oder <see langword="null"/>.</param>
public sealed record DataStatus(bool HistoryLoading, string? Warning);

/// <summary>Prüfung „Simulationsuhr liegt hinter dem Wasserstand der Aufnahme“ als reine Funktion.</summary>
public static class DataWatermarkCheck
{
    /// <summary>Toleranz, bis zu der die Uhr hinter dem Wasserstand liegen darf (Messungenauigkeit der Uhr, Tick der Aufnahme).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Liegt die Simulationsuhr deutlich vor dem Wasserstand zurück, wurde der Simulator mit einer älteren Startzeit neu
    /// gestartet: Redis enthält dann Daten „aus der Zukunft“ der Uhr. Ohne Wasserstand gibt es nichts zu prüfen.
    /// </summary>
    public static bool IsClockBehind(DateTimeOffset clockUtc, DateTimeOffset? watermarkUtc, TimeSpan? tolerance = null) =>
        watermarkUtc is { } watermark && clockUtc < watermark - (tolerance ?? Tolerance);

    /// <summary>
    /// Kurzer Hinweistext für das Banner (auf dem Handy höchstens zwei Zeilen): Der Wochentag samt Datum steht nur dabei, wenn
    /// beide Zeitpunkte nicht am selben Tag liegen.
    /// </summary>
    public static string WarningText(DateTimeOffset clockUtc, DateTimeOffset watermarkUtc)
    {
        var sameDay = DemoTime.ToLocal(clockUtc).Date == DemoTime.ToLocal(watermarkUtc).Date;
        string Stamp(DateTimeOffset utc) => sameDay ? DemoTime.TimeText(utc) : DemoTime.DateTimeText(utc);
        return $"Datenstand ({Stamp(watermarkUtc)}) liegt nach der Simulationsuhr ({Stamp(clockUtc)}): "
            + "Simulator neu gestartet? Kennzahlen evtl. unstimmig.";
    }
}

/// <summary>
/// Hält fest, ob die Historie noch geladen wird (gesetzt vom <see cref="IngestionService"/>) und ob die Simulationsuhr hinter
/// dem Wasserstand liegt (Hash <c>wm</c>). Die Prüfung wird kurz zwischengespeichert, damit <c>/api/me</c>
/// Redis nicht bei jedem Aufruf fragt. Es wird nichts gelöscht oder verändert, nur gemeldet.
/// </summary>
public sealed partial class DataStateMonitor(
    RollupReader reader, IDemoClock clock, TimeProvider timeProvider, ILogger<DataStateMonitor> logger)
{
    private static readonly TimeSpan s_cacheDuration = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private volatile bool _loading;
    private bool _warned;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private string? _warning;

    /// <summary>Wird die Historie noch geladen?</summary>
    public bool HistoryLoading => _loading;

    public void BeginLoading() => _loading = true;

    public void EndLoading() => _loading = false;

    /// <summary>Aktueller Zustand; der Hinweis zur Zeitbasis stammt aus der letzten Prüfung (höchstens 15 s alt).</summary>
    public async Task<DataStatus> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        bool due;
        lock (_gate)
        {
            due = now - _checkedAt >= s_cacheDuration;
            if (due) _checkedAt = now;
        }

        if (due) await CheckAsync(cancellationToken);
        lock (_gate) return new DataStatus(_loading, _warning);
    }

    /// <summary>Prüft Uhr und Wasserstand sofort und meldet einen Wechsel des Zustands im Log.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset? watermark;
        try
        {
            watermark = await reader.ReadOverallWatermarkAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCheckFailed(ex.GetType().Name);
            return;
        }

        var now = clock.UtcNow;
        var behind = DataWatermarkCheck.IsClockBehind(now, watermark);
        lock (_gate)
        {
            _warning = behind ? DataWatermarkCheck.WarningText(now, watermark!.Value) : null;
            if (behind && !_warned) LogClockBehind(now, watermark!.Value);
            _warned = behind;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Die Simulationsuhr ({Clock:u}) liegt vor dem Wasserstand der Aufnahme ({Watermark:u}): Der Simulator wurde vermutlich mit einer früheren Startzeit neu gestartet. Es wird nichts gelöscht.")]
    private partial void LogClockBehind(DateTimeOffset clock, DateTimeOffset watermark);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Prüfung des Wasserstands fehlgeschlagen ({Error}); der Hinweis bleibt auf dem letzten Stand.")]
    private partial void LogCheckFailed(string error);
}
