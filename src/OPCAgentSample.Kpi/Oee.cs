using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>
/// OEE = Verfügbarkeit × Leistung × Qualität (Werte 0…1).
/// <list type="bullet">
/// <item>Planbelegungszeit = Schichtzeit − geplante Stopps (Rüsten im Soll, geplante Wartung).</item>
/// <item>Verfügbarkeit = Laufzeit / Planbelegungszeit; Laufzeit = Planbelegungszeit − ungeplanter Stillstand
/// (Störung, Warten auf Material/Vorstufe, Personal fehlt, Rüsten über Soll, Qualitätssperre).</item>
/// <item>Leistung = Σ (Stück × Soll-Takt) / Laufzeit; Kurzstopps, Taktverluste und Anlaufverluste wirken hier.</item>
/// <item>Qualität = Σ (Gut × Soll-Takt) / Σ (Stück × Soll-Takt); je Station gleich Gut / (Gut + Ausschuss), über
/// Stationen mit verschiedenen Einheiten (Scheiben, Kartons) zeitgewichtet.</item>
/// </list>
/// Damit gilt OEE = Σ (Gut × Soll-Takt) / Planbelegungszeit. Für Bereiche, Hallen und Zeiträume werden zuerst die
/// Grundgrößen addiert und dann die Quoten gebildet; nie werden Prozente gemittelt.
/// </summary>
public sealed record OeeResult(double Availability, double Performance, double Quality)
{
    public double Oee => Availability * Performance * Quality;

    /// <summary>Prozentwert gerundet wie in der Oberfläche (kaufmännisch, ganze Prozent).</summary>
    public static int Percent(double ratio) => (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
}

public static class OeeCalculator
{
    public static OeeResult From(ProductionFigures figures)
    {
        ArgumentNullException.ThrowIfNull(figures);
        var planning = figures.PlanningTime.TotalSeconds;
        var run = figures.RunTime.TotalSeconds;
        var availability = planning > 0 ? run / planning : 0;
        var performance = run > 0 ? (double)figures.IdealSeconds / run : 0;
        var quality = figures.IdealSeconds > 0 ? (double)(figures.GoodIdealSeconds / figures.IdealSeconds) : 0;
        return new OeeResult(availability, performance, quality);
    }

    public static OeeResult From(KpiBlock block, IEnumerable<string> stationIds)
    {
        ArgumentNullException.ThrowIfNull(block);
        return From(block.Figures(stationIds));
    }
}
