using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Verdichtet die Historie in Redis (Spezifikation Abschnitt 15). Der Aufrufer liefert die Datensätze Fenster für Fenster
/// (<see cref="ApplyAsync"/>); jedes Fenster liegt in einer Schicht und setzt den Wasserstand der Quelle. Der Dienst hält nur den
/// laufenden Schichtblock (<see cref="KpiBlockBuilder"/>), die offenen Fächer der Prozessstatistik und die Summen der
/// abgeschlossenen Schichten im Speicher; alles andere liegt in Redis.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Die laufende Schicht wird mit jedem Fenster absolut geschrieben (Schicht-Hash = Stand des Builders). Tag, Woche, Monat, Quartal
/// und Jahr sind absolut geschriebene Summen ihrer Kinder (abgeschlossene Kinder im Speicher plus die laufende Schicht).</item>
/// <item>Alle Schreibvorgänge eines Fensters laufen in einem <c>MULTI</c>/<c>EXEC</c> zusammen mit dem Wasserstand.</item>
/// <item>Nach einem Neustart des Prozesses setzt der Dienst die laufende Schicht aus den gespeicherten Summen, den Ereignissen und dem
/// Plan fort; stimmt der Wasserstand nicht mehr (Redis geleert, anderes Fenster), baut er seinen Stand neu auf.</item>
/// <item>Ein Ereignis, das in einer früheren Schicht begonnen hat (nachgetragener Grund, spätes Ende), rechnet nur den
/// ereignisabhängigen Teil dieser Schichten neu; Stück und Ausschuss ändern sich dadurch nicht.</item>
/// </list>
/// </remarks>
public sealed partial class RollupEngine(IConnectionMultiplexer redis, IRollupContextSource contextSource) : IDisposable
{
    private static readonly TimeSpan s_fineWindow = TimeSpan.FromDays(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RollupReader _reader = new(redis);
    private readonly Dictionary<string, StreamIds> _ids = [];
    private RollupContext? _context;
    private Dictionary<string, MouldMount[]> _mouldsByStation = [];
    private PeriodSums<StationTotals>? _counters;
    private PeriodSums<ProcessStat>? _stats;
    private RunningShift? _running;
    private DateTimeOffset? _fedUntil;

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Nimmt die Datensätze eines Fensters auf. <paramref name="window"/> darf nicht über das Ende der Schicht hinausgehen, in der es
    /// beginnt. Schlägt etwas fehl, verwirft der Dienst seinen Stand und baut ihn beim nächsten Fenster aus Redis auf.
    /// </summary>
    /// <param name="horizonUtc">
    /// Ziel der laufenden Aufnahme (Standard: Ende des Fensters). Das 1-Minuten-Raster hält nur die letzten zwei Tage vor dem Ziel;
    /// beim Nachladen der Historie entstehen so keine Fächer, die sofort wieder verfielen.
    /// </param>
    public async Task ApplyAsync(
        string sourceId, TimeWindow window, IReadOnlyList<HistoryRecord> records, DateTimeOffset? horizonUtc = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentNullException.ThrowIfNull(records);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_fedUntil is { } fed && fed != window.StartUtc) Reset();

            var context = await ContextAsync(cancellationToken);
            var database = redis.GetDatabase();
            if (_counters is null) await LoadSumsAsync(context);

            await ApplyCoreAsync(context, database, sourceId, window, records, horizonUtc ?? window.EndUtc);
            _fedUntil = window.EndUtc;
        }
        catch
        {
            Reset();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Verwirft den Stand im Speicher (er wird beim nächsten Fenster aus Redis aufgebaut), z. B. nachdem Redis geleert wurde.</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Reset();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Reset()
    {
        _context = null;
        _counters = null;
        _stats = null;
        _running = null;
        _fedUntil = null;
        _ids.Clear();
    }

    private async Task<RollupContext> ContextAsync(CancellationToken cancellationToken)
    {
        if (_context is { } known) return known;

        var context = await contextSource.GetAsync(cancellationToken);
        _mouldsByStation = context.Moulds
            .GroupBy(m => m.StationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.MountedUtc).ToArray());
        _context = context;
        return context;
    }

    /// <summary>Baut die Summen der abgeschlossenen Schichten und Tage aus Redis auf (Start des Prozesses, Neuaufbau).</summary>
    private async Task LoadSumsAsync(RollupContext context)
    {
        var counters = new PeriodSums<StationTotals>(context.Calendar, _ => new StationTotals(), (sum, part) => sum.Add(part));
        var stats = new PeriodSums<ProcessStat>(
            context.Calendar, owner => new ProcessStat(StatOwner.Info(owner).Bins), (sum, part) => sum.Merge(part));

        var shiftDays = new HashSet<(string, string)>();
        foreach (var row in await _reader.ScanCountersAsync(RollupLevel.Shift))
        {
            counters.SetShift(row.Station, row.Period, row.Totals);
            shiftDays.Add((row.Station, PeriodKeys.Day(PeriodKeys.DayOfShift(row.Period))));
        }

        foreach (var row in await _reader.ScanCountersAsync(RollupLevel.Day))
        {
            // Ein Tag ohne Schichten ist ein direkt gelieferter Tageswert (Historie B, Zähler der Hallen B und C).
            if (!shiftDays.Contains((row.Station, row.Period))) counters.SetDirectDay(row.Station, row.Period, row.Totals);
        }

        foreach (var (owner, shiftKey, stat) in await _reader.ReadShiftStatsAsync())
        {
            stats.SetShift(owner, shiftKey, stat);
        }

        _counters = counters;
        _stats = stats;
    }

    private async Task ApplyCoreAsync(
        RollupContext context, IDatabase database, string sourceId, TimeWindow window, IReadOnlyList<HistoryRecord> records,
        DateTimeOffset horizonUtc)
    {
        var calendar = context.Calendar;
        var shift = calendar.ShiftPeriodOf(window.StartUtc);
        if (_running?.Period.Key != shift.Key && records.Any(r => r is not DailyStationKpi))
        {
            _running = await StartShiftAsync(context, shift, database);
        }

        var run = _running?.Period.Key == shift.Key ? _running : null;
        var work = new WindowWork();
        var fineFrom = horizonUtc - s_fineWindow;
        foreach (var record in records)
        {
            Consume(record, context, run, shift, work, fineFrom);
        }

        var tx = new TxWriter(database);
        var runningTotals = new Dictionary<string, StationTotals>();
        var until = window.EndUtc < shift.EndUtc ? window.EndUtc : shift.EndUtc;
        if (run is not null)
        {
            if (work.EarlierEventStarts.Count > 0) await RecomputeEarlierShiftsAsync(context, shift, work, tx);
            WriteRunning(tx, run, until, runningTotals, work);
            WriteStats(tx, run, context.Calendar);
        }

        WriteParents(tx, context, run, runningTotals, work, window.EndUtc);
        await WriteStreamsAsync(tx, database, context, work, run?.Period);
        tx.HashSet(RollupKeys.Watermarks, [new HashEntry(sourceId, window.EndUtc.UtcTicks)]);
        await tx.ExecuteAsync();

        if (run is null) return;

        foreach (var series in run.AllSeries) series.ClearDirty();
        if (window.EndUtc >= shift.EndUtc) CloseRunningShift(run, runningTotals);
    }

    /// <summary>Beginn einer Schicht: neuer Block oder Wiederaufnahme aus den gespeicherten Summen.</summary>
    private async Task<RunningShift> StartShiftAsync(RollupContext context, ProductionPeriod shift, IDatabase database)
    {
        var calendar = context.Calendar;
        var day = PeriodKeys.DayOfShift(shift.Key);
        var run = new RunningShift(shift, day, calendar.DayPeriod(day).StartUtc, new KpiBlockBuilder(shift, context.Costs));
        _counters!.RegisterDay(run.DayKey);
        _stats!.RegisterDay(run.DayKey);

        var stations = PlantLayout.HallAStations.Select(s => s.Id).ToList();
        var stored = await _reader.ReadCountersAsync(stations.Select(s => (s, RollupLevel.Shift, shift.Key)));
        foreach (var row in stored)
        {
            var f = row.Totals.Figures;
            run.Builder.RestorePieces(row.Station, f.GoodPieces, f.ScrapPieces, f.IdealSeconds, f.GoodIdealSeconds, f.ScrapCostEuro);
            if (row.Totals.Plan is { } plan)
            {
                run.Plans[row.Station] = plan;
                run.Builder.Add(new ShiftPlanRecord(row.Station, plan.StartUtc, shift.Key, plan.StartUtc, plan.EndUtc, plan.PlannedQuantity));
            }

            foreach (var (reason, count) in row.Totals.ScrapByReason) run.Scrap[(row.Station, reason)] = count;
        }

        foreach (var loss in await _reader.ReadLossesOverlappingAsync(shift.StartUtc, shift.EndUtc))
        {
            run.Builder.Add(loss);
        }

        if (stored.Count > 0) await RestoreBucketsAsync(run, database);
        return run;
    }

    /// <summary>Lädt die Fächer der Schicht aus den Tages-Hashes, damit ein Fenster nach einem Neustart sie fortschreibt.</summary>
    private async Task RestoreBucketsAsync(RunningShift run, IDatabase database)
    {
        foreach (var (resolution, fine) in new[] { ("15m", false), ("1m", true) })
        {
            var slotTicks = fine ? TimeSpan.TicksPerMinute : 15 * TimeSpan.TicksPerMinute;
            var first = (int)((run.Period.StartUtc - run.DayStartUtc).Ticks / slotTicks);
            var last = (int)((run.Period.EndUtc - run.DayStartUtc).Ticks / slotTicks);
            foreach (var key in await _reader.ScanAsync($"ps:{RollupKeys.StatsVersion}:*:*:{resolution}:{run.DayKey}"))
            {
                var parts = key.Split(':');
                var series = run.Series(parts[2], parts[3]);
                foreach (var entry in await database.HashGetAllAsync(key))
                {
                    var slot = int.Parse((string)entry.Name!, CultureInfo.InvariantCulture);
                    if (slot < first || slot >= last) continue;

                    (fine ? series.Fine : series.Coarse)[slot] = ProcessStat.Decode((byte[])entry.Value!, series.Info.Bins, wide: false);
                }
            }
        }
    }

    private void CloseRunningShift(RunningShift run, Dictionary<string, StationTotals> runningTotals)
    {
        foreach (var (station, totals) in runningTotals) _counters!.SetShift(station, run.Period.Key, totals);
        foreach (var series in run.AllSeries) _stats!.SetShift(StatOwner.Of(series.Info), run.Period.Key, series.ShiftStat());
        _running = null;
    }

    // ---- Datensätze -------------------------------------------------------------------------------------------

    private void Consume(
        HistoryRecord record, RollupContext context, RunningShift? run, ProductionPeriod shift, WindowWork work, DateTimeOffset fineFrom)
    {
        switch (record)
        {
            case CycleRecord cycle when run is not null:
                OnCycle(cycle, context, run, work, fineFrom);
                break;
            case UnitCountRecord count when run is not null:
                run.Builder.Add(count);
                work.PackagingArticles[count.StationId] = count.ArticleId;
                var rate = count.TargetCycleSeconds > 0 ? (count.Good + count.Scrap) / (60.0 / count.TargetCycleSeconds) : 0;
                Observe(run, count.StationId, SeriesNames.Cartons, count.TimestampUtc, count.Good + count.Scrap, rate, fineFrom);
                break;
            case MinuteValue minute when run is not null:
                OnMinute(minute, run, work, fineFrom);
                break;
            case ShiftPlanRecord plan when run is not null:
                run.Builder.Add(plan);
                if (plan.StartUtc < shift.EndUtc && plan.EndUtc > shift.StartUtc)
                {
                    run.Plans[plan.StationId] = new PlanWindow(plan.StartUtc, plan.EndUtc, plan.PlannedQuantity);
                }

                break;
            case LossEvent loss when run is not null:
                run.Builder.Add(loss);
                work.Losses[loss.Id] = loss;
                if (loss.StartUtc < shift.StartUtc) work.EarlierEventStarts.Add(loss.StartUtc);
                break;
            case AlarmRecord alarm:
                work.Alarms[alarm.Id] = alarm;
                break;
            case StateChange state:
                work.States.Add(state);
                break;
            case OrderEvent order:
                work.Orders.Add(order);
                break;
            case PressLot lot:
                work.Lots.Add(lot);
                break;
            case OvenCharge charge:
                work.Charges.Add(charge);
                break;
            case PalletRecord pallet:
                work.Pallets.Add(pallet);
                break;
            case DailyStationKpi day:
                var dayKey = PeriodKeys.Day(day.Day);
                var totals = StationTotals.From(new StationBlock(day.Figures, day.Reasons), null, false, day.TimestampUtc);
                _counters!.SetDirectDay(day.StationId, dayKey, totals);
                work.MarkDirtyDay(dayKey, day.StationId);
                break;
            default:
                break;
        }
    }

    private void OnCycle(CycleRecord cycle, RollupContext context, RunningShift run, WindowWork work, DateTimeOffset fineFrom)
    {
        var station = cycle.StationId;
        run.Builder.Add(cycle);
        if (cycle.Scrap is { } reason)
        {
            var key = (station, reason.ToString());
            run.Scrap[key] = run.Scrap.GetValueOrDefault(key) + 1;
        }

        if (!work.Cycles.TryGetValue(station, out var cycles)) work.Cycles[station] = cycles = [];
        cycles.Add(cycle);

        if (cycle.OrderId is { } orderId)
        {
            var (_, count) = work.OrderCycles.GetValueOrDefault(orderId);
            work.OrderCycles[orderId] = (station, count + 1);
        }

        if (MouldAt(station, cycle.TimestampUtc) is { } mould)
        {
            work.MouldCycles[mould.MouldId] = work.MouldCycles.GetValueOrDefault(mould.MouldId) + 1;
        }

        var cycleDeviation = cycle.TargetCycleSeconds > 0 ? ((cycle.CycleTimeSeconds / cycle.TargetCycleSeconds) - 1) * 100 : 0;
        var fillDeviation = context.Fill.TryGetValue(cycle.ArticleId, out var fill) && fill.ToleranceGrams > 0
            ? (cycle.FillWeightGrams - fill.TargetGrams) / fill.ToleranceGrams
            : 0;
        var at = cycle.TimestampUtc;
        Observe(run, station, SeriesNames.CycleTime, at, cycle.CycleTimeSeconds, 0, fineFrom);
        Observe(run, station, SeriesNames.CycleDeviation, at, cycleDeviation, cycleDeviation, fineFrom);
        Observe(run, station, SeriesNames.PressForce, at, cycle.PressForceKn, 0, fineFrom);
        Observe(run, station, SeriesNames.BuildUp, at, cycle.PressForceBuildUpSeconds, cycle.PressForceBuildUpSeconds, fineFrom);
        Observe(run, station, SeriesNames.FillWeight, at, cycle.FillWeightGrams, fillDeviation, fineFrom);
        Observe(run, station, SeriesNames.Thickness, at, cycle.ThicknessMm, 0, fineFrom);
        Observe(run, station, SeriesNames.Scrap, at, cycle.IsGood ? 0 : 1, 0, fineFrom);
    }

    private static void OnMinute(MinuteValue minute, RunningShift run, WindowWork work, DateTimeOffset fineFrom)
    {
        if (SeriesCatalog.IsCounter(minute.Variable)) return;

        Observe(run, minute.StationId, minute.Variable, minute.TimestampUtc, minute.Value, minute.Value, fineFrom);
        if (minute.Variable == MinuteVariables.Zone2Temperature && minute.Value >= SeriesNames.OvenHoldFromCelsius)
        {
            Observe(run, minute.StationId, SeriesNames.OvenHold, minute.TimestampUtc, minute.Value, minute.Value, fineFrom);
        }

        var key = (minute.StationId, minute.Variable);
        if (!work.Minutes.TryGetValue(key, out var list)) work.Minutes[key] = list = [];
        list.Add(minute);
        if (minute.StationId == MinuteVariables.HallAClimateSource && minute.Variable == MinuteVariables.Temperature)
        {
            work.FirstClimateTemperature ??= minute.Value;
        }
    }

    private MouldMount? MouldAt(string station, DateTimeOffset at) =>
        _mouldsByStation.TryGetValue(station, out var moulds) ? moulds.FirstOrDefault(m => m.MountedUtc <= at) : null;

    private static void Observe(
        RunningShift run, string station, string variable, DateTimeOffset at, double value, double binValue, DateTimeOffset fineFrom) =>
        run.Series(station, variable).Observe(at, value, binValue, run.DayStartUtc, fineFrom);

    // ---- Schreiben: Zähler ------------------------------------------------------------------------------------

    /// <summary>Schreibt den Stand der laufenden Schicht (Zähler, Stillstand je Grund, Ausschuss je Grund) absolut.</summary>
    private static void WriteRunning(
        TxWriter tx, RunningShift run, DateTimeOffset until, Dictionary<string, StationTotals> runningTotals, WindowWork work)
    {
        var block = run.Builder.Snapshot(until);
        var isOpen = until < run.Period.EndUtc;
        if (block.Stations.Count > 0) tx.SetIfAbsent(RollupKeys.RawStart, run.Period.StartUtc.UtcTicks);
        foreach (var (station, stationBlock) in block.Stations)
        {
            var scrap = run.Scrap.Where(p => p.Key.Station == station).ToDictionary(p => p.Key.Reason, p => p.Value);
            var totals = StationTotals.From(stationBlock, scrap, isOpen, until);
            totals.Plan = run.Plans.GetValueOrDefault(station);
            runningTotals[station] = totals;
            WriteTotals(tx, station, RollupLevel.Shift, run.Period.Key, totals);
            work.MarkDirtyDay(run.DayKey, station);
        }
    }

    /// <summary>
    /// Schreibt Tag, Woche, Monat, Quartal und Jahr aller berührten Tage als Summe ihrer Kinder: abgeschlossene Kinder aus dem Speicher
    /// plus die laufende Schicht. Offen ist eine Periode, solange sie über den Datenstand hinausreicht.
    /// </summary>
    private void WriteParents(
        TxWriter tx, RollupContext context, RunningShift? run, Dictionary<string, StationTotals> runningTotals, WindowWork work,
        DateTimeOffset dataEnd)
    {
        var written = new HashSet<(string Station, RollupLevel Level, string Key)>();
        foreach (var (dayKey, stations) in work.DirtyDays)
        {
            var day = PeriodKeys.ParseDay(dayKey);
            foreach (var level in new[] { RollupLevel.Day }.Concat(RollupLevels.AboveDay))
            {
                var key = PeriodKeys.Of(level, day, context.Calendar);
                var isOpen = PeriodBounds.Of(level, key, context.Calendar).EndUtc > dataEnd;
                foreach (var station in stations)
                {
                    if (!written.Add((station, level, key))) continue;

                    (string ShiftKey, StationTotals Value)? current =
                        run is not null && runningTotals.TryGetValue(station, out var totals) ? (run.Period.Key, totals) : null;
                    var sum = level == RollupLevel.Day ? _counters!.Day(station, dayKey, current) : _counters!.Parent(station, level, key, current);
                    if (sum is null) continue;

                    sum.IsOpen = isOpen;
                    WriteTotals(tx, station, level, key, sum);
                }
            }
        }
    }

    private static void WriteTotals(TxWriter tx, string station, RollupLevel level, string period, StationTotals totals)
    {
        var retention = Retention.For(level);
        tx.HashSet(RollupKeys.Counters(station, level, period), totals.ToEntries(), retention);
        tx.SortedSetReplace(
            RollupKeys.ReasonDuration(station, level, period),
            [.. totals.Reasons.Select(r => new SortedSetEntry(r.Key, ExactDouble(r.Value.Duration.Ticks)))], retention);
        tx.SortedSetReplace(
            RollupKeys.ReasonCount(station, level, period),
            [.. totals.Reasons.Select(r => new SortedSetEntry(r.Key, r.Value.Count))], retention);
    }

    /// <summary>Scores sind Doubles; Ticks bleiben darin bis 2^53 exakt (rund 29 Jahre Stillstand einer Station).</summary>
    private static double ExactDouble(long value) =>
        Math.Abs(value) <= (1L << 53) ? value : throw new InvalidOperationException("Wert nicht exakt als Score darstellbar.");

    // ---- Neuberechnung früherer Schichten ---------------------------------------------------------------------

    /// <summary>
    /// Ein Ereignis, das vor der laufenden Schicht begonnen hat, kann frühere Schichten ändern (Endemeldung, nachgetragener Grund).
    /// Stück, Ausschuss und Plan bleiben; der ereignisabhängige Teil wird mit dem Builder neu gerechnet.
    /// </summary>
    private async Task RecomputeEarlierShiftsAsync(RollupContext context, ProductionPeriod current, WindowWork work, TxWriter tx)
    {
        var calendar = context.Calendar;
        var shifts = new SortedDictionary<DateTimeOffset, ProductionPeriod>();
        foreach (var start in work.EarlierEventStarts)
        {
            for (var shift = calendar.ShiftPeriodOf(start); shift.StartUtc < current.StartUtc; shift = calendar.ShiftPeriodOf(shift.EndUtc))
            {
                shifts[shift.StartUtc] = shift;
            }
        }

        foreach (var shift in shifts.Values)
        {
            var stations = _counters!.ShiftKeys.Where(k => k.ShiftKey == shift.Key).Select(k => k.Owner).ToList();
            if (stations.Count == 0) continue;

            var builder = new KpiBlockBuilder(shift, context.Costs);
            foreach (var station in stations)
            {
                var stored = _counters.Shift(station, shift.Key)!;
                var f = stored.Figures;
                builder.RestorePieces(station, f.GoodPieces, f.ScrapPieces, f.IdealSeconds, f.GoodIdealSeconds, f.ScrapCostEuro);
                if (stored.Plan is { } plan)
                {
                    builder.Add(new ShiftPlanRecord(station, plan.StartUtc, shift.Key, plan.StartUtc, plan.EndUtc, plan.PlannedQuantity));
                }
            }

            foreach (var loss in await _reader.ReadLossesOverlappingAsync(shift.StartUtc, shift.EndUtc)) builder.Add(loss);
            foreach (var loss in work.Losses.Values) builder.Add(loss);

            var block = builder.Snapshot(shift.EndUtc);
            foreach (var station in stations)
            {
                var stored = _counters.Shift(station, shift.Key)!;
                var stationBlock = block.Stations.GetValueOrDefault(station) ?? StationBlock.Empty;
                var updated = StationTotals.From(stationBlock, stored.ScrapByReason, false, shift.EndUtc);
                updated.Plan = stored.Plan;
                _counters.SetShift(station, shift.Key, updated);
                WriteTotals(tx, station, RollupLevel.Shift, shift.Key, updated);
                work.MarkDirtyDay(PeriodKeys.Day(PeriodKeys.DayOfShift(shift.Key)), station);
            }
        }
    }
}
