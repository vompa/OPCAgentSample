using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Features.ChatHistory;

/// <summary>
/// Verlauf der Fragen (Spezifikation Abschnitt 16), ausschließlich in Redis: je Besitzer ein Sorted Set der Sitzungen
/// (<c>chat:s:{owner}</c>), je Sitzung ein Hash (<c>chat:sess:{id}</c>) und ein Stream mit einem Eintrag je Frage
/// (<c>chat:t:{id}</c>); die letzten Fragen stehen in <c>hist:{owner}</c>. Jede Abfrage ist an den Besitzer gebunden: Ein Besitzer
/// sieht nie Sitzungen eines anderen. Aufbewahrung: 30 Tage ab der letzten Frage (TTL und Aufräum-Lauf).
/// </summary>
/// <remarks>
/// Zeitbasis: <c>StartedAtUtc</c> einer Sitzung ist die Simulationszeit (<see cref="IDemoClock"/>), damit die Sitzungsliste
/// dasselbe Datum zeigt wie die Oberfläche. Aufbewahrung (<c>LastActivityUtc</c>), Zeitpunkt der Fragen und die Reihenfolge der
/// Sitzungen folgen der Echtzeit – die Reihenfolge über die zeitgeordnete Kennung (UUIDv7 der Echtzeit), nicht über
/// <c>StartedAtUtc</c>; sonst stünde nach einem Neustart des Simulators mit früherer Startzeit eine neue Sitzung hinter alten.
/// </remarks>
public sealed class ChatHistoryService(
    IConnectionMultiplexer redis,
    IOptions<ChatHistoryOptions> options,
    TimeProvider timeProvider,
    IDemoClock demoClock)
{
    private const int MaxTitleLength = 80;
    private const string NewSessionTitle = "Neue Sitzung";
    private const string TurnField = "d";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private IDatabase Database => redis.GetDatabase();

    private TimeSpan Lifetime => TimeSpan.FromDays(options.Value.RetentionDays + 1);

    /// <summary>
    /// „Neue Sitzung“ (wie <c>/clear</c>). Ist die letzte Sitzung noch leer, wird sie wiederverwendet,
    /// damit kein Stapel leerer Sitzungen entsteht.
    /// </summary>
    public async Task<ChatSessionSummary> StartSessionAsync(ChatOwner owner, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        cancellationToken.ThrowIfCancellationRequested();

        if (await LatestSessionIdAsync(owner) is { } latestId && await ReadSummaryAsync(latestId) is { } latest
            && latest.TurnCount == 0 && latest.Role == role)
        {
            return latest;
        }

        var now = timeProvider.GetUtcNow();
        var sessionId = Guid.CreateVersion7(now);
        var summary = new ChatSessionSummary(sessionId, role, demoClock.UtcNow, now, NewSessionTitle, 0);
        var transaction = Database.CreateTransaction();
        var sessionKey = RedisKeys.ChatSession(sessionId);
        _ = transaction.HashSetAsync(sessionKey,
        [
            new("owner", owner.Key), new("role", role), new("started", summary.StartedAtUtc.UtcTicks), new("last", now.UtcTicks),
            new("title", summary.Title), new("turns", 0),
        ]);
        _ = transaction.KeyExpireAsync(sessionKey, Lifetime);
        _ = transaction.SortedSetAddAsync(RedisKeys.ChatSessions(owner.Key), sessionId.ToString("N"), now.ToUnixTimeMilliseconds());
        _ = transaction.KeyExpireAsync(RedisKeys.ChatSessions(owner.Key), Lifetime);
        await transaction.ExecuteAsync();
        return summary;
    }

    /// <summary>Letzte Sitzung des Besitzers oder, falls es noch keine gibt, eine neue.</summary>
    public async Task<ChatSessionPackage> GetOrStartLatestSessionAsync(ChatOwner owner, string role, CancellationToken cancellationToken = default)
    {
        var latest = await GetLatestSessionAsync(owner, cancellationToken);
        if (latest is not null) return latest;

        var started = await StartSessionAsync(owner, role, cancellationToken);
        return new ChatSessionPackage(started, []);
    }

    /// <summary>Speichert eine Frage mit Antwort in einer Sitzung des Besitzers.</summary>
    /// <exception cref="ChatSessionNotFoundException">Die Sitzung gehört nicht diesem Besitzer.</exception>
    public async Task<ChatTurnRecord> AppendTurnAsync(
        ChatOwner owner, Guid sessionId, NewChatTurn turn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentException.ThrowIfNullOrWhiteSpace(turn.Question);
        if (turn.AnswerCard.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Die Antwortkarte muss ein JSON-Objekt sein.", nameof(turn));
        if (turn.ToolCalls.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Die Tool-Aufrufe müssen ein JSON-Array sein.", nameof(turn));
        cancellationToken.ThrowIfCancellationRequested();

        var database = Database;
        var sessionKey = RedisKeys.ChatSession(sessionId);
        var head = await database.HashGetAsync(sessionKey, ["owner", "role"]);
        if (head[0].IsNull || head[0] != owner.Key) throw new ChatSessionNotFoundException(sessionId);

        var sequence = (int)await database.HashIncrementAsync(sessionKey, "turns");
        var now = timeProvider.GetUtcNow();
        var question = turn.Question.Trim();
        var stored = new StoredTurn(
            sequence, now, question, turn.AnswerCard.Clone(), turn.ToolCalls.Clone(), turn.DataAsOfUtc,
            turn.InputTokens, turn.OutputTokens, turn.CacheReadTokens, turn.CacheWriteTokens, turn.DurationMs);

        var transaction = database.CreateTransaction();
        _ = transaction.StreamAddAsync(RedisKeys.ChatTurns(sessionId), TurnField, JsonSerializer.Serialize(stored, s_json));
        _ = transaction.KeyExpireAsync(RedisKeys.ChatTurns(sessionId), Lifetime);
        _ = transaction.HashSetAsync(sessionKey, "last", now.UtcTicks);
        if (sequence == 1) _ = transaction.HashSetAsync(sessionKey, "title", Shorten(question));
        _ = transaction.KeyExpireAsync(sessionKey, Lifetime);
        _ = transaction.KeyExpireAsync(RedisKeys.ChatSessions(owner.Key), Lifetime);
        var history = RedisKeys.History(owner.Key);
        _ = transaction.SortedSetAddAsync(history, question, now.ToUnixTimeMilliseconds());
        _ = transaction.SortedSetRemoveRangeByRankAsync(history, 0, -(options.Value.RecentQuestionLimit + 1));
        _ = transaction.KeyExpireAsync(history, Lifetime);
        UsageStore.Record(transaction, now, head[1].ToString(), turn);
        await transaction.ExecuteAsync();
        return ToRecord(stored);
    }

    /// <summary>Letzte Sitzung als fertiges Paket oder <see langword="null"/>, wenn es noch keine gibt.</summary>
    public async Task<ChatSessionPackage?> GetLatestSessionAsync(ChatOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        return await LatestSessionIdAsync(owner) is { } id ? await LoadPackageAsync(owner, id) : null;
    }

    /// <summary>Eine frühere Sitzung des Besitzers; für fremde Sitzungen <see langword="null"/>.</summary>
    public Task<ChatSessionPackage?> GetSessionAsync(ChatOwner owner, Guid sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        return LoadPackageAsync(owner, sessionId);
    }

    /// <summary>Sitzungsliste (Datum, erste Frage, Anzahl), neueste zuerst; leere Sitzungen erscheinen nicht.</summary>
    public async Task<IReadOnlyList<ChatSessionSummary>> ListSessionsAsync(
        ChatOwner owner, int take = 50, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var members = await Database.SortedSetRangeByRankAsync(RedisKeys.ChatSessions(owner.Key), order: Order.Descending);
        var summaries = new List<ChatSessionSummary>();
        foreach (var member in members)
        {
            if (summaries.Count == take) break;

            if (await ReadSummaryAsync(Guid.ParseExact(member.ToString(), "N")) is { TurnCount: > 0 } summary) summaries.Add(summary);
        }

        return summaries;
    }

    /// <summary>Die letzten Fragen des Besitzers, neueste zuerst, ohne Wiederholungen (Pfeil hoch/runter, „Zuletzt gefragt“).</summary>
    public async Task<IReadOnlyList<string>> GetRecentQuestionsAsync(ChatOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        cancellationToken.ThrowIfCancellationRequested();
        var questions = await Database.SortedSetRangeByRankAsync(
            RedisKeys.History(owner.Key), 0, options.Value.RecentQuestionLimit - 1, Order.Descending);
        return [.. questions.Select(q => q.ToString())];
    }

    /// <summary>Löscht Sitzungen ohne Aktivität seit der Aufbewahrungsfrist samt Fragen. Gibt die Zahl der Sitzungen zurück.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays);
        var database = Database;
        var deleted = 0;
        foreach (var setKey in await ScanAsync(RedisKeys.ChatSessionsPattern))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = setKey["chat:s:".Length..];
            foreach (var member in await database.SortedSetRangeByRankAsync(setKey))
            {
                var id = Guid.ParseExact(member.ToString(), "N");
                var last = await database.HashGetAsync(RedisKeys.ChatSession(id), "last");
                if (last.HasValue && new DateTimeOffset((long)last, TimeSpan.Zero) >= cutoff) continue;

                await database.KeyDeleteAsync([RedisKeys.ChatSession(id), RedisKeys.ChatTurns(id)]);
                await database.SortedSetRemoveAsync(setKey, member);
                deleted++;
            }

            await database.SortedSetRemoveRangeByScoreAsync(RedisKeys.History(owner), double.NegativeInfinity, cutoff.ToUnixTimeMilliseconds(), Exclude.Stop);
        }

        return deleted;
    }

    private async Task<IReadOnlyList<string>> ScanAsync(string pattern)
    {
        var keys = new List<string>();
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            if (server.IsReplica) continue;

            await foreach (var key in server.KeysAsync(Database.Database, pattern)) keys.Add(key.ToString());
        }

        return keys;
    }

    private async Task<Guid?> LatestSessionIdAsync(ChatOwner owner)
    {
        var latest = await Database.SortedSetRangeByRankAsync(RedisKeys.ChatSessions(owner.Key), 0, 0, Order.Descending);
        return latest.Length == 0 ? null : Guid.ParseExact(latest[0].ToString(), "N");
    }

    private async Task<ChatSessionSummary?> ReadSummaryAsync(Guid sessionId)
    {
        var entries = await Database.HashGetAllAsync(RedisKeys.ChatSession(sessionId));
        if (entries.Length == 0) return null;

        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        return new ChatSessionSummary(
            sessionId, map["role"].ToString(),
            new DateTimeOffset((long)map["started"], TimeSpan.Zero), new DateTimeOffset((long)map["last"], TimeSpan.Zero),
            map["title"].ToString(), (int)map["turns"]);
    }

    private async Task<ChatSessionPackage?> LoadPackageAsync(ChatOwner owner, Guid sessionId)
    {
        var owned = await Database.HashGetAsync(RedisKeys.ChatSession(sessionId), "owner");
        if (owned.IsNull || owned != owner.Key) return null;

        var summary = await ReadSummaryAsync(sessionId);
        if (summary is null) return null;

        var rows = await Database.StreamRangeAsync(RedisKeys.ChatTurns(sessionId));
        var turns = rows
            .Select(r => JsonSerializer.Deserialize<StoredTurn>(r.Values.First(v => v.Name == TurnField).Value.ToString(), s_json)!)
            .OrderBy(t => t.Sequence)
            .Select(ToRecord)
            .ToList();
        return new ChatSessionPackage(summary, turns);
    }

    private static ChatTurnRecord ToRecord(StoredTurn t) =>
        new(t.Sequence, t.AskedAtUtc, t.Question, t.AnswerCard, t.ToolCalls, t.DataAsOfUtc, t.InputTokens, t.OutputTokens);

    private static string Shorten(string question) =>
        question.Length <= MaxTitleLength ? question : question[..(MaxTitleLength - 1)] + "…";

    /// <summary>Eine Frage, wie sie im Stream steht (alle Felder; die Oberfläche bekommt davon <see cref="ChatTurnRecord"/>).</summary>
    private sealed record StoredTurn(
        int Sequence,
        DateTimeOffset AskedAtUtc,
        string Question,
        JsonElement AnswerCard,
        JsonElement ToolCalls,
        DateTimeOffset DataAsOfUtc,
        int InputTokens,
        int OutputTokens,
        int CacheReadTokens,
        int CacheWriteTokens,
        int DurationMs);
}
