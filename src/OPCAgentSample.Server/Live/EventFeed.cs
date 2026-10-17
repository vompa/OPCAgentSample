using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Live;

/// <summary>Ein aktiver Alarm (Fault oder Zustandswarnung) für Statusleiste und Tool <c>active_alarms</c>.</summary>
public sealed record ActiveAlarm(string Id, string StationId, string Severity, string Text, DateTimeOffset SinceUtc);

/// <summary>Meldung mit ihrer Stream-Id (Event-Id des Live-Stroms).</summary>
public sealed record StreamedFeedEntry(string Id, FeedEntry Entry);

/// <summary>
/// Meldungen des Produktionstags (<c>ev:A:{prodtag}</c>, Redis-Stream, TTL 48 h) und aktive Alarme
/// (<c>alarm:A:active</c>, Sorted Set nach Severity). Beides ist aus den Alarm-Streams der Verdichtung (<c>al:A</c>,
/// <c>al:A:open</c>) jederzeit neu aufzubauen: Der Live-Pfad schreibt den Tag bei jeder Aufnahme neu (deterministische
/// Eintrags-Ids), der Feed liest bei fehlendem Stream aus den Alarm-Streams.
/// </summary>
public sealed partial class EventFeed(RollupReader reader, RedisAccess redis, ShiftCalendar calendar)
{
    private const string HallId = PlantLayout.HallA;
    private static readonly TimeSpan s_streamTtl = TimeSpan.FromHours(48);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>Schreibt Meldungsstrom des Produktionstags und aktive Alarme neu nach Redis.</summary>
    public async Task SyncAsync(DateTimeOffset untilUtc, CancellationToken cancellationToken)
    {
        var day = calendar.DayPeriodOf(untilUtc);
        var alarms = await LoadDayAsync(day, untilUtc, cancellationToken);
        await WriteStreamAsync(day, alarms);
        await WriteActiveAlarmsAsync(await LoadActiveAsync(untilUtc, cancellationToken));
    }

    /// <summary>Meldungen des Produktionstags, neueste zuerst. Fehlt der Stream in Redis, kommen sie aus den Alarm-Streams.</summary>
    public async Task<IReadOnlyList<FeedEntry>> ReadAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var day = calendar.DayPeriodOf(nowUtc);
        var entries = await redis.TryAsync<IReadOnlyList<FeedEntry>?>(async database =>
        {
            var key = RedisKeys.Events(HallId, day);
            if (!await database.KeyExistsAsync(key)) return null;

            var rows = await database.StreamRangeAsync(key, messageOrder: Order.Descending);
            return (IReadOnlyList<FeedEntry>?)[.. rows.Select(ToEntry)];
        }, null, "feed read");
        if (entries is not null) return entries;

        var alarms = await LoadDayAsync(day, nowUtc, cancellationToken);
        await WriteStreamAsync(day, alarms);
        return [.. alarms.OrderByDescending(a => a.ActiveSinceUtc).ThenByDescending(a => a.Id, StringComparer.Ordinal).Select(a => ToEntry(a, calendar))];
    }

    /// <summary>
    /// Meldungen des Produktionstags mit Stream-Id, aufsteigend, die nach <paramref name="afterId"/> eingetragen wurden
    /// (Live-Strom: Wiederaufnahme über <c>Last-Event-ID</c>). Fehlt der Stream, wird er wie in <see cref="ReadAsync"/> aus
    /// den Alarm-Streams neu aufgebaut; ist Redis nicht erreichbar, ist das Ergebnis leer.
    /// </summary>
    public async Task<IReadOnlyList<StreamedFeedEntry>> ReadAfterAsync(DateTimeOffset nowUtc, string afterId, CancellationToken cancellationToken)
    {
        var day = calendar.DayPeriodOf(nowUtc);
        var rows = await ReadStreamAsync(day, afterId);
        if (rows is null)
        {
            await WriteStreamAsync(day, await LoadDayAsync(day, nowUtc, cancellationToken));
            rows = await ReadStreamAsync(day, afterId) ?? [];
        }

        return [.. rows.Where(r => CompareIds(r.Id.ToString(), afterId) > 0).Select(r => new StreamedFeedEntry(r.Id.ToString(), ToEntry(r)))];
    }

    /// <summary>Id der jüngsten Meldung des Produktionstags (<c>0-0</c>, wenn es keine gibt).</summary>
    public async Task<string> LastIdAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var all = await ReadAfterAsync(nowUtc, "0-0", cancellationToken);
        return all.Count > 0 ? all[^1].Id : "0-0";
    }

    /// <summary>Vergleicht zwei Stream-Ids der Form <c>{millisekunden}-{folge}</c>.</summary>
    public static int CompareIds(string left, string right)
    {
        var (leftMs, leftSeq) = SplitId(left);
        var (rightMs, rightSeq) = SplitId(right);
        var byTime = leftMs.CompareTo(rightMs);
        return byTime != 0 ? byTime : leftSeq.CompareTo(rightSeq);
    }

    /// <summary>Ist <paramref name="id"/> eine Stream-Id der Form <c>{millisekunden}-{folge}</c>?</summary>
    public static bool IsStreamId(string? id) =>
        id is not null && StreamIdPattern().IsMatch(id);

    private static (long Milliseconds, long Sequence) SplitId(string id)
    {
        var parts = id.Split('-', 2);
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
    }

    [GeneratedRegex(@"^\d{1,18}-\d{1,18}$")]
    private static partial Regex StreamIdPattern();

    /// <summary><see langword="null"/>, wenn der Stream fehlt; leer, wenn Redis nicht erreichbar ist.</summary>
    private Task<StreamEntry[]?> ReadStreamAsync(ProductionPeriod day, string afterId) =>
        redis.TryAsync<StreamEntry[]?>(async database =>
        {
            var key = RedisKeys.Events(HallId, day);
            return await database.KeyExistsAsync(key) ? await database.StreamRangeAsync(key, minId: afterId) : null;
        }, [], "feed read after");

    /// <summary>Aktive Alarme nach Severity absteigend (aus Redis, sonst aus den Alarm-Streams).</summary>
    public async Task<IReadOnlyList<ActiveAlarm>> GetActiveAlarmsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var members = await redis.TryAsync(
            database => database.SortedSetRangeByRankAsync(RedisKeys.ActiveAlarms(HallId), order: Order.Descending), [], "alarms read");
        if (members.Length > 0)
        {
            return [.. members.Select(m => JsonSerializer.Deserialize<ActiveAlarm>(m.ToString(), s_json)!)];
        }

        var active = await LoadActiveAsync(nowUtc, cancellationToken);
        await WriteActiveAlarmsAsync(active);
        return [.. active.OrderByDescending(a => a.Severity).Select(ToActive)];
    }

    private Task WriteStreamAsync(ProductionPeriod day, IReadOnlyList<AlarmRecord> alarms) =>
        redis.TryAsync(async database =>
        {
            var key = RedisKeys.Events(HallId, day);
            var transaction = database.CreateTransaction();
            _ = transaction.KeyDeleteAsync(key);
            var lastMilliseconds = -1L;
            var sequence = 0;
            foreach (var alarm in alarms)
            {
                var milliseconds = alarm.ActiveSinceUtc.ToUnixTimeMilliseconds();
                sequence = milliseconds == lastMilliseconds ? sequence + 1 : 0;
                lastMilliseconds = milliseconds;
                var entry = ToEntry(alarm, calendar);
                _ = transaction.StreamAddAsync(key,
                [
                    new NameValueEntry("time", entry.Time),
                    new NameValueEntry("source", entry.Source),
                    new NameValueEntry("text", entry.Text),
                    new NameValueEntry("level", entry.Level),
                    new NameValueEntry("station", alarm.StationId),
                    new NameValueEntry("alarm", alarm.Id),
                ], messageId: string.Create(CultureInfo.InvariantCulture, $"{milliseconds}-{sequence}"));
            }

            _ = transaction.KeyExpireAsync(key, s_streamTtl);
            await transaction.ExecuteAsync();
        }, "feed write");

    private Task WriteActiveAlarmsAsync(IReadOnlyList<AlarmRecord> active) =>
        redis.TryAsync(async database =>
        {
            var key = RedisKeys.ActiveAlarms(HallId);
            var transaction = database.CreateTransaction();
            _ = transaction.KeyDeleteAsync(key);
            if (active.Count > 0)
            {
                _ = transaction.SortedSetAddAsync(key,
                    [.. active.Select(a => new SortedSetEntry(JsonSerializer.Serialize(ToActive(a), s_json), (int)a.Severity))]);
            }

            await transaction.ExecuteAsync();
        }, "alarms write");

    private async Task<IReadOnlyList<AlarmRecord>> LoadDayAsync(ProductionPeriod day, DateTimeOffset until, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var alarms = await reader.ReadAlarmsAsync(day.StartUtc, until + TimeSpan.FromMilliseconds(1), hall: HallId);
        return [.. alarms.Where(a => a.ActiveSinceUtc >= day.StartUtc && a.ActiveSinceUtc <= until)];
    }

    private async Task<IReadOnlyList<AlarmRecord>> LoadActiveAsync(DateTimeOffset until, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var open = await reader.ReadOpenAlarmsAsync(HallId);
        return [.. open.Where(a => a.ActiveSinceUtc <= until && a.Kind is AlarmKind.Fault or AlarmKind.ConditionWarning)];
    }

    private static ActiveAlarm ToActive(AlarmRecord alarm) =>
        new(alarm.Id, alarm.StationId, alarm.Severity.ToString(), alarm.Text, alarm.ActiveSinceUtc);

    private static FeedEntry ToEntry(StreamEntry row)
    {
        string Field(string name) => row.Values.FirstOrDefault(v => v.Name == name).Value.ToString();
        return new FeedEntry(Field("time"), Field("source"), Field("text"), Field("level"));
    }

    /// <summary>
    /// Meldung der Oberfläche zu einem Alarm: Quelle „Station · Art“ (Störung, Zustandswarnung, Material, Qualität, Belegung …)
    /// und Stufe (Störung = kritisch, Warnungen = Warnung, Hinweis = Info).
    /// </summary>
    internal static FeedEntry ToEntry(AlarmRecord alarm, ShiftCalendar calendar) => new(
        calendar.ToLocal(alarm.ActiveSinceUtc).ToString("HH:mm", CultureInfo.InvariantCulture),
        $"{alarm.StationId} · {Label(alarm)}",
        alarm.Text,
        alarm.Kind switch
        {
            AlarmKind.Fault => Rating.Crit,
            AlarmKind.ConditionWarning or AlarmKind.ProcessWarning => Rating.Warn,
            _ => Rating.Info,
        });

    private static string Label(AlarmRecord alarm)
    {
        if (alarm.Kind == AlarmKind.Fault) return "Störung";
        if (alarm.Kind == AlarmKind.ConditionWarning) return "Zustandswarnung";
        if (alarm.Assembly == Assemblies.Material) return "Material";
        if (alarm.Assembly.StartsWith("Gewebezuführung", StringComparison.Ordinal)) return "Störung";
        var oven = PlantLayout.IsKnown(alarm.StationId) && PlantLayout.Get(alarm.StationId).Type == StationType.Oven;
        return alarm.Kind switch
        {
            AlarmKind.ProcessWarning => oven ? "Belegung" : "Qualität",
            _ => "Zustand",
        };
    }
}
