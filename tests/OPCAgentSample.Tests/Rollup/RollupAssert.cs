using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;

namespace OPCAgentSample.Tests.Rollup;

/// <summary>
/// Vergleicht Kennzahlen aus Redis mit der Rechnung der Bibliothek. Zeiten, Stück und Anzahlen müssen exakt gleich sein; Dezimalwerte
/// (Soll-Zeiten, Planmenge, Kosten) liegen in Redis auf 10^-9 gerundet und werden mit der Toleranz 10^-6 verglichen.
/// </summary>
internal static class RollupAssert
{
    private const decimal Tolerance = 0.000_001m;

    public static void Equivalent(KpiBlock expected, KpiBlock actual, string label)
    {
        Assert.Equal(
            expected.Stations.Keys.Order().ToList(),
            actual.Stations.Keys.Order().ToList());
        foreach (var (station, left) in expected.Stations)
        {
            Equivalent(left.Figures, actual.Stations[station].Figures, $"{label} {station}");
            Assert.Equal(
                left.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)),
                actual.Stations[station].Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)));
        }
    }

    public static void Equivalent(StationTotals expected, StationTotals actual, string label)
    {
        Equivalent(expected.Figures, actual.Figures, label);
        Assert.Equal(
            expected.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)),
            actual.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)));
        Assert.Equal(expected.ScrapByReason.OrderBy(p => p.Key), actual.ScrapByReason.OrderBy(p => p.Key));
    }

    public static void Equivalent(ProductionFigures expected, ProductionFigures actual, string label)
    {
        Assert.True(expected.ScheduledTime == actual.ScheduledTime, $"{label}: Schichtzeit {expected.ScheduledTime} <> {actual.ScheduledTime}");
        Assert.True(expected.PlannedStopTime == actual.PlannedStopTime, $"{label}: geplanter Stopp");
        Assert.True(expected.AvailabilityLossTime == actual.AvailabilityLossTime, $"{label}: Verfügbarkeitsverlust");
        Assert.True(expected.MinorStopTime == actual.MinorStopTime, $"{label}: Kurzstopps");
        Assert.True(expected.GoodPieces == actual.GoodPieces, $"{label}: Gut {expected.GoodPieces} <> {actual.GoodPieces}");
        Assert.True(expected.ScrapPieces == actual.ScrapPieces, $"{label}: Ausschuss {expected.ScrapPieces} <> {actual.ScrapPieces}");
        Assert.True(expected.BreakdownCount == actual.BreakdownCount, $"{label}: Anzahl Störungen");
        Assert.True(expected.BreakdownTime == actual.BreakdownTime, $"{label}: Störungszeit");
        Assert.True(expected.MaintenanceTime == actual.MaintenanceTime, $"{label}: Wartungszeit");
        Assert.True(expected.OverproductionPieces == actual.OverproductionPieces, $"{label}: Überproduktion");
        Close(expected.IdealSeconds, actual.IdealSeconds, $"{label}: Soll-Sekunden");
        Close(expected.GoodIdealSeconds, actual.GoodIdealSeconds, $"{label}: Soll-Sekunden gut");
        Close(expected.PlannedQuantity, actual.PlannedQuantity, $"{label}: Planmenge");
        Close(expected.ScrapCostEuro, actual.ScrapCostEuro, $"{label}: Ausschusskosten");
    }

    private static void Close(decimal expected, decimal actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, $"{what}: {expected} <> {actual}");
}
