using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Zähler einer Station in einer Periode: die Grundgrößen der Kennzahlen-Bibliothek, Stillstand je Grund und Ausschuss je Grund.
/// Alles ist additiv; Perioden höherer Ebenen sind Summen ihrer Kinder. Dezimalwerte liegen als ganze Zahl mal 10^9 im Hash
/// (exakt addierbar, gemessen: keine Abweichung in den Referenzzahlen).
/// </summary>
public sealed class StationTotals
{
    private const decimal Scale = 1_000_000_000m;

    public ProductionFigures Figures { get; private set; } = ProductionFigures.Empty;

    public Dictionary<string, ReasonFigures> Reasons { get; } = [];

    /// <summary>Ausschuss je Grund (Gewicht, Dicke, Gewebe, Etikett, Bruch); nur Perioden mit Rohdaten.</summary>
    public Dictionary<string, long> ScrapByReason { get; } = [];

    /// <summary>Die Periode ist noch offen (läuft über den Datenstand hinaus).</summary>
    public bool IsOpen { get; set; }

    public DateTimeOffset SourceUntilUtc { get; set; }

    /// <summary>Planfenster und Planmenge der Schicht (nur Schichten): Grundlage der Neuberechnung des ereignisabhängigen Teils.</summary>
    public PlanWindow? Plan { get; set; }

    public static StationTotals From(StationBlock block, IReadOnlyDictionary<string, long>? scrap, bool isOpen, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(block);
        var totals = new StationTotals { Figures = Quantized(block.Figures), IsOpen = isOpen, SourceUntilUtc = until };
        foreach (var (reason, figures) in block.Reasons) totals.Reasons[reason] = figures;
        if (scrap is not null)
        {
            foreach (var (reason, count) in scrap) totals.ScrapByReason[reason] = count;
        }

        return totals;
    }

    /// <summary>Dezimalwerte auf 9 Nachkommastellen gerundet, so wie sie im Hash stehen; ab hier sind alle Summen ganzzahlig exakt.</summary>
    public static ProductionFigures Quantized(ProductionFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        return figures with
        {
            IdealSeconds = Unscaled(Scaled(figures.IdealSeconds)),
            GoodIdealSeconds = Unscaled(Scaled(figures.GoodIdealSeconds)),
            PlannedQuantity = Unscaled(Scaled(figures.PlannedQuantity)),
            ScrapCostEuro = Unscaled(Scaled(figures.ScrapCostEuro)),
        };
    }

    public StationBlock ToBlock() => new(Figures, Reasons);

    public StationTotals Clone()
    {
        var copy = new StationTotals();
        copy.Add(this);
        copy.IsOpen = IsOpen;
        copy.SourceUntilUtc = SourceUntilUtc;
        copy.Plan = Plan;
        return copy;
    }

    public void Add(StationTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Figures = Figures.Add(other.Figures);
        foreach (var (reason, figures) in other.Reasons)
        {
            Reasons[reason] = Reasons.TryGetValue(reason, out var sum) ? sum.Add(figures) : figures;
        }

        foreach (var (reason, count) in other.ScrapByReason)
        {
            ScrapByReason[reason] = ScrapByReason.GetValueOrDefault(reason) + count;
        }

        IsOpen |= other.IsOpen;
        if (other.SourceUntilUtc > SourceUntilUtc) SourceUntilUtc = other.SourceUntilUtc;
    }

    /// <summary>Felder des Hashes <c>kc:…</c>; kurze Namen sparen Speicher (Zuordnung in der Spezifikation).</summary>
    public HashEntry[] ToEntries()
    {
        var f = Figures;
        var entries = new List<HashEntry>(26)
        {
            new("s", f.ScheduledTime.Ticks),
            new("ps", f.PlannedStopTime.Ticks),
            new("al", f.AvailabilityLossTime.Ticks),
            new("mi", f.MinorStopTime.Ticks),
            new("g", f.GoodPieces),
            new("sc", f.ScrapPieces),
            new("id", Scaled(f.IdealSeconds)),
            new("gi", Scaled(f.GoodIdealSeconds)),
            new("pl", Scaled(f.PlannedQuantity)),
            new("bn", f.BreakdownCount),
            new("bt", f.BreakdownTime.Ticks),
            new("mt", f.MaintenanceTime.Ticks),
            new("co", Scaled(f.ScrapCostEuro)),
            new("ov", f.OverproductionPieces),
            new("op", IsOpen ? 1 : 0),
            new("su", SourceUntilUtc.UtcTicks),
        };
        if (Plan is { } plan)
        {
            entries.Add(new HashEntry("p0", plan.StartUtc.UtcTicks));
            entries.Add(new HashEntry("p1", plan.EndUtc.UtcTicks));
            entries.Add(new HashEntry("pq", plan.PlannedQuantity));
        }

        foreach (var (reason, count) in ScrapByReason) entries.Add(new HashEntry("sr:" + reason, count));
        return [.. entries];
    }

    public static StationTotals FromEntries(HashEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = entries.ToDictionary(e => (string)e.Name!, e => (long)e.Value);
        long Get(string name) => map.GetValueOrDefault(name);
        var figures = new ProductionFigures(
            TimeSpan.FromTicks(Get("s")), TimeSpan.FromTicks(Get("ps")), TimeSpan.FromTicks(Get("al")), TimeSpan.FromTicks(Get("mi")),
            Get("g"), Get("sc"), Unscaled(Get("id")), Unscaled(Get("gi")), Unscaled(Get("pl")), (int)Get("bn"),
            TimeSpan.FromTicks(Get("bt")), TimeSpan.FromTicks(Get("mt")), Unscaled(Get("co")), Get("ov"));
        var totals = new StationTotals
        {
            Figures = figures,
            IsOpen = Get("op") == 1,
            SourceUntilUtc = new DateTimeOffset(Get("su"), TimeSpan.Zero),
        };
        if (map.ContainsKey("p0"))
        {
            totals.Plan = new PlanWindow(
                new DateTimeOffset(Get("p0"), TimeSpan.Zero), new DateTimeOffset(Get("p1"), TimeSpan.Zero), (int)Get("pq"));
        }

        foreach (var (name, value) in map.Where(p => p.Key.StartsWith("sr:", StringComparison.Ordinal)))
        {
            totals.ScrapByReason[name[3..]] = value;
        }

        return totals;
    }

    private static long Scaled(decimal value) => decimal.ToInt64(decimal.Round(value * Scale));

    private static decimal Unscaled(long value) => value / Scale;
}

/// <summary>Planfenster und Planmenge einer Station in einer Schicht (<see cref="ShiftPlanRecord"/>).</summary>
public sealed record PlanWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int PlannedQuantity);
