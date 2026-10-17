using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Kpis;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Kennzahlen eines Zeitraums: der Block der Halle A (Stationen) und die Summen der Zählerhallen B/C. Alle Kennzahlen
/// entstehen aus diesen Grundgrößen mit der Kennzahlen-Bibliothek; gemittelt wird nie.
/// </summary>
public sealed class KpiData
{
    public KpiData(ResolvedRange range, KpiBlock block, IReadOnlyDictionary<string, StationBlock> counters, DateTimeOffset sourceUntilUtc)
    {
        Range = range;
        Block = block;
        Counters = counters;
        SourceUntilUtc = sourceUntilUtc;
    }

    public ResolvedRange Range { get; }

    public KpiBlock Block { get; }

    public IReadOnlyDictionary<string, StationBlock> Counters { get; }

    public DateTimeOffset SourceUntilUtc { get; }

    public ProductionFigures Figures(string stationId) =>
        Counters.TryGetValue(stationId, out var counter) ? counter.Figures : Block.Figures(stationId);

    public ProductionFigures Figures(IEnumerable<string> stationIds) => ProductionFigures.Sum(stationIds.Select(Figures));

    public IReadOnlyDictionary<string, ReasonFigures> Reasons(string stationId) =>
        Counters.TryGetValue(stationId, out var counter) ? counter.Reasons
        : Block.Stations.TryGetValue(stationId, out var station) ? station.Reasons
        : new Dictionary<string, ReasonFigures>();

    /// <summary>Block mit Halle A und den Zählerhallen in einem (für Pareto, Kosten und OEE über das Werk).</summary>
    public KpiBlock Combined()
    {
        var stations = new Dictionary<string, StationBlock>(Block.Stations);
        foreach (var (id, counter) in Counters) stations[id] = counter;
        return new KpiBlock(Block.Key, Block.SourceUntilUtc, stations);
    }

    public OeeResult Oee(IEnumerable<string> stationIds) => OeeCalculator.From(Figures(stationIds));
}

/// <summary>
/// Liefert Kennzahlen aus den verdichteten Zählern in Redis: Schichten, Tage und Wochen über den <see cref="KpiBlockService"/>, lange
/// oder freie Zeiträume aus den Tageszählern (Historie B) und die Hallen B/C aus ihren Tageszählern. Merkt die Herkunft im
/// <see cref="McpSession"/>.
/// </summary>
public sealed class KpiAccess(
    KpiBlockService blocks,
    RollupReader reader,
    ShiftCalendar calendar,
    IOptions<Configuration.PlantOptions> plant,
    McpSession session)
{
    public async Task<KpiData> GetAsync(ResolvedRange range, bool withCounters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var now = await session.NowAsync(ct);
        var rawStart = await RawStartAsync();
        var parts = new List<KpiBlock>();
        var untilMax = DateTimeOffset.MinValue;

        var direct = range.Periods.Count > 0 && range.Periods.All(p => p.StartUtc >= rawStart && p.Kind != PeriodKind.Hour);
        if (direct)
        {
            foreach (var period in range.Periods.Where(p => p.StartUtc < now))
            {
                var result = await blocks.GetAsync(period, ct);
                session.Sources.Add(result.SourceLabel);
                untilMax = result.Block.SourceUntilUtc > untilMax ? result.Block.SourceUntilUtc : untilMax;
                parts.Add(result.Block);
            }
        }
        else
        {
            parts.Add(await DaysAsync(range, now, ct));
        }

        var combined = KpiBlock.Combine(range.Label, parts);
        var counters = withCounters ? await CountersAsync(range, now) : new Dictionary<string, StationBlock>();
        if (untilMax == DateTimeOffset.MinValue) untilMax = combined.SourceUntilUtc;
        session.Sources.Touch(untilMax);
        return new KpiData(range, combined, counters, untilMax);
    }

    /// <summary>Block eines Zeitraums, getrennt nach Schichtart (z. B. alle Nachtschichten der Woche), nur Halle A.</summary>
    public async Task<KpiData> GetByShiftKindAsync(ResolvedRange range, Shift kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        var now = await session.NowAsync(ct);
        var parts = new List<KpiBlock>();
        foreach (var shift in calendar.ProductionShiftsBetween(range.StartUtc, range.EffectiveEnd(now))
                     .Where(s => calendar.ShiftOf(s.StartUtc) == kind && s.StartUtc < now))
        {
            var result = await blocks.GetAsync(shift, ct);
            session.Sources.Add(result.SourceLabel);
            parts.Add(result.Block);
        }

        var combined = KpiBlock.Combine(range.Label, parts);
        if (parts.Count > 0) session.Sources.Touch(combined.SourceUntilUtc);
        return new KpiData(range, combined, new Dictionary<string, StationBlock>(), parts.Count > 0 ? combined.SourceUntilUtc : now);
    }

    /// <summary>Tageskennzahlen je Station und Tag (Historie B) im Zeitraum; mit <paramref name="closedOnly"/> nur abgeschlossene Tage.</summary>
    public async Task<IReadOnlyList<DailyStationKpi>> DaysAsync(
        ResolvedRange range, IReadOnlyCollection<string>? stationIds, bool closedOnly, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(range);
        ct.ThrowIfCancellationRequested();
        var now = await session.NowAsync(ct);
        var first = calendar.ProductionDayOf(range.StartUtc);
        var last = calendar.ProductionDayOf(range.EffectiveEnd(now).AddMinutes(-1));
        var stations = stationIds ?? [.. PlantLayout.HallAStations.Select(s => s.Id)];
        var rows = await DayRowsAsync(first, last, stations);
        session.Sources.Add(KpiBlockService.SourceLabel, rows.Count > 0 ? rows.Max(r => r.Totals.SourceUntilUtc) : null);

        var result = new List<DailyStationKpi>();
        foreach (var row in rows)
        {
            if (row.Totals.IsOpen && closedOnly) continue;

            var day = PeriodKeys.ParseDay(row.Period);
            var stamp = row.Totals.IsOpen ? now : PeriodBounds.Of(RollupLevel.Day, row.Period, calendar).EndUtc;
            result.Add(new DailyStationKpi(row.Station, stamp, day, row.Totals.Figures, row.Totals.Reasons));
        }

        return result;
    }

    /// <summary>Summen der Tage eines freien oder weit zurückliegenden Zeitraums aus den Tageszählern (der laufende Tag zählt bis jetzt).</summary>
    private async Task<KpiBlock> DaysAsync(ResolvedRange range, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await DaysAsync(range, null, closedOnly: false, ct);
        var sums = new Dictionary<string, StationBlock>();
        foreach (var row in rows)
        {
            var block = new StationBlock(row.Figures, row.Reasons);
            sums[row.StationId] = sums.TryGetValue(row.StationId, out var known) ? known.Add(block) : block;
        }

        return new KpiBlock(range.Label, now, sums);
    }

    private async Task<Dictionary<string, StationBlock>> CountersAsync(ResolvedRange range, DateTimeOffset now)
    {
        var result = new Dictionary<string, StationBlock>();
        if (range.Kind == RangeKind.Shift) return result;

        var first = calendar.ProductionDayOf(range.StartUtc);
        var last = calendar.ProductionDayOf(range.EffectiveEnd(now).AddMinutes(-1));
        foreach (var row in await DayRowsAsync(first, last, [.. plant.Value.CounterStations.Values]))
        {
            var block = row.Totals.ToBlock();
            result[row.Station] = result.TryGetValue(row.Station, out var known) ? known.Add(block) : block;
        }

        if (result.Count > 0) session.Sources.Add(KpiBlockService.SourceLabel);
        return result;
    }

    private Task<IReadOnlyList<CounterRow>> DayRowsAsync(DateOnly first, DateOnly last, IReadOnlyCollection<string> stations)
    {
        var wanted = new List<(string, RollupLevel, string)>();
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            wanted.AddRange(stations.Select(station => (station, RollupLevel.Day, PeriodKeys.Day(day))));
        }

        return reader.ReadCountersAsync(wanted);
    }

    /// <summary>Beginn der Rohdaten-Historie: erste Schicht mit Daten (davor gibt es nur Tageskennzahlen).</summary>
    public async Task<DateTimeOffset> RawStartAsync() => await reader.ReadRawStartAsync() ?? DateTimeOffset.MaxValue;
}

/// <summary>
/// Kostensätze der Halle A aus den Stammdaten, ergänzt um die Sätze der verdichteten Hallen B/C
/// (Konfiguration Mcp:CounterHallDowntimeEuroPerMinute).
/// </summary>
public sealed class PlantCostRates(MasterDataStore masterData, IOptions<McpOptions> options)
{
    public async Task<CostRateTable> GetAsync(CancellationToken ct) =>
        (await masterData.GetAsync(ct)).CostTable(options.Value.CounterHallDowntimeEuroPerMinute);
}
