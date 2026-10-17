using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Simulation.Scenario;

/// <summary>Verdichtete Zähler einer Halle (B oder C) für eine Woche und eine Schichtart.</summary>
public sealed record HallShiftFigures(string HallStationId, string WeekKey, Shift Shift, ProductionFigures Figures, IReadOnlyDictionary<string, ReasonFigures> Reasons);

/// <summary>
/// Hallen B und C liefern nur Zähler und Zustände für den Werksvergleich (Spezifikation Abschnitt 2). Ihre
/// Wochenzähler je Schichtart sind hier als Daten hinterlegt; zusammen mit dem gerechneten Hallen-A-Anteil ergeben sie
/// die Werkszahlen der Referenzantworten (OEE KW 42 79 % / Vorwoche 78 %, Schichtvergleich, Kosten 41.000 €,
/// Ausbringung 1,41 von 1,47 Mio., Mischung nachts 5,8 h). Die Werte sind auf den Hallen-A-Anteil kalibriert.
/// </summary>
public static class PlantComparison
{
    public const string HallBStation = "HALLE-B";
    public const string HallCStation = "HALLE-C";
    public const string Week41 = "2026-W41";
    public const string Week42 = "2026-W42";

    /// <summary>Kostensatz je Stillstandsminute der verdichteten Hallen (Mittel je Stationsminute).</summary>
    public static IReadOnlyDictionary<string, decimal> DowntimeEuroPerMinute { get; } =
        new Dictionary<string, decimal> { [HallBStation] = 1.00m, [HallCStation] = 0.835m };

    // Halle, Woche, Schicht, Planbelegung (min), Laufzeit (min), Soll-Zeit (s), Soll-Zeit gut (s), Ist, Ausschuss, Soll, Ausschusskosten, Mischung (min)
    private static readonly (string Hall, string Week, Shift Shift, double Planning, double Run, double Ideal, double GoodIdeal,
        long Actual, long Scrap, long Planned, decimal ScrapCost, double MixtureMinutes)[] s_rows =
    [
        (HallBStation, Week41, Shift.Early, 17_937.5, 15_516.8, 919_159.9, 896_631.7, 360_000, 1_950, 365_000, 2_082m, 0),
        (HallBStation, Week41, Shift.Late, 17_862.5, 15_186.2, 901_275.6, 878_331.6, 355_000, 1_930, 365_000, 2_059m, 0),
        (HallBStation, Week41, Shift.Night, 17_962.5, 12_911.3, 772_045.8, 762_804.7, 300_000, 1_600, 315_000, 1_709m, 40),
        (HallCStation, Week41, Shift.Early, 17_937.5, 15_516.8, 904_570.1, 882_399.5, 350_000, 1_900, 360_000, 1_723m, 0),
        (HallCStation, Week41, Shift.Late, 17_862.5, 15_186.2, 886_969.7, 864_389.9, 345_000, 1_880, 360_000, 1_704m, 0),
        (HallCStation, Week41, Shift.Night, 17_962.5, 12_911.3, 759_791.1, 750_696.7, 290_000, 1_560, 310_000, 1_444m, 30),
        (HallBStation, Week42, Shift.Early, 12_240.0, 10_754.4, 634_700.1, 622_442.3, 245_000, 3_564, 248_000, 2_470m, 0),
        (HallBStation, Week42, Shift.Late, 10_700.0, 9_214.6, 548_717.0, 534_916.7, 210_000, 3_055, 216_000, 2_118m, 0),
        (HallBStation, Week42, Shift.Night, 10_775.0, 7_968.0, 475_548.7, 473_929.2, 150_000, 2_181, 156_000, 1_512m, 60),
        (HallCStation, Week42, Shift.Early, 12_240.0, 10_754.4, 625_876.0, 613_788.7, 238_000, 3_391, 243_000, 2_048m, 0),
        (HallCStation, Week42, Shift.Late, 10_700.0, 9_214.6, 541_088.4, 527_480.0, 204_475, 2_913, 211_205, 1_759m, 0),
        (HallCStation, Week42, Shift.Night, 10_775.0, 7_968.0, 468_937.3, 467_340.3, 147_000, 2_095, 153_000, 1_265m, 42),
    ];

    public static IReadOnlyList<HallShiftFigures> Figures { get; } = [.. s_rows.Select(ToFigures)];

    /// <summary>Kennzahlenblock der Hallen B und C für eine Woche (optional nur eine Schichtart).</summary>
    public static KpiBlock Block(string weekKey, Shift? shift = null)
    {
        var stations = Figures
            .Where(f => f.WeekKey == weekKey && (shift is null || f.Shift == shift))
            .GroupBy(f => f.HallStationId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => new StationBlock(f.Figures, f.Reasons)).Aggregate(StationBlock.Empty, (sum, part) => sum.Add(part)));
        return new KpiBlock(weekKey, DateTimeOffset.MinValue, stations);
    }

    /// <summary>Kostensätze der Halle A ergänzt um die verdichteten Hallen B und C.</summary>
    public static CostRateTable WithHallRates(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var downtime = scenario.ProductionStations.ToDictionary(s => s.StationId, s => s.DowntimeEuroPerMinute);
        foreach (var oven in scenario.Ovens) downtime[oven.StationId] = oven.DowntimeEuroPerMinute;
        foreach (var (hall, rate) in DowntimeEuroPerMinute) downtime[hall] = rate;
        var scrap = scenario.Articles.ToDictionary(a => a.Id, a => a.ScrapCostEuro);
        foreach (var article in scenario.PackagingArticles) scrap[article.Id] = article.ScrapCostEuro;
        return new CostRateTable(downtime, scrap);
    }

    private static HallShiftFigures ToFigures(
        (string Hall, string Week, Shift Shift, double Planning, double Run, double Ideal, double GoodIdeal,
            long Actual, long Scrap, long Planned, decimal ScrapCost, double MixtureMinutes) row)
    {
        var planning = TimeSpan.FromMinutes(row.Planning);
        var loss = planning - TimeSpan.FromMinutes(row.Run);
        var figures = new ProductionFigures(
            planning, TimeSpan.Zero, loss, TimeSpan.Zero, row.Actual - row.Scrap, row.Scrap, (decimal)row.Ideal,
            (decimal)row.GoodIdeal, row.Planned, 0, TimeSpan.Zero, TimeSpan.Zero, row.ScrapCost, 0);
        var reasons = new Dictionary<string, ReasonFigures>();
        if (row.MixtureMinutes > 0) reasons[LossReasons.MixtureWait] = new ReasonFigures(TimeSpan.FromMinutes(row.MixtureMinutes), 2);
        return new HallShiftFigures(row.Hall, row.Week, row.Shift, figures, reasons);
    }
}
