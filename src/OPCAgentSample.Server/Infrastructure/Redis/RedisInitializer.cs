using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.MasterData;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Infrastructure.Redis;

/// <summary>
/// Spielt beim Start die Stammdaten aus dem Seed nach Redis ein (fehlen sie dort) und lädt sie in den Prozess. Fährt Redis noch
/// hoch (z. B. nach <c>docker compose up</c>), wird mehrfach versucht. Als erster Hosted Service registriert, damit alle anderen
/// beim Start die Stammdaten schon vorfinden.
/// </summary>
public sealed partial class RedisInitializer(
    MasterDataStore masterData,
    IOptions<RedisOptions> options,
    ILogger<RedisInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _ = await masterData.GetAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (IsNotReadyYet(ex) && attempt < settings.StartupAttempts)
            {
                LogRetry(attempt, settings.StartupAttempts, ex.GetType().Name);
                await Task.Delay(settings.StartupRetryDelay, cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IsNotReadyYet(Exception ex) =>
        ex is RedisException or TimeoutException or System.Net.Sockets.SocketException
        || ex.InnerException is RedisException or System.Net.Sockets.SocketException;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis noch nicht bereit (Versuch {Attempt} von {Max}, {Error}); neuer Versuch.")]
    private partial void LogRetry(int attempt, int max, string error);
}
