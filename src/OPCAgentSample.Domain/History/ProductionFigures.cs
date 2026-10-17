namespace OPCAgentSample.Domain.History;

/// <summary>
/// Additive Grundgrößen einer Station in einem Zeitbereich (Kennzahlenblock). Alle Kennzahlen (OEE, Ist/Soll, MTBF …)
/// werden aus diesen Summen gebildet, nie aus Prozenten gemittelt; Blöcke lassen sich daher zu Tag, Woche, Bereich
/// und Halle addieren. Zeiten sind exakt (Ticks), Mengen ganzzahlig, Soll-Zeiten und Kosten dezimal.
/// <see cref="IdealSeconds"/> = Σ Stück × Soll-Takt, <see cref="GoodIdealSeconds"/> = Σ Gutteile × Soll-Takt.
/// </summary>
public sealed record ProductionFigures(
    TimeSpan ScheduledTime,
    TimeSpan PlannedStopTime,
    TimeSpan AvailabilityLossTime,
    TimeSpan MinorStopTime,
    long GoodPieces,
    long ScrapPieces,
    decimal IdealSeconds,
    decimal GoodIdealSeconds,
    decimal PlannedQuantity,
    int BreakdownCount,
    TimeSpan BreakdownTime,
    TimeSpan MaintenanceTime,
    decimal ScrapCostEuro,
    long OverproductionPieces)
{
    public static ProductionFigures Empty { get; } =
        new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0, 0m, 0m, 0m, 0, TimeSpan.Zero, TimeSpan.Zero, 0m, 0);

    /// <summary>Planbelegungszeit = Schichtzeit minus geplante Stopps (Rüsten im Soll, geplante Wartung).</summary>
    public TimeSpan PlanningTime => ScheduledTime - PlannedStopTime;

    /// <summary>Laufzeit = Planbelegungszeit minus ungeplanter Stillstand (Kurzstopps zählen zur Laufzeit).</summary>
    public TimeSpan RunTime => PlanningTime - AvailabilityLossTime;

    /// <summary>Ist = gefertigte Stück (Gut + Ausschuss).</summary>
    public long TotalPieces => GoodPieces + ScrapPieces;

    public static ProductionFigures operator +(ProductionFigures left, ProductionFigures right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Add(right);
    }

    public ProductionFigures Add(ProductionFigures other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(
            ScheduledTime + other.ScheduledTime,
            PlannedStopTime + other.PlannedStopTime,
            AvailabilityLossTime + other.AvailabilityLossTime,
            MinorStopTime + other.MinorStopTime,
            GoodPieces + other.GoodPieces,
            ScrapPieces + other.ScrapPieces,
            IdealSeconds + other.IdealSeconds,
            GoodIdealSeconds + other.GoodIdealSeconds,
            PlannedQuantity + other.PlannedQuantity,
            BreakdownCount + other.BreakdownCount,
            BreakdownTime + other.BreakdownTime,
            MaintenanceTime + other.MaintenanceTime,
            ScrapCostEuro + other.ScrapCostEuro,
            OverproductionPieces + other.OverproductionPieces);
    }

    public static ProductionFigures Sum(IEnumerable<ProductionFigures> parts) =>
        parts.Aggregate(Empty, (sum, part) => sum.Add(part));
}

/// <summary>Stillstand eines Pareto-Grunds: Dauer und Anzahl (Anzahl nach Beginn im Zeitbereich).</summary>
public sealed record ReasonFigures(TimeSpan Duration, int Count)
{
    public static ReasonFigures Empty { get; } = new(TimeSpan.Zero, 0);

    public ReasonFigures Add(ReasonFigures other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Duration + other.Duration, Count + other.Count);
    }
}

/// <summary>
/// Tageskennzahl einer Station (Historie B): die Grundgrößen eines Produktionstags und der
/// Stillstand je Pareto-Grund. Für Tage vor dem Rohdatenfenster gibt es nur diese Verdichtung.
/// </summary>
public sealed record DailyStationKpi(
    string StationId,
    DateTimeOffset TimestampUtc,
    DateOnly Day,
    ProductionFigures Figures,
    IReadOnlyDictionary<string, ReasonFigures> Reasons) : HistoryRecord(StationId, TimestampUtc);
