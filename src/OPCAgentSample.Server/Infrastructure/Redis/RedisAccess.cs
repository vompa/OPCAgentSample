using System.Diagnostics;
using OPCAgentSample.Server.Observability;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Infrastructure.Redis;

/// <summary>
/// Fehlertoleranter Zugang zu Redis für Zugriffe, die ausfallen dürfen (Kontingent, Gesundheitsprüfung): Ist Redis nicht
/// erreichbar, liefert der Zugriff den Ersatzwert. Fehler werden geloggt, aber nicht weitergereicht.
/// </summary>
public sealed partial class RedisAccess(IConnectionMultiplexer connection, ILogger<RedisAccess> logger)
{
    public async Task<T> TryAsync<T>(Func<IDatabase, Task<T>> operation, T fallback, string description)
    {
        // Eigener Span, weil es für StackExchange.Redis keine stabile Instrumentierung gibt (nur Beta).
        using var span = AppTelemetry.Source.StartActivity(AppTelemetry.Spans.RedisCommand, ActivityKind.Client);
        span?.SetTag("db.system", "redis");
        span?.SetTag("db.operation", description);
        try
        {
            return await operation(connection.GetDatabase());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            span?.SetStatus(ActivityStatusCode.Error);
            LogUnavailable(ex.GetType().Name, description);
            return fallback;
        }
    }

    public Task TryAsync(Func<IDatabase, Task> operation, string description) =>
        TryAsync(async database =>
        {
            await operation(database);
            return true;
        }, false, description);

    /// <summary>Prüft, ob Redis antwortet (für den Health-Endpunkt).</summary>
    public async Task<bool> IsAvailableAsync() =>
        await TryAsync(async database =>
        {
            await database.PingAsync();
            return true;
        }, false, "ping");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis nicht verfügbar ({Error}) bei {Operation}; der Zugriff wird übersprungen.")]
    private partial void LogUnavailable(string error, string operation);
}
