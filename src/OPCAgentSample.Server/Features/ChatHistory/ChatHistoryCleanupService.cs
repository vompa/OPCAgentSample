using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Configuration;

namespace OPCAgentSample.Server.Features.ChatHistory;

/// <summary>Räumt in festen Abständen Sitzungen auf, die länger als die Aufbewahrungsfrist unbenutzt sind (Standard 30 Tage).</summary>
public sealed partial class ChatHistoryCleanupService(
    ChatHistoryService history,
    IOptions<ChatHistoryOptions> options,
    ILogger<ChatHistoryCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        do
        {
            await PurgeOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await history.PurgeExpiredAsync(cancellationToken);
            if (deleted > 0) LogPurged(deleted, options.Value.RetentionDays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(ex);
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} Sitzungen ohne Aktivität seit {Days} Tagen gelöscht.")]
    private partial void LogPurged(int count, int days);

    [LoggerMessage(Level = LogLevel.Error, Message = "Aufräumen des Verlaufs fehlgeschlagen.")]
    private partial void LogFailed(Exception exception);
}
