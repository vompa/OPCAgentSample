using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>
/// Schreibt einen Kennzahlenblock Datensatz für Datensatz fort (inkrementell) und liefert jederzeit einen Stand
/// (<see cref="Snapshot"/>). Die vollständige Neuberechnung ist derselbe Builder über alle Datensätze – es gibt nur
/// eine Rechenfunktion je Kennzahl.
/// </summary>
/// <remarks>
/// Regeln:
/// <list type="bullet">
/// <item>Stück und Soll-Zeiten zählen nach Zeitstempel des Takts bzw. der Minute im Zeitbereich.</item>
/// <item>Schichtzeit = Planfenster (<see cref="ShiftPlanRecord"/>) ∩ Zeitbereich ∩ bis Datenstand.</item>
/// <item>Stillstandszeiten werden auf Schichtzeit und Datenstand geschnitten; offene Ereignisse laufen bis zum Datenstand.</item>
/// <item>Anzahlen (Ereignisse, Störungen, Überproduktion) zählen nach Beginn im Zeitbereich.</item>
/// <item>Ereignisse werden je Id ersetzt (Beginn- und Endemeldung).</item>
/// </list>
/// Der Aufrufer speist nur Datensätze vor dem Datenstand ein.
/// </remarks>
public sealed class KpiBlockBuilder
{
    private readonly ProductionPeriod _period;
    private readonly CostRateTable _costs;
    private readonly HashSet<string>? _stations;
    private readonly Dictionary<string, StationAccumulator> _accumulators = [];

    public KpiBlockBuilder(ProductionPeriod period, CostRateTable costs, IEnumerable<string>? stationIds = null)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(costs);
        _period = period;
        _costs = costs;
        _stations = stationIds is null ? null : [.. stationIds];
    }

    public ProductionPeriod Period => _period;

    public void AddRange(IEnumerable<HistoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            Add(record);
        }
    }

    public void Add(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_stations is not null && !_stations.Contains(record.StationId)) return;

        switch (record)
        {
            case CycleRecord cycle when _period.Contains(cycle.TimestampUtc):
                Accumulator(cycle.StationId).AddPieces(
                    cycle.IsGood ? 1 : 0, cycle.IsGood ? 0 : 1, cycle.TargetCycleSeconds, _costs.ScrapCost(cycle.ArticleId));
                break;
            case UnitCountRecord count when _period.Contains(count.TimestampUtc):
                Accumulator(count.StationId).AddPieces(
                    count.Good, count.Scrap, count.TargetCycleSeconds, _costs.ScrapCost(count.ArticleId));
                break;
            case ShiftPlanRecord plan when plan.StartUtc < _period.EndUtc && plan.EndUtc > _period.StartUtc:
                Accumulator(plan.StationId).Plans.Add(plan);
                break;
            case LossEvent loss:
                Upsert(loss);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Setzt die Stückzahlen und Soll-Zeiten einer Station auf gespeicherte Summen (Wiederaufnahme nach einem Neustart und
    /// Neuberechnung des ereignisabhängigen Teils): Stück und Kosten stammen aus den abgeschlossenen Fenstern, Ereignisse und
    /// Plan werden danach wie gewohnt eingespeist.
    /// </summary>
    public void RestorePieces(
        string stationId, long good, long scrap, decimal idealSeconds, decimal goodIdealSeconds, decimal scrapCostEuro)
    {
        ArgumentException.ThrowIfNullOrEmpty(stationId);
        if (_stations is not null && !_stations.Contains(stationId)) return;

        Accumulator(stationId).RestorePieces(good, scrap, idealSeconds, goodIdealSeconds, scrapCostEuro);
    }

    /// <summary>Stand des Blocks bis <paramref name="sourceUntilUtc"/> (Wasserstand).</summary>
    public KpiBlock Snapshot(DateTimeOffset sourceUntilUtc)
    {
        var stations = _accumulators.ToDictionary(pair => pair.Key, pair => pair.Value.Build(_period, sourceUntilUtc));
        return new KpiBlock(_period.Key, sourceUntilUtc, stations);
    }

    /// <summary>
    /// Neuester Stand je Ereignis-Id. Ein späterer Stand ersetzt den früheren auch dann, wenn er den Zeitbereich nicht
    /// mehr berührt (z. B. Endemeldung eines vorher offenen Ereignisses) – dann fällt das Ereignis heraus.
    /// </summary>
    private void Upsert(LossEvent loss)
    {
        if (Overlaps(loss))
        {
            Accumulator(loss.StationId).Events[loss.Id] = loss;
        }
        else if (_accumulators.TryGetValue(loss.StationId, out var accumulator))
        {
            accumulator.Events.Remove(loss.Id);
        }
    }

    private bool Overlaps(LossEvent loss) =>
        loss.StartUtc < _period.EndUtc && (loss.EndUtc is null || loss.EndUtc > _period.StartUtc);

    private StationAccumulator Accumulator(string stationId)
    {
        if (!_accumulators.TryGetValue(stationId, out var accumulator))
        {
            accumulator = new StationAccumulator();
            _accumulators[stationId] = accumulator;
        }

        return accumulator;
    }

    private sealed class StationAccumulator
    {
        private long _good;
        private long _scrap;
        private decimal _idealSeconds;
        private decimal _goodIdealSeconds;
        private decimal _scrapCost;

        public List<ShiftPlanRecord> Plans { get; } = [];

        public Dictionary<string, LossEvent> Events { get; } = [];

        public void RestorePieces(long good, long scrap, decimal idealSeconds, decimal goodIdealSeconds, decimal scrapCostEuro)
        {
            _good = good;
            _scrap = scrap;
            _idealSeconds = idealSeconds;
            _goodIdealSeconds = goodIdealSeconds;
            _scrapCost = scrapCostEuro;
        }

        public void AddPieces(int good, int scrap, double targetCycleSeconds, decimal scrapCostPerPiece)
        {
            _good += good;
            _scrap += scrap;
            var target = (decimal)targetCycleSeconds;
            _idealSeconds += (good + scrap) * target;
            _goodIdealSeconds += good * target;
            _scrapCost += scrap * scrapCostPerPiece;
        }

        public StationBlock Build(ProductionPeriod period, DateTimeOffset sourceUntil)
        {
            var windowEnd = sourceUntil < period.EndUtc ? sourceUntil : period.EndUtc;
            var scheduled = Plans
                .Select(plan => new TimeWindow(plan.StartUtc, plan.EndUtc).Intersect(period.StartUtc, windowEnd))
                .Where(window => !window.IsEmpty)
                .ToList();
            var plannedQuantity = Plans.Sum(plan => PlannedQuantityShare(plan, period.StartUtc, windowEnd));
            var losses = new LossTotals();
            foreach (var loss in Events.Values)
            {
                losses.Add(loss, period, windowEnd, scheduled);
            }

            var figures = new ProductionFigures(
                ScheduledTime: TimeWindow.TotalDuration(scheduled),
                PlannedStopTime: losses.PlannedStop,
                AvailabilityLossTime: losses.AvailabilityLoss,
                MinorStopTime: losses.MinorStop,
                GoodPieces: _good,
                ScrapPieces: _scrap,
                IdealSeconds: _idealSeconds,
                GoodIdealSeconds: _goodIdealSeconds,
                PlannedQuantity: plannedQuantity,
                BreakdownCount: losses.BreakdownCount,
                BreakdownTime: losses.Breakdown,
                MaintenanceTime: losses.Maintenance,
                ScrapCostEuro: _scrapCost,
                OverproductionPieces: losses.OverproductionPieces);
            return new StationBlock(figures, losses.Reasons);
        }

        private static decimal PlannedQuantityShare(ShiftPlanRecord plan, DateTimeOffset from, DateTimeOffset until)
        {
            var window = new TimeWindow(plan.StartUtc, plan.EndUtc).Intersect(from, until);
            var total = (plan.EndUtc - plan.StartUtc).Ticks;
            return window.IsEmpty || total == 0 ? 0m : plan.PlannedQuantity * (decimal)window.Duration.Ticks / total;
        }
    }

    /// <summary>Summen der Verlustereignisse einer Station; Zuordnung der Kategorien zu Planzeit, Verfügbarkeit und Leistung.</summary>
    private sealed class LossTotals
    {
        public TimeSpan PlannedStop { get; private set; }

        public TimeSpan AvailabilityLoss { get; private set; }

        public TimeSpan MinorStop { get; private set; }

        public TimeSpan Breakdown { get; private set; }

        public TimeSpan Maintenance { get; private set; }

        public int BreakdownCount { get; private set; }

        public long OverproductionPieces { get; private set; }

        public Dictionary<string, ReasonFigures> Reasons { get; } = [];

        public void Add(LossEvent loss, ProductionPeriod period, DateTimeOffset windowEnd, IReadOnlyList<TimeWindow> scheduled)
        {
            var clipped = new TimeWindow(loss.StartUtc, loss.EndUtc ?? windowEnd).Intersect(period.StartUtc, windowEnd);
            var startsInside = loss.StartUtc >= period.StartUtc && loss.StartUtc < windowEnd;
            if (clipped.IsEmpty && !startsInside) return;

            var inSchedule = clipped.OverlapWith(scheduled);
            switch (loss.Category)
            {
                case LossCategory.Overproduction:
                    if (startsInside) OverproductionPieces += loss.AffectedPieces;
                    return;
                case LossCategory.SpeedLoss:
                    return;
                case LossCategory.PlannedMaintenance:
                    PlannedStop += inSchedule;
                    Maintenance += clipped.Duration;
                    return;
                case LossCategory.Setup:
                    PlannedStop += inSchedule;
                    return;
                case LossCategory.MinorStop:
                    MinorStop += inSchedule;
                    break;
                default:
                    // Jede übrige Stillstandskategorie (auch Qualitätssperre) mindert die Verfügbarkeit.
                    AvailabilityLoss += inSchedule;
                    break;
            }

            if (inSchedule <= TimeSpan.Zero) return;
            var counted = startsInside ? 1 : 0;
            if (loss.Category == LossCategory.Breakdown)
            {
                Breakdown += inSchedule;
                BreakdownCount += counted;
            }

            var reason = new ReasonFigures(inSchedule, counted);
            Reasons[loss.Reason] = Reasons.TryGetValue(loss.Reason, out var sum) ? sum.Add(reason) : reason;
        }
    }
}
