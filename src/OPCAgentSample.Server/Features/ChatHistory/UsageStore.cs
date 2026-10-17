using System.Globalization;
using System.Text.Json;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Features.ChatHistory;

/// <summary>Summen aller Fragen (<c>usage:total</c>) mit dem Histogramm der Antwortzeiten; <c>Repeats</c> zählt die gespeicherten Antworten auf wiederholte Fragen.</summary>
public sealed record UsageTotals(
    long Questions, long AnsweredByClaude, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens,
    long Measured, double DurationSumMs, DateTimeOffset? FirstQuestionAtUtc, IReadOnlyList<long> LatencyBins, long Repeats = 0);

/// <summary>Aufrufe eines Tools in einer Rolle mit einem Ergebnis: Anzahl, Summe der Laufzeit und Histogramm der Laufzeit.</summary>
public sealed record UsageToolRow(string Tool, string Role, string Outcome, long Calls, double DurationSumMs, IReadOnlyList<long> LatencyBins);

/// <summary>
/// Zähler der Nutzung (Seite „KI-Nutzung“), beim Speichern einer Frage fortgeschrieben: je Tag und Stunde die Fragen und Tokens,
/// insgesamt die Summen samt Histogramm der Antwortzeiten, je Tool Aufrufe, Laufzeit und Ergebnis je Rolle. Es werden nur Zähler
/// gespeichert, keine Frage- oder Antwortinhalte. Aufbewahrung 35 Tage ab dem letzten Schreiben.
/// </summary>
public sealed class UsageStore(IConnectionMultiplexer redis)
{
    /// <summary>Untergrenzen der Behälter des Histogramms der Antwortzeiten in Millisekunden; der letzte Behälter ist offen.</summary>
    public static readonly int[] LatencyEdgesMs = [250, 500, 1000, 2000, 3000, 5000, 8000, 12000, 20000, 30000];

    private static readonly TimeSpan s_retention = TimeSpan.FromDays(35);

    public static int Bins => LatencyEdgesMs.Length + 1;

    /// <summary>Behälter einer Zeit: 0 unterhalb der ersten Grenze, <c>i + 1</c> ab <c>LatencyEdgesMs[i]</c>.</summary>
    public static int BinOf(double milliseconds)
    {
        var bin = 0;
        for (var i = 0; i < LatencyEdgesMs.Length; i++)
        {
            if (milliseconds >= LatencyEdgesMs[i]) bin = i + 1;
        }

        return bin;
    }

    /// <summary>Schreibt die Zähler einer Frage in die Transaktion der Frage.</summary>
    public static void Record(ITransaction tx, DateTimeOffset askedAt, string role, NewChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(turn);
        var tokens = turn.InputTokens + turn.OutputTokens + turn.CacheReadTokens + turn.CacheWriteTokens;
        var total = RedisKeys.UsageTotal;
        _ = tx.HashIncrementAsync(total, "q");
        if (tokens > 0) _ = tx.HashIncrementAsync(total, "cl");
        if (turn.Repeat) _ = tx.HashIncrementAsync(total, "rp");
        _ = tx.HashIncrementAsync(total, "in", turn.InputTokens);
        _ = tx.HashIncrementAsync(total, "out", turn.OutputTokens);
        _ = tx.HashIncrementAsync(total, "cr", turn.CacheReadTokens);
        _ = tx.HashIncrementAsync(total, "cw", turn.CacheWriteTokens);
        _ = tx.HashSetAsync(total, "first", askedAt.ToUnixTimeMilliseconds(), When.NotExists);
        if (turn.DurationMs > 0 && !turn.Repeat) // eine gespeicherte Antwort verzerrt das Histogramm der Antwortzeiten
        {
            _ = tx.HashIncrementAsync(total, "dn");
            _ = tx.HashIncrementAsync(total, "ds", turn.DurationMs);
            _ = tx.HashIncrementAsync(total, "b" + BinOf(turn.DurationMs).ToString(CultureInfo.InvariantCulture));
        }

        var day = RedisKeys.UsageDay(askedAt);
        _ = tx.HashIncrementAsync(day, "q");
        var hour = RedisKeys.UsageHour(askedAt);
        _ = tx.HashIncrementAsync(hour, "q");
        _ = tx.HashIncrementAsync(hour, "in", turn.InputTokens);
        _ = tx.HashIncrementAsync(hour, "out", turn.OutputTokens);
        foreach (var key in new[] { total, day, hour }) _ = tx.KeyExpireAsync(key, s_retention);

        foreach (var call in turn.ToolCalls.EnumerateArray())
        {
            if (!call.TryGetProperty("tool", out var tool) || tool.ValueKind != JsonValueKind.String || tool.GetString() is not { Length: > 0 } name) continue;

            var outcome = call.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : "ok";
            var duration = call.TryGetProperty("durationMs", out var d) && d.TryGetDouble(out var ms) ? ms : 0;
            var prefix = $"{role}|{outcome}|";
            var key = RedisKeys.UsageTool(name);
            _ = tx.SetAddAsync(RedisKeys.UsageTools, name);
            _ = tx.HashIncrementAsync(key, prefix + "n");
            _ = tx.HashIncrementAsync(key, prefix + "ms", (long)Math.Round(duration));
            _ = tx.HashIncrementAsync(key, prefix + "b" + BinOf(duration).ToString(CultureInfo.InvariantCulture));
            _ = tx.KeyExpireAsync(key, s_retention);
            _ = tx.KeyExpireAsync(RedisKeys.UsageTools, s_retention);
        }
    }

    public async Task<UsageTotals> ReadTotalsAsync()
    {
        var map = (await redis.GetDatabase().HashGetAllAsync(RedisKeys.UsageTotal)).ToDictionary(e => (string)e.Name!, e => (long)e.Value);
        long Get(string field) => map.GetValueOrDefault(field);
        return new UsageTotals(
            Get("q"), Get("cl"), Get("in"), Get("out"), Get("cr"), Get("cw"), Get("dn"), Get("ds"),
            map.TryGetValue("first", out var first) ? DateTimeOffset.FromUnixTimeMilliseconds(first) : null,
            [.. Enumerable.Range(0, Bins).Select(i => Get("b" + i.ToString(CultureInfo.InvariantCulture)))],
            Get("rp"));
    }

    /// <summary>Fragen und Tokens je Stunde für die genannten Stunden (UTC); fehlende Stunden zählen null.</summary>
    public async Task<IReadOnlyList<(long Questions, long Input, long Output)>> ReadHoursAsync(IReadOnlyList<DateTimeOffset> hours)
    {
        var batch = redis.GetDatabase().CreateBatch();
        var tasks = hours.Select(h => batch.HashGetAllAsync(RedisKeys.UsageHour(h))).ToList();
        batch.Execute();
        var result = new List<(long, long, long)>(hours.Count);
        foreach (var task in tasks)
        {
            var map = (await task).ToDictionary(e => (string)e.Name!, e => (long)e.Value);
            result.Add((map.GetValueOrDefault("q"), map.GetValueOrDefault("in"), map.GetValueOrDefault("out")));
        }

        return result;
    }

    public async Task<long> ReadDayQuestionsAsync(DateTimeOffset day)
    {
        var value = await redis.GetDatabase().HashGetAsync(RedisKeys.UsageDay(day), "q");
        return value.HasValue ? (long)value : 0;
    }

    public async Task<IReadOnlyList<UsageToolRow>> ReadToolsAsync()
    {
        var database = redis.GetDatabase();
        var names = (await database.SetMembersAsync(RedisKeys.UsageTools)).Select(v => v.ToString()).Order(StringComparer.Ordinal).ToList();
        var batch = database.CreateBatch();
        var tasks = names.Select(n => batch.HashGetAllAsync(RedisKeys.UsageTool(n))).ToList();
        batch.Execute();
        var rows = new List<UsageToolRow>();
        for (var i = 0; i < names.Count; i++)
        {
            var fields = (await tasks[i]).ToDictionary(e => (string)e.Name!, e => (long)e.Value);
            foreach (var group in fields.Keys.Select(k => k.Split('|')).Where(p => p.Length == 3).GroupBy(p => (p[0], p[1])))
            {
                var prefix = $"{group.Key.Item1}|{group.Key.Item2}|";
                rows.Add(new UsageToolRow(
                    names[i], group.Key.Item1, group.Key.Item2, fields.GetValueOrDefault(prefix + "n"), fields.GetValueOrDefault(prefix + "ms"),
                    [.. Enumerable.Range(0, Bins).Select(b => fields.GetValueOrDefault(prefix + "b" + b.ToString(CultureInfo.InvariantCulture)))]));
            }
        }

        return rows;
    }

    /// <summary>Perzentil aus dem Histogramm, linear innerhalb des Behälters geschätzt (der offene letzte Behälter zählt doppelt so breit).</summary>
    public static double? Percentile(IReadOnlyList<long> bins, double fraction)
    {
        var total = bins.Sum();
        if (total == 0) return null;

        var target = fraction * total;
        long seen = 0;
        for (var i = 0; i < bins.Count; i++)
        {
            if (bins[i] == 0 || seen + bins[i] < target)
            {
                seen += bins[i];
                continue;
            }

            double lower = i == 0 ? 0 : LatencyEdgesMs[i - 1];
            double upper = i < LatencyEdgesMs.Length ? LatencyEdgesMs[i] : lower * 2;
            return lower + ((upper - lower) * (target - seen) / bins[i]);
        }

        return LatencyEdgesMs[^1];
    }
}
