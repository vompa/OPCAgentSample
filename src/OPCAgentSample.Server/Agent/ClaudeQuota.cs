using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Agent;

/// <summary>Zähler für das Tageskontingent. Die Redis-Variante hat bei Ausfall einen Speicher-Ersatz, damit die Kostengrenze hält.</summary>
public interface IQuotaCounters
{
    /// <summary>Erhöht den Zähler und liefert den neuen Wert.</summary>
    Task<long> AddAsync(string key, long delta, TimeSpan expiry);

    Task<long> GetAsync(string key);
}

public enum QuotaDenial
{
    None,
    OwnerQuestions,
    OwnerTokens,
    TotalQuestions,
}

public readonly record struct QuotaDecision(bool Allowed, QuotaDenial Denial, long QuestionsLeft, long TokensLeft)
{
    public static QuotaDecision Granted(long questionsLeft, long tokensLeft) => new(true, QuotaDenial.None, questionsLeft, tokensLeft);

    public static QuotaDecision Denied(QuotaDenial denial) => new(false, denial, 0, 0);
}

/// <summary>
/// Tageskontingent (UTC-Tag): Fragen je Besitzer, Tokens je Besitzer und Fragen insgesamt. Wie <c>DemoQuota</c> des ODataAgentSample,
/// aber in Redis mit Ablauf statt im Speicher, damit es mehrere Instanzen und Neustarts übersteht.
/// </summary>
public sealed class ClaudeQuota(IQuotaCounters counters, IOptions<ClaudeOptions> options, TimeProvider time)
{
    private static readonly TimeSpan s_expiry = TimeSpan.FromHours(48);

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    /// <summary>Reserviert eine Frage. Bei Ablehnung bleibt kein Zähler erhöht.</summary>
    public async Task<QuotaDecision> TryAcquireAsync(string ownerKey)
    {
        var limits = options.Value;
        var day = Today;
        var tokensKey = RedisKeys.ClaudeQuota(ownerKey, "t", day);
        var questionsKey = RedisKeys.ClaudeQuota(ownerKey, "q", day);
        var totalKey = RedisKeys.ClaudeQuotaTotal(day);

        if (await counters.GetAsync(tokensKey) >= limits.DailyTokensPerOwner) return QuotaDecision.Denied(QuotaDenial.OwnerTokens);

        var total = await counters.AddAsync(totalKey, 1, s_expiry);
        if (total > limits.DailyQuestionsTotal)
        {
            await counters.AddAsync(totalKey, -1, s_expiry);
            return QuotaDecision.Denied(QuotaDenial.TotalQuestions);
        }

        var asked = await counters.AddAsync(questionsKey, 1, s_expiry);
        if (asked > limits.DailyQuestionsPerOwner)
        {
            await counters.AddAsync(questionsKey, -1, s_expiry);
            await counters.AddAsync(totalKey, -1, s_expiry);
            return QuotaDecision.Denied(QuotaDenial.OwnerQuestions);
        }

        var tokens = await counters.GetAsync(tokensKey);
        return QuotaDecision.Granted(limits.DailyQuestionsPerOwner - asked, Math.Max(0, limits.DailyTokensPerOwner - tokens));
    }

    /// <summary>Bucht die verbrauchten Tokens nach der Frage; die Grenze wirkt ab der nächsten Frage.</summary>
    public Task AddTokensAsync(string ownerKey, long tokens) =>
        tokens > 0 ? counters.AddAsync(RedisKeys.ClaudeQuota(ownerKey, "t", Today), tokens, s_expiry) : Task.CompletedTask;
}

/// <summary>Redis-Zähler (<c>INCRBY</c> mit Ablauf); fällt Redis aus, zählt der Speicher dieses Prozesses weiter.</summary>
public sealed class RedisQuotaCounters(RedisAccess redis) : IQuotaCounters
{
    private readonly MemoryQuotaCounters _fallback = new();

    public async Task<long> AddAsync(string key, long delta, TimeSpan expiry)
    {
        var value = await redis.TryAsync<long?>(async database =>
        {
            var next = await database.StringIncrementAsync(key, delta);
            if (next == delta) await database.KeyExpireAsync(key, expiry);
            return next;
        }, null, "claude-quota-add");
        return value ?? await _fallback.AddAsync(key, delta, expiry);
    }

    public async Task<long> GetAsync(string key)
    {
        var value = await redis.TryAsync<RedisValue?>(async database => await database.StringGetAsync(key), null, "claude-quota-get");
        return value is { } stored && stored.TryParse(out long parsed) ? parsed : await _fallback.GetAsync(key);
    }
}

/// <summary>Zähler im Speicher: Ersatz bei Redis-Ausfall und Zähler der Tests.</summary>
public sealed class MemoryQuotaCounters : IQuotaCounters
{
    private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);

    public Task<long> AddAsync(string key, long delta, TimeSpan expiry) =>
        Task.FromResult(_values.AddOrUpdate(key, delta, (_, current) => current + delta));

    public Task<long> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));
}
