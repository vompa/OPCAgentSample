using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>Zähler und Stillstand je Grund einer Station und Periode, wie sie in Redis stehen.</summary>
public sealed record CounterRow(string Station, RollupLevel Level, string Period, StationTotals Totals);

/// <summary>
/// Liest die verdichteten Strukturen zurück und baut daraus dieselben Typen, die die Kennzahlen-Bibliothek verarbeitet
/// (<see cref="KpiBlock"/>): OEE, Pareto und Kosten laufen danach unverändert über <see cref="OeeCalculator"/>,
/// <see cref="DowntimePareto"/> und <see cref="CostCalculator"/>. Jeder Aufruf liest in einer Pipeline (eine Rundreise).
/// </summary>
public sealed partial class RollupReader(IConnectionMultiplexer connection)
{
    private IDatabase Database => connection.GetDatabase();

    /// <summary>Zähler mehrerer Stationen für mehrere Perioden: je Station und Periode ein <c>HGETALL</c> und zwei <c>ZRANGE</c>.</summary>
    public async Task<IReadOnlyList<CounterRow>> ReadCountersAsync(IEnumerable<(string Station, RollupLevel Level, string Period)> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var list = wanted.ToList();
        var batch = Database.CreateBatch();
        var counters = new Task<HashEntry[]>[list.Count];
        var durations = new Task<SortedSetEntry[]>[list.Count];
        var counts = new Task<SortedSetEntry[]>[list.Count];
        for (var i = 0; i < list.Count; i++)
        {
            var (station, level, period) = list[i];
            counters[i] = batch.HashGetAllAsync(RollupKeys.Counters(station, level, period));
            durations[i] = batch.SortedSetRangeByRankWithScoresAsync(RollupKeys.ReasonDuration(station, level, period));
            counts[i] = batch.SortedSetRangeByRankWithScoresAsync(RollupKeys.ReasonCount(station, level, period));
        }

        batch.Execute();
        var rows = new List<CounterRow>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var entries = await counters[i];
            if (entries.Length == 0) continue;

            var totals = StationTotals.FromEntries(entries);
            ApplyReasons(totals, await durations[i], await counts[i]);
            rows.Add(new CounterRow(list[i].Station, list[i].Level, list[i].Period, totals));
        }

        return rows;
    }

    /// <summary>Tageskennzahlen je Station und Tag (Historie B und Rohdaten-Tage), nur abgeschlossene Tage, nach Tag und Station sortiert.</summary>
    public async Task<IReadOnlyList<DailyStationKpi>> ReadClosedDaysAsync(
        DateOnly first, DateOnly last, IReadOnlyCollection<string> stations, ShiftCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(calendar);
        var wanted = new List<(string, RollupLevel, string)>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            wanted.AddRange(stations.Select(station => (station, RollupLevel.Day, PeriodKeys.Day(day))));
        }

        return [.. (await ReadCountersAsync(wanted))
            .Where(r => !r.Totals.IsOpen)
            .OrderBy(r => r.Period, StringComparer.Ordinal).ThenBy(r => r.Station, StringComparer.Ordinal)
            .Select(r => new DailyStationKpi(
                r.Station, PeriodBounds.Of(RollupLevel.Day, r.Period, calendar).EndUtc, PeriodKeys.ParseDay(r.Period), r.Totals.Figures, r.Totals.Reasons))];
    }

    /// <summary>Zähler einer Station und Periode oder <see langword="null"/>.</summary>
    public async Task<StationTotals?> ReadTotalsAsync(string station, RollupLevel level, string period) =>
        (await ReadCountersAsync([(station, level, period)])) is [var row] ? row.Totals : null;

    /// <summary>
    /// Kennzahlenblock der Stationen für eine Periode. Stationen ohne Zähler fehlen; ohne Zähler überhaupt ist der Block leer.
    /// <see cref="PeriodBlock.IsOpen"/> ist gesetzt, solange die Periode noch läuft.
    /// </summary>
    public async Task<PeriodBlock> ReadBlockAsync(IReadOnlyList<string> stations, RollupLevel level, string period)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var rows = await ReadCountersAsync(stations.Select(s => (s, level, period)));
        var until = DateTimeOffset.MinValue;
        var open = false;
        var blocks = new Dictionary<string, StationBlock>();
        foreach (var row in rows)
        {
            blocks[row.Station] = row.Totals.ToBlock();
            if (row.Totals.SourceUntilUtc > until) until = row.Totals.SourceUntilUtc;
            open |= row.Totals.IsOpen;
        }

        return new PeriodBlock(new KpiBlock(period, until, blocks), open, rows.ToDictionary(r => r.Station, r => r.Totals));
    }

    /// <summary>Stillstandsdauer (Ticks) je Grund über mehrere Stationen mit <c>ZUNION</c> in einem Befehl.</summary>
    public async Task<IReadOnlyDictionary<string, double>> UnionDurationsAsync(
        IReadOnlyList<string> stations, RollupLevel level, string period, IReadOnlyList<double>? weights = null)
    {
        ArgumentNullException.ThrowIfNull(stations);
        var args = new List<object> { stations.Count };
        args.AddRange(stations.Select(s => (object)(RedisKey)RollupKeys.ReasonDuration(s, level, period)));
        if (weights is not null)
        {
            args.Add("WEIGHTS");
            args.AddRange(weights.Select(w => (object)w));
        }

        args.Add("WITHSCORES");
        var result = (RedisResult[])(await Database.ExecuteAsync("ZUNION", [.. args]))!;
        var map = new Dictionary<string, double>();
        if (result.Length > 0 && result[0].Resp3Type == ResultType.Array)
        {
            foreach (var pair in result)
            {
                var items = (RedisResult[])pair!;
                map[(string)items[0]!] = (double)items[1];
            }
        }
        else
        {
            for (var i = 0; i + 1 < result.Length; i += 2) map[(string)result[i]!] = (double)result[i + 1];
        }

        return map;
    }

    /// <summary>
    /// Alle Zähler einer Ebene (Suchlauf über die Schlüssel), z. B. für den Neuaufbau der Summen im Speicher nach einem Neustart.
    /// </summary>
    public async Task<IReadOnlyList<CounterRow>> ScanCountersAsync(RollupLevel level)
    {
        var keys = await ScanAsync(RollupKeys.CountersPattern(level));
        var wanted = new List<(string, RollupLevel, string)>(keys.Count);
        foreach (var key in keys)
        {
            // kc:{station}:{ebene}:{periode}
            var parts = key.Split(':', 4);
            wanted.Add((parts[1], level, parts[3]));
        }

        return await ReadCountersAsync(wanted);
    }

    /// <summary>Alle Schlüssel zu einem Muster (<c>SCAN</c>), sortiert.</summary>
    public async Task<IReadOnlyList<string>> ScanAsync(string pattern)
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            if (server.IsReplica) continue;

            await foreach (var key in server.KeysAsync(Database.Database, pattern, pageSize: 1000))
            {
                keys.Add(key.ToString());
            }
        }

        return [.. keys];
    }

    internal static void ApplyReasons(StationTotals totals, SortedSetEntry[] durations, SortedSetEntry[] counts)
    {
        var reasonCounts = counts.ToDictionary(e => (string)e.Element!, e => (int)e.Score);
        foreach (var entry in durations)
        {
            var reason = (string)entry.Element!;
            totals.Reasons[reason] = new ReasonFigures(TimeSpan.FromTicks((long)entry.Score), reasonCounts.GetValueOrDefault(reason));
        }
    }
}

/// <summary>Kennzahlenblock einer Periode mit den Zählern je Station (für Ausschuss je Grund) und dem Kennzeichen „Periode läuft noch“.</summary>
public sealed record PeriodBlock(KpiBlock Block, bool IsOpen, IReadOnlyDictionary<string, StationTotals> Totals);
