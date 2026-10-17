using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Snapshot;

/// <summary>
/// Hallen B und C liefern nur Zähler (Spezifikation Abschnitt 2). Ihre Wochenzähler aus dem Szenario werden als
/// Tageskennzahlen auf die Produktionstage verteilt (Summe je Woche bleibt exakt erhalten).
/// </summary>
public static class CounterHalls
{
    private static readonly (string Week, int FirstDay, int Days)[] s_weeks = [(PlantComparison.Week41, 5, 5), (PlantComparison.Week42, 12, 3)];

    public static IEnumerable<DailyStationKpi> Days(ShiftCalendar calendar)
    {
        foreach (var station in new[] { PlantComparison.HallBStation, PlantComparison.HallCStation })
        {
            foreach (var (week, firstDay, days) in s_weeks)
            {
                var total = PlantComparison.Block(week).Stations[station];
                for (var i = 0; i < days; i++)
                {
                    var day = new DateOnly(2026, 10, firstDay + i);
                    yield return new DailyStationKpi(station, calendar.DayPeriod(day).EndUtc, day, Part(total.Figures, i, days), Reasons(total.Reasons, i, days));
                }
            }
        }
    }

    private static ProductionFigures Part(ProductionFigures f, int i, int n) => new(
        Share(f.ScheduledTime, i, n), Share(f.PlannedStopTime, i, n), Share(f.AvailabilityLossTime, i, n), Share(f.MinorStopTime, i, n),
        Share(f.GoodPieces, i, n), Share(f.ScrapPieces, i, n), Share(f.IdealSeconds, i, n), Share(f.GoodIdealSeconds, i, n),
        Share(f.PlannedQuantity, i, n), (int)Share(f.BreakdownCount, i, n), Share(f.BreakdownTime, i, n), Share(f.MaintenanceTime, i, n),
        Share(f.ScrapCostEuro, i, n), Share(f.OverproductionPieces, i, n));

    private static Dictionary<string, ReasonFigures> Reasons(IReadOnlyDictionary<string, ReasonFigures> total, int i, int n) =>
        total.ToDictionary(p => p.Key, p => new ReasonFigures(Share(p.Value.Duration, i, n), (int)Share(p.Value.Count, i, n)));

    // Anteil i von n; der letzte Tag erhält den Rest, damit die Summe exakt stimmt.
    private static long Share(long total, int i, int n) => i < n - 1 ? total / n : total - (total / n * (n - 1));

    private static TimeSpan Share(TimeSpan total, int i, int n) => TimeSpan.FromTicks(Share(total.Ticks, i, n));

    private static decimal Share(decimal total, int i, int n)
    {
        var part = decimal.Round(total / n, 4);
        return i < n - 1 ? part : total - (part * (n - 1));
    }
}
