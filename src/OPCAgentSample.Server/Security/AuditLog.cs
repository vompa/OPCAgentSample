using System.Security.Claims;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Security;

public static class AuditChannels
{
    public const string Mcp = "mcp";
    public const string Rest = "rest";
}

public static class AuditOutcomes
{
    public const string Ok = "ok";
    public const string Denied = "denied";
    public const string Error = "error";
}

/// <summary>
/// Zentrale Stelle für das Audit-Log: eine Logzeile und ein Eintrag im Stream <c>audit</c> je Zugriff (die letzten 20 000).
/// Ein Fehler beim Schreiben bricht die Anfrage nicht ab, wird aber als Fehler geloggt.
/// </summary>
public sealed partial class AuditLog(
    IConnectionMultiplexer redis,
    TimeProvider timeProvider,
    ILogger<AuditLog> logger)
{
    public const int MaxTextLength = 300;

    /// <summary>Höchstzahl der Einträge im Stream.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>Länge eines gespeicherten Textes: 300 Zeichen plus Kürzungszeichen.</summary>
    public const int StoredTextLength = MaxTextLength + 3;

    private const string Unknown = "unbekannt";

    public async Task RecordAsync(
        string channel,
        ClaimsPrincipal? user,
        string action,
        string? arguments,
        string outcome,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        var entry = new AuditEntry(
            timeProvider.GetUtcNow(),
            user?.Identity?.Name ?? Unknown,
            user?.FindFirst(ClaimTypes.Role)?.Value ?? Unknown,
            channel,
            action,
            Shorten(arguments),
            outcome,
            Shorten(error));

        LogEntry(entry.Channel, entry.Client, entry.Role, entry.Action, entry.Outcome);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await redis.GetDatabase().StreamAddAsync(
                RedisKeys.Audit,
                [
                    new("ts", entry.TimestampUtc.UtcTicks), new("client", entry.Client), new("role", entry.Role),
                    new("channel", entry.Channel), new("action", entry.Action), new("args", entry.Arguments ?? string.Empty),
                    new("outcome", entry.Outcome), new("error", entry.Error ?? string.Empty),
                ],
                maxLength: MaxEntries,
                useApproximateMaxLength: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreFailed(ex, action);
        }
    }

    internal static string? Shorten(string? text) =>
        text is { Length: > MaxTextLength } ? text[..MaxTextLength] + "..." : text;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Audit: {Channel} Client {Client} ({Role}) {Action} -> {Outcome}")]
    private partial void LogEntry(string channel, string client, string role, string action, string outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit-Eintrag konnte nicht gespeichert werden ({Action})")]
    private partial void LogStoreFailed(Exception exception, string action);
}

/// <summary>Ein Eintrag des Audit-Logs.</summary>
public sealed record AuditEntry(
    DateTimeOffset TimestampUtc, string Client, string Role, string Channel, string Action, string? Arguments, string Outcome, string? Error);
