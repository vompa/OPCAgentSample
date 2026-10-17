using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Live;

/// <summary>
/// Inhalt einer Live-Verbindung (<c>GET /api/live?role=</c>). Ereignisse (Daten als JSON, camelCase):
/// <list type="bullet">
/// <item><c>station</c>: Anlagenübersicht der Rolle, Format wie <c>GET /api/overview</c></item>
/// <item><c>alarm</c>: <c>{ count, items[] }</c> aktive Alarme, nach Rollenfilter</item>
/// <item><c>feed</c>: eine neue Meldung <c>{ time, source, text, level }</c>; als einziges Ereignis mit <c>id</c> (Redis-Stream-Id)</item>
/// <item><c>inbox</c>: <c>{ open, hasUrgent }</c> Postfach-Zähler des Besitzers</item>
/// <item><c>clock</c>: Uhr der Demo, Format wie <c>clock</c> in <c>GET /api/me</c></item>
/// </list>
/// Reihenfolge nach dem Verbindungsaufbau: <c>retry</c>, nachgelieferte <c>feed</c>-Ereignisse (nur mit <c>Last-Event-ID</c>),
/// dann <c>station</c>, <c>alarm</c>, <c>inbox</c>, <c>clock</c> als Momentaufnahme. Danach nur noch Änderungen; Ereignisse,
/// die sich gegenüber dem zuletzt gesendeten Stand nicht ändern, entfallen. Ohne Schreiben folgt alle
/// <see cref="LiveOptions.HeartbeatInterval"/> der Kommentar <c>: heartbeat</c>. Mit <see cref="LiveOptions.MaxStreamDuration"/>
/// endet die Verbindung nach dieser Dauer mit dem Ereignis <c>idle</c> (<c>{ "reason": "max-duration" }</c>).
/// </summary>
public sealed partial class LiveStreamService(
    EventFeed feed,
    IHallOverviewSource overview,
    IInboxSource inbox,
    IDemoClock clock,
    IOptions<LiveOptions> options,
    ILogger<LiveStreamService> logger)
{
    /// <summary>Name des Ereignisses, mit dem der Server den Strom nach <see cref="LiveOptions.MaxStreamDuration"/> beendet.</summary>
    public const string IdleEvent = "idle";

    private const string IdleJson = "{\"reason\":\"max-duration\"}";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(
        RoleContext context, string? lastEventId, LiveSubscription subscription, SseWriter writer, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var minimumRank = ApiEndpoints.MinimumRank(context.Role.MessageFilter);
        var sent = new SentState();
        var resumeFrom = EventFeed.IsStreamId(lastEventId) ? lastEventId : null;
        var cursor = resumeFrom ?? await feed.LastIdAsync(clock.UtcNow, cancellationToken);

        await writer.WriteRetryAsync(settings.ReconnectDelay, cancellationToken);
        if (resumeFrom is not null) cursor = await SendFeedAsync(cursor, minimumRank, writer, cancellationToken);
        await SendChangesAsync(LiveChangeKind.All & ~LiveChangeKind.Feed, context, minimumRank, sent, writer, cancellationToken);
        await SendClockAsync(sent, writer, cancellationToken);

        var tick = settings.ClockCheckInterval < settings.HeartbeatInterval ? settings.ClockCheckInterval : settings.HeartbeatInterval;
        var started = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            var wait = tick;
            if (settings.MaxStreamDuration is { } maxDuration)
            {
                var remaining = maxDuration - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    await writer.WriteEventAsync(IdleEvent, IdleJson, id: null, cancellationToken);
                    LogIdle(maxDuration.TotalSeconds);
                    return;
                }

                if (remaining < wait) wait = remaining;
            }

            var changes = await subscription.WaitAsync(wait, cancellationToken);
            if ((changes & LiveChangeKind.Feed) != 0) cursor = await SendFeedAsync(cursor, minimumRank, writer, cancellationToken);
            await SendChangesAsync(changes & ~LiveChangeKind.Feed, context, minimumRank, sent, writer, cancellationToken);
            await SendClockAsync(sent, writer, cancellationToken);
            if (writer.IdleTime >= settings.HeartbeatInterval) await writer.WriteCommentAsync("heartbeat", cancellationToken);
        }
    }

    /// <summary>Sendet neue Meldungen nach <paramref name="cursor"/> (gefiltert nach Rolle) und liefert den neuen Stand.</summary>
    private async Task<string> SendFeedAsync(string cursor, int minimumRank, SseWriter writer, CancellationToken cancellationToken)
    {
        var entries = await TryAsync(() => feed.ReadAfterAsync(clock.UtcNow, cursor, cancellationToken), "feed");
        if (entries is null || entries.Count == 0) return cursor;

        foreach (var entry in entries.Where(e => Rating.Rank(e.Entry.Level) >= minimumRank))
        {
            await writer.WriteEventAsync("feed", Serialize(entry.Entry), entry.Id, cancellationToken);
        }

        return entries[^1].Id;
    }

    private async Task SendChangesAsync(
        LiveChangeKind kinds, RoleContext context, int minimumRank, SentState sent, SseWriter writer, CancellationToken cancellationToken)
    {
        if ((kinds & LiveChangeKind.Station) != 0)
        {
            var overviewJson = await TryAsync(
                async () => Serialize(await ApiEndpoints.BuildOverviewAsync(context, overview, clock, cancellationToken)), "station");
            sent.Station = await SendIfChangedAsync("station", overviewJson, sent.Station, writer, cancellationToken);
        }

        if ((kinds & LiveChangeKind.Alarm) != 0)
        {
            var alarmJson = await TryAsync(() => BuildAlarmsAsync(minimumRank, cancellationToken), "alarm");
            sent.Alarm = await SendIfChangedAsync("alarm", alarmJson, sent.Alarm, writer, cancellationToken);
        }

        if ((kinds & LiveChangeKind.Inbox) != 0)
        {
            var inboxJson = await TryAsync(() => BuildInboxAsync(context, cancellationToken), "inbox");
            sent.Inbox = await SendIfChangedAsync("inbox", inboxJson, sent.Inbox, writer, cancellationToken);
        }
    }

    private async Task SendClockAsync(SentState sent, SseWriter writer, CancellationToken cancellationToken)
    {
        var view = ApiEndpoints.ClockOf(clock);
        if (view.LocalText == sent.ClockText) return;

        sent.ClockText = view.LocalText;
        await writer.WriteEventAsync("clock", Serialize(view), id: null, cancellationToken);
    }

    private static async Task<string?> SendIfChangedAsync(
        string name, string? json, string? previous, SseWriter writer, CancellationToken cancellationToken)
    {
        if (json is null || json == previous) return previous;

        await writer.WriteEventAsync(name, json, id: null, cancellationToken);
        return json;
    }

    private async Task<string> BuildAlarmsAsync(int minimumRank, CancellationToken cancellationToken)
    {
        var alarms = (await feed.GetActiveAlarmsAsync(clock.UtcNow, cancellationToken))
            .Where(a => Rating.Rank(RatingOf(a.Severity)) >= minimumRank)
            .ToList();
        return Serialize(new { count = alarms.Count, items = alarms });
    }

    private async Task<string> BuildInboxAsync(RoleContext context, CancellationToken cancellationToken)
    {
        var open = (await inbox.GetAsync(context.Role.Id, context.Owner.Key, cancellationToken)).Where(i => i.TakenBy is null).ToList();
        return Serialize(new { open = open.Count, hasUrgent = open.Any(i => i.Level == Rating.Crit) });
    }

    /// <summary>Severity des Alarms als Bewertung für den Rollenfilter: hoch = kritisch, mittel = Warnung, sonst Info.</summary>
    private static string RatingOf(string severity) => severity switch
    {
        nameof(OPCAgentSample.Domain.History.AlarmSeverity.High) => Rating.Crit,
        nameof(OPCAgentSample.Domain.History.AlarmSeverity.Medium) => Rating.Warn,
        _ => Rating.Info,
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, s_json);

    /// <summary>Ein Fehler beim Berechnen (z. B. Datenbank kurz weg) beendet den Strom nicht; der nächste Anlass versucht es erneut.</summary>
    private async Task<T?> TryAsync<T>(Func<Task<T>> compute, string what)
    {
        try
        {
            return await compute();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogComputeFailed(ex.GetType().Name, what);
            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Live-Strom: nach {Seconds:0} s beendet (idle), damit die Anwendung einschlafen kann.")]
    private partial void LogIdle(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live-Strom: {Error} beim Berechnen von {What}; wird beim nächsten Anlass wiederholt.")]
    private partial void LogComputeFailed(string error, string what);

    /// <summary>Zuletzt gesendeter Stand je Ereignis (Vergleich verhindert doppelte Sendungen).</summary>
    private sealed class SentState
    {
        public string? Station { get; set; }

        public string? Alarm { get; set; }

        public string? Inbox { get; set; }

        public string? ClockText { get; set; }
    }
}
