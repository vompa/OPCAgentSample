using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Modell einer Produktionsstation (Rundtisch oder Verpackung). Rechnet Schichten bei Bedarf (zwischengespeichert)
/// und liefert für ein beliebiges Fenster alle Datensätze der Station. Jedes Fenster ist ein exakter Ausschnitt des
/// Gesamtlaufs; darauf beruht die Live-Fortsetzung ab der Demo-Uhr.
/// </summary>
internal sealed class ProductionStationModel
{
    private const double KgPerGram = 0.001;
    private static readonly TimeSpan s_mixtureInterval = TimeSpan.FromMinutes(160);
    private static readonly TimeSpan s_mixturePotLife = TimeSpan.FromHours(4);
    private const double MixtureKilogramsPerDelivery = 340;
    private const int DeliveriesPerShift = 3;
    private const int MaxCachedCycleRuns = 4;
    private static readonly TimeSpan s_shippingLookback = TimeSpan.FromDays(4);
    private static readonly TimeSpan s_orderHorizon = TimeSpan.FromDays(21);

    private readonly ScenarioDefinition _scenario;
    private readonly ProductionShiftIndex _shifts;
    private readonly ShiftRunBuilder _builder;
    private readonly int _seed;
    private readonly Dictionary<string, ShiftRun> _runs = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<(DateTimeOffset, DateTimeOffset, int), DateTimeOffset?> _quantityReached = [];
    private readonly Lock _cycleLock = new();
    private readonly Dictionary<string, CycleRecord[]> _cycles = [];
    private readonly Queue<string> _cycleOrder = new();
    private readonly Dictionary<string, IReadOnlyList<PackedPallet>> _pallets = [];
    private List<MixtureDeliveryInfo>? _mixtureDeliveries;

    public ProductionStationModel(ScenarioDefinition scenario, StationPlan plan, ProductionShiftIndex shifts, int seed)
    {
        _scenario = scenario;
        Plan = plan;
        _shifts = shifts;
        _seed = seed;
        _builder = new ShiftRunBuilder(scenario, plan, seed);
    }

    public StationPlan Plan { get; }

    public string StationId => Plan.StationId;

    public bool IsPress => Plan.Spec.IsPress;

    public ShiftRun Run(ProductionPeriod shift)
    {
        lock (_lock)
        {
            if (!_runs.TryGetValue(shift.Key, out var run))
            {
                run = _builder.Build(shift);
                _runs[shift.Key] = run;
            }

            return run;
        }
    }

    public IEnumerable<ShiftRun> RunsBetween(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        _shifts.Between(fromUtc, toUtc).Select(Run);

    /// <summary>
    /// Alle Taktwerte der Schicht. Die Werte einer Schicht entstehen aus einem fortlaufenden Zufallsstrom und sind
    /// teuer; die jüngsten Schichten bleiben deshalb im Speicher, damit der Live-Takt nicht jede Sekunde die ganze
    /// Schicht neu rechnet.
    /// </summary>
    private CycleRecord[] CyclesOf(ShiftRun run)
    {
        lock (_cycleLock)
        {
            if (_cycles.TryGetValue(run.Shift.Key, out var cached)) return cached;

            var cycles = CycleValueGenerator.Cycles(_scenario, Plan, run, _seed).ToArray();
            _cycles[run.Shift.Key] = cycles;
            _cycleOrder.Enqueue(run.Shift.Key);
            while (_cycleOrder.Count > MaxCachedCycleRuns) _cycles.Remove(_cycleOrder.Dequeue());
            return cycles;
        }
    }

    /// <summary>Taktwerte mit Zeitstempel in [<paramref name="fromUtc"/>, <paramref name="untilUtc"/>), aufsteigend.</summary>
    public IEnumerable<CycleRecord> CyclesBetween(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        foreach (var run in RunsBetween(fromUtc, untilUtc))
        {
            foreach (var cycle in CyclesIn(run, fromUtc, untilUtc)) yield return cycle;
        }
    }

    private IEnumerable<CycleRecord> CyclesIn(ShiftRun run, DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var first = run.CountBefore(fromUtc);
        var end = run.CountBefore(untilUtc);
        if (first >= end) yield break;

        var cycles = CyclesOf(run);
        for (var i = first; i < end; i++) yield return cycles[i];
    }

    /// <summary>Paletten der Schicht (unabhängig vom Ausgabefenster), einmal berechnet.</summary>
    internal IReadOnlyList<PackedPallet> PalletsOf(ShiftRun run)
    {
        lock (_cycleLock)
        {
            if (_pallets.TryGetValue(run.Shift.Key, out var cached)) return cached;
            return _pallets[run.Shift.Key] = PackagingTrace.PackPallets(_scenario, this, run);
        }
    }

    public int CountCycles(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        fromUtc >= toUtc ? 0 : RunsBetween(fromUtc, toUtc).Sum(run => run.CountBetween(fromUtc, toUtc));

    public int CountScrap(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        fromUtc >= toUtc ? 0 : RunsBetween(fromUtc, toUtc).Sum(run => run.ScrapBetween(fromUtc, toUtc));

    /// <summary>Alle Datensätze der Station mit Zeitstempel im Fenster (unsortiert).</summary>
    public List<HistoryRecord> Generate(DateTimeOffset fromUtc, DateTimeOffset untilUtc, OvenLookup ovens)
    {
        var records = new List<HistoryRecord>();
        // Eine Schicht, die genau am Fensterbeginn endet, liefert dort noch ihre Lose (Zeitstempel = Schichtende).
        foreach (var run in RunsBetween(fromUtc - TimeSpan.FromTicks(1), untilUtc))
        {
            AddShiftRecords(run, fromUtc, untilUtc, records);
        }

        if (!IsPress)
        {
            // Versandmeldungen können Paletten früherer Schichten betreffen.
            var window = new TimeWindow(fromUtc, untilUtc);
            foreach (var run in RunsBetween(fromUtc - s_shippingLookback, untilUtc))
            {
                records.AddRange(PackagingTrace.Pallets(_scenario, this, run, ovens, window));
            }
        }

        AddLossEvents(fromUtc, untilUtc, records);
        AddStateChanges(fromUtc, untilUtc, records);
        AddOrderEvents(fromUtc, untilUtc, records);
        AddMinuteValues(fromUtc, untilUtc, records);
        return records;
    }

    private void AddShiftRecords(ShiftRun run, DateTimeOffset from, DateTimeOffset until, List<HistoryRecord> records)
    {
        var window = new TimeWindow(from, until);
        if (window.Contains(run.Shift.StartUtc))
        {
            records.Add(new ShiftPlanRecord(StationId, run.Shift.StartUtc, run.Shift.Key, run.Shift.StartUtc, run.Shift.EndUtc, run.PlannedQuantity));
        }

        foreach (var stop in run.NoiseStops) AddVersions(stop, window, records);
        if (!run.StartupWindow.IsEmpty) AddVersions(StartupLoss(run), window, records);

        if (IsPress)
        {
            records.AddRange(CyclesIn(run, window.StartUtc, window.EndUtc));
            records.AddRange(PressLots(run).Where(lot => window.Contains(lot.TimestampUtc)));
        }
        else
        {
            records.AddRange(UnitCounts(run, window));
        }
    }

    private PlannedLoss StartupLoss(ShiftRun run)
    {
        var startup = run.StartupWindow;
        var model = _scenario.Model;
        var nominal = run.Profile.MeasuredCycle((startup.StartUtc - run.Shift.StartUtc).TotalSeconds) / model.StartupCycleStretch;
        var lost = startup.Duration.TotalSeconds * (1 - (model.StartupSpeedFactor / model.StartupCycleStretch)) / nominal;
        return new PlannedLoss($"A-{StationId}-{run.Shift.Key}", StationId, LossCategory.SpeedLoss, LossReasons.StartupLoss,
            Assemblies.Station, "Anlaufverlust nach Schichtwechsel", startup.StartUtc, startup.EndUtc, (int)Math.Round(lost), true, null);
    }

    private IEnumerable<PressLot> PressLots(ShiftRun run)
    {
        var bounds = Plan.Batches
            .Where(b => b.StartUtc > run.Shift.StartUtc && b.StartUtc < run.Shift.EndUtc)
            .Select(b => b.StartUtc)
            .Prepend(run.Shift.StartUtc)
            .Append(run.Shift.EndUtc)
            .ToList();
        for (var i = 0; i + 1 < bounds.Count; i++)
        {
            var (start, end) = (bounds[i], bounds[i + 1]);
            var good = run.CountBetween(start, end) - run.ScrapBetween(start, end);
            if (good <= 0) continue;
            var batch = Plan.BatchAt(start);
            yield return new PressLot(StationId, end, LotId(run.Shift.Key, batch), batch, Plan.AssignmentAt(start).ArticleId,
                run.Shift.Key, start, end, good);
        }
    }

    public string LotId(string shiftKey, string batchId) => $"L-{StationId}-{shiftKey}-{batchId}";

    /// <summary>Pressenlose, die das Fenster überlappen (für die Rückverfolgung der Ofenchargen).</summary>
    public IEnumerable<string> LotIdsOverlapping(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        RunsBetween(fromUtc, toUtc).SelectMany(PressLots).Where(l => l.StartUtc < toUtc && l.EndUtc > fromUtc).Select(l => l.LotId);

    private IEnumerable<UnitCountRecord> UnitCounts(ShiftRun run, TimeWindow window)
    {
        var minute = TimeSpan.FromMinutes(1);
        var from = MinuteCeiling(window.StartUtc > run.Shift.StartUtc ? window.StartUtc : run.Shift.StartUtc);
        var until = window.EndUtc < run.Shift.EndUtc ? window.EndUtc : run.Shift.EndUtc;
        for (var t = from; t < until; t += minute)
        {
            var total = run.CountBetween(t, t + minute);
            if (total == 0) continue;
            var scrap = run.ScrapBetween(t, t + minute);
            var article = Plan.AssignmentAt(t).ArticleId;
            yield return new UnitCountRecord(StationId, t, article, total - scrap, scrap, _scenario.PackagingArticle(article).TargetCycleSeconds);
        }
    }

    private void AddLossEvents(DateTimeOffset from, DateTimeOffset until, List<HistoryRecord> records)
    {
        var window = new TimeWindow(from, until);
        foreach (var loss in Plan.Losses)
        {
            AddVersions(loss, window, records);
            if (loss.Category == LossCategory.Breakdown) AlarmVersions.AddFaultAlarm(loss, window, records);
        }

        foreach (var alarm in _scenario.Alarms.Where(a => a.StationId == StationId))
        {
            AlarmVersions.AddScripted(alarm, window, records);
        }
    }

    internal static void AddVersions(PlannedLoss loss, TimeWindow window, List<HistoryRecord> records)
    {
        if (window.Contains(loss.StartUtc)) records.Add(loss.ToEvent(loss.StartUtc, closed: false));
        if (window.Contains(loss.EndUtc)) records.Add(loss.ToEvent(loss.EndUtc, closed: true));
    }

    /// <summary>Betriebszustand und Auftrag zu einem Zeitpunkt.</summary>
    public (OperatingState State, string? OrderId) StateAt(DateTimeOffset instant)
    {
        var shift = _shifts.At(instant);
        if (shift is null) return (OperatingState.Idle, null);
        var assignment = Plan.AssignmentAt(instant);
        if (Plan.BlockAt(instant) is { } block) return (block.State, assignment.OrderId);
        if (Run(shift).NoiseStopAt(instant) is not null) return (OperatingState.Fault, assignment.OrderId);
        return IsPress && assignment.OrderId is null
            ? (OperatingState.RunningWithoutOrder, null)
            : (OperatingState.Running, assignment.OrderId);
    }

    private void AddStateChanges(DateTimeOffset from, DateTimeOffset until, List<HistoryRecord> records) =>
        records.AddRange(StateChangesBetween(from, until));

    /// <summary>Zustandswechsel im Fenster (ein Wechsel liegt vor, wenn sich Zustand oder Auftrag ändert).</summary>
    public List<StateChange> StateChangesBetween(DateTimeOffset from, DateTimeOffset until)
    {
        var records = new List<StateChange>();
        var instants = new SortedSet<DateTimeOffset>();
        foreach (var shift in _shifts.Between(from - TimeSpan.FromDays(1), until + TimeSpan.FromDays(1)))
        {
            instants.Add(shift.StartUtc);
            instants.Add(shift.EndUtc);
            foreach (var stop in Run(shift).NoiseStops)
            {
                instants.Add(stop.StartUtc);
                instants.Add(stop.EndUtc);
            }
        }

        foreach (var block in Plan.Blocks)
        {
            instants.Add(block.StartUtc);
            instants.Add(block.EndUtc);
        }

        foreach (var assignment in Plan.Assignments) instants.Add(assignment.StartUtc);
        instants.Add(_scenario.HistoryStartUtc);

        foreach (var instant in instants.GetViewBetween(from, until))
        {
            if (instant == until) continue;
            var (state, order) = StateAt(instant);
            var isFirst = instant == _scenario.HistoryStartUtc;
            if (!isFirst && (state, order) == StateAt(instant - TimeSpan.FromTicks(1))) continue;
            records.Add(new StateChange(StationId, instant, state, order));
        }

        return records;
    }

    private void AddOrderEvents(DateTimeOffset from, DateTimeOffset until, List<HistoryRecord> records)
    {
        var window = new TimeWindow(from, until);
        for (var i = 0; i < Plan.Assignments.Count; i++)
        {
            var assignment = Plan.Assignments[i];
            if (assignment.OrderId is not { } orderId) continue;
            var end = i + 1 < Plan.Assignments.Count ? Plan.Assignments[i + 1].StartUtc : DateTimeOffset.MaxValue;
            if (window.Contains(assignment.StartUtc))
            {
                records.Add(new OrderEvent(StationId, assignment.StartUtc, orderId, OrderEventKind.Started, 0));
            }

            var quantity = _scenario.Orders.FirstOrDefault(o => o.Id == orderId)?.Quantity;
            if (quantity is { } target && QuantityReachedAt(assignment.StartUtc, end, target) is { } reached && window.Contains(reached))
            {
                records.Add(new OrderEvent(StationId, reached, orderId, OrderEventKind.QuantityReached, target));
            }

            if (end != DateTimeOffset.MaxValue && window.Contains(end))
            {
                records.Add(new OrderEvent(StationId, end, orderId, OrderEventKind.Completed, CountCycles(assignment.StartUtc, end)));
            }
        }
    }

    /// <summary>Zeitpunkt des Takts, mit dem die Auftragsmenge erreicht wird (innerhalb der Zuordnung), sonst <c>null</c>.</summary>
    public DateTimeOffset? QuantityReachedAt(DateTimeOffset startUtc, DateTimeOffset endUtc, int quantity)
    {
        var key = (startUtc, endUtc, quantity);
        lock (_lock)
        {
            if (_quantityReached.TryGetValue(key, out var cached)) return cached;
        }

        var reached = FindQuantityReached(startUtc, endUtc, quantity);
        lock (_lock)
        {
            _quantityReached[key] = reached;
        }

        return reached;
    }

    private DateTimeOffset? FindQuantityReached(DateTimeOffset startUtc, DateTimeOffset endUtc, int quantity)
    {
        var horizon = startUtc + s_orderHorizon;
        var until = endUtc < horizon ? endUtc : horizon;
        var produced = 0;
        foreach (var run in RunsBetween(startUtc, until))
        {
            var first = run.CountBefore(startUtc > run.Shift.StartUtc ? startUtc : run.Shift.StartUtc);
            var count = run.CountBetween(startUtc, until);
            if (produced + count >= quantity) return new DateTimeOffset(run.CycleTicks[first + (quantity - produced) - 1], TimeSpan.Zero);
            produced += count;
        }

        return null;
    }

    private void AddMinuteValues(DateTimeOffset from, DateTimeOffset until, List<HistoryRecord> records)
    {
        var minute = TimeSpan.FromMinutes(1);
        for (var t = MinuteCeiling(from); t < until; t += minute)
        {
            var shift = _shifts.At(t);
            var run = shift is null ? null : Run(shift);
            var good = run is null ? 0 : run.CountBefore(t) - run.ScrapBefore(t);
            var scrap = run?.ScrapBefore(t) ?? 0;
            records.Add(new MinuteValue(StationId, t, MinuteVariables.GoodCount, good));
            records.Add(new MinuteValue(StationId, t, MinuteVariables.ScrapCount, scrap));
            records.Add(new MinuteValue(StationId, t, MinuteVariables.EnergyKwh, Energy(run, t, good + scrap)));
            foreach (var consumable in Plan.Consumables)
            {
                records.Add(new MinuteValue(StationId, t, consumable.Variable, ConsumableLevel(consumable, t)));
            }

            if (IsPress)
            {
                AddPressProcessValues(run, t, records);
            }
            else
            {
                records.Add(new MinuteValue(StationId, t, MinuteVariables.CheckweigherRejects, scrap));
            }
        }
    }

    private void AddPressProcessValues(ShiftRun? run, DateTimeOffset t, List<HistoryRecord> records)
    {
        var noise = MinuteNoise.For(_seed, StationId, t);
        var producing = run is not null;
        var sinceStart = producing ? (t - run!.Shift.StartUtc).TotalMinutes : 0;
        var driftExcess = Plan.BuildUpAt(t, _scenario.Model.BuildUpNominalSeconds) - _scenario.Model.BuildUpNominalSeconds;
        var oil = producing ? 38 + (9 * (1 - Math.Exp(-sinceStart / 40))) + (driftExcess * 8) + noise.Next(0.15) : 26 + noise.Next(0.1);
        var pressure = producing ? 210 - (driftExcess * 32) + noise.Next(0.4) : 0;
        var mould = producing ? 35 + (3 * (1 - Math.Exp(-sinceStart / 60))) + noise.Next(0.2) : 24 + noise.Next(0.1);
        records.Add(new MinuteValue(StationId, t, MinuteVariables.OilTemperature, Math.Round(oil, 2)));
        records.Add(new MinuteValue(StationId, t, MinuteVariables.SystemPressure, Math.Round(pressure, 1)));
        records.Add(new MinuteValue(StationId, t, MinuteVariables.MouldTemperature, Math.Round(mould, 2)));
        records.Add(new MinuteValue(StationId, t, MinuteVariables.MixtureLevelKg, Math.Round(MixtureLevel(t).Kilograms, 1)));
    }

    private double Energy(ShiftRun? run, DateTimeOffset t, int pieces)
    {
        if (run is null) return 0;
        var minutes = (t - run.Shift.StartUtc).TotalMinutes;
        var (idlePerMinute, perPiece) = IsPress ? (0.06, 0.0045) : (0.02, 0.01);
        return Math.Round((minutes * idlePerMinute) + (pieces * perPiece), 2);
    }

    /// <summary>Füllstand eines Verbrauchsmaterials: Kapazität minus Verbrauch seit dem letzten Nachfüllen.</summary>
    public double ConsumableLevel(ConsumableSpec consumable, DateTimeOffset instant)
    {
        var origin = _scenario.HistoryStartUtc + consumable.RefillOffset;
        var cycles = Math.Floor((instant - origin) / consumable.RefillInterval);
        var lastRefill = origin + (consumable.RefillInterval * cycles);
        var used = CountCycles(lastRefill, instant) * consumable.PerPiece;
        return Math.Max(0, consumable.Capacity - used);
    }

    /// <summary>Mischung am Rundtisch: Füllstand und „verarbeitbar bis“ des aktuellen Gebindes.</summary>
    public MixtureState MixtureLevel(DateTimeOffset instant)
    {
        var delivery = MixtureDeliveries().LastOrDefault(d => d.DeliveredUtc <= instant);
        if (delivery is null) return new MixtureState(0, null, Plan.BatchAt(instant));
        var article = _scenario.Article(Plan.AssignmentAt(instant).ArticleId);
        var used = CountCycles(delivery.DeliveredUtc, instant) * article.FillWeightTargetGrams * KgPerGram;
        return new MixtureState(Math.Max(0, delivery.Kilograms - used), delivery.ProcessableUntilUtc, Plan.BatchAt(instant));
    }

    private List<MixtureDeliveryInfo> MixtureDeliveries()
    {
        lock (_lock)
        {
            if (_mixtureDeliveries is not null) return _mixtureDeliveries;
            var scripted = Plan.MixtureDeliveries;
            var span = scripted.Count == 0
                ? new TimeWindow(DateTimeOffset.MinValue, DateTimeOffset.MinValue)
                : new TimeWindow(scripted[0].DeliveredUtc, scripted[^1].ProcessableUntilUtc);
            var periodic = _shifts.Between(_scenario.HistoryStartUtc, _scenario.DemoTimeUtc + TimeSpan.FromDays(21))
                .SelectMany(shift => Enumerable.Range(0, DeliveriesPerShift)
                    .Select(i => shift.StartUtc + (s_mixtureInterval * i)))
                .Where(at => !span.Contains(at))
                .Select(at => new MixtureDeliveryInfo(at, MixtureKilogramsPerDelivery, at + s_mixturePotLife));
            _mixtureDeliveries = [.. periodic
                .Concat(scripted.Select(d => new MixtureDeliveryInfo(d.DeliveredUtc, d.Kilograms, d.ProcessableUntilUtc)))
                .OrderBy(d => d.DeliveredUtc)];
            return _mixtureDeliveries;
        }
    }

    internal static DateTimeOffset MinuteCeiling(DateTimeOffset instant)
    {
        var remainder = instant.UtcTicks % TimeSpan.TicksPerMinute;
        return remainder == 0 ? instant : new DateTimeOffset(instant.UtcTicks - remainder + TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    private sealed record MixtureDeliveryInfo(DateTimeOffset DeliveredUtc, double Kilograms, DateTimeOffset ProcessableUntilUtc);
}

/// <summary>Zustand der Mischung an einem Rundtisch.</summary>
public sealed record MixtureState(double Kilograms, DateTimeOffset? ProcessableUntilUtc, string BatchId);

/// <summary>Zugriff der Verpackung auf die Ofenchargen (Rückverfolgung).</summary>
internal delegate IReadOnlyList<string> OvenLookup(int diameterMm, DateTimeOffset fromUtc, DateTimeOffset toUtc);
