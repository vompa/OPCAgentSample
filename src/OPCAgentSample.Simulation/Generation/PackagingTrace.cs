using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Paletten der Verpackung mit Verweis auf die Ofenchargen (letzte Stufe der Rückverfolgung). Versand täglich
/// 14:00 für Paletten, die bis 12:00 gepackt sind; spätere Paletten gehen am nächsten Produktionstag.
/// </summary>
internal static class PackagingTrace
{
    private const int SmallDiscCartonsPerPallet = 120;
    private const int LargeDiscCartonsPerPallet = 48;
    private const int LargeDiscDiameterMm = 180;
    private static readonly TimeSpan s_ovenLookback = TimeSpan.FromMinutes(90);
    private static readonly TimeOnly s_packingCutoff = new(12, 0);
    private static readonly TimeOnly s_shippingTime = new(14, 0);

    public static IEnumerable<PalletRecord> Pallets(
        ScenarioDefinition scenario, ProductionStationModel station, ShiftRun run, OvenLookup ovens, TimeWindow window)
    {
        foreach (var pallet in station.PalletsOf(run))
        {
            var packedInWindow = window.Contains(pallet.PackedUtc);
            var shippedInWindow = window.Contains(pallet.ShippedUtc);
            if (!packedInWindow && !shippedInWindow) continue;

            var article = scenario.PackagingArticle(pallet.ArticleId);
            var charges = ovens(article.DiameterMm, pallet.PackedUtc - s_ovenLookback, pallet.PackedUtc);
            if (packedInWindow)
            {
                yield return new PalletRecord(station.StationId, pallet.PackedUtc, pallet.PalletId, article.Id, pallet.Cartons,
                    article.PiecesPerCarton, charges, null);
            }

            if (shippedInWindow)
            {
                yield return new PalletRecord(station.StationId, pallet.ShippedUtc, pallet.PalletId, article.Id, pallet.Cartons,
                    article.PiecesPerCarton, charges, pallet.ShippedUtc);
            }
        }
    }

    /// <summary>Alle Paletten einer Schicht mit Packzeitpunkt und Versandzeitpunkt (ohne Bezug zu einem Ausgabefenster).</summary>
    internal static List<PackedPallet> PackPallets(ScenarioDefinition scenario, ProductionStationModel station, ShiftRun run)
    {
        var pallets = new List<PackedPallet>();
        var cartons = 0;
        var number = 0;
        for (var i = 0; i < run.CycleTicks.Length; i++)
        {
            if (run.Scrap[i]) continue;
            var instant = new DateTimeOffset(run.CycleTicks[i], TimeSpan.Zero);
            var article = scenario.PackagingArticle(station.Plan.AssignmentAt(instant).ArticleId);
            var size = article.DiameterMm >= LargeDiscDiameterMm ? LargeDiscCartonsPerPallet : SmallDiscCartonsPerPallet;
            if (++cartons < size) continue;

            cartons = 0;
            var palletId = $"P-{station.StationId}-{run.Shift.Key}-{++number:00}";
            pallets.Add(new PackedPallet(palletId, article.Id, size, instant, ShippingTime(scenario, instant)));
        }

        return pallets;
    }

    private static DateTimeOffset ShippingTime(ScenarioDefinition scenario, DateTimeOffset packedUtc)
    {
        var calendar = scenario.Calendar;
        var local = calendar.ToLocal(packedUtc);
        var day = DateOnly.FromDateTime(local.DateTime);
        if (TimeOnly.FromDateTime(local.DateTime) >= s_packingCutoff) day = day.AddDays(1);
        while (!calendar.IsProductionDay(day)) day = day.AddDays(1);
        return calendar.ToUtc(day.ToDateTime(s_shippingTime));
    }
}

/// <summary>Palette der Verpackung: Packzeitpunkt (Takt, der sie füllt) und geplanter Versand.</summary>
internal sealed record PackedPallet(string PalletId, string ArticleId, int Cartons, DateTimeOffset PackedUtc, DateTimeOffset ShippedUtc);
