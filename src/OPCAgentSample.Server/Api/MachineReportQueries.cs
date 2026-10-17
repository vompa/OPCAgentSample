using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Rollup;

namespace OPCAgentSample.Server.Api;

/// <summary>Ein Abschnitt eines Prozesswerts: Mittelwert, Anzahl der Werte außerhalb des Toleranzbands und Anzahl aller Werte.</summary>
public sealed record BandBucket(DateTimeOffset StartUtc, double Mean, long Outside, long Count);

/// <summary>Ein Abschnitt der Zählwerte einer Verpackung: Kartons, Anzahl der Zählminuten.</summary>
public sealed record UnitBucket(DateTimeOffset StartUtc, long Pieces, long Minutes);

/// <summary>Prozesswert der Rundtische, der gegen ein Toleranzband bewertet wird (feste Liste, keine Eingabe im Schlüssel).</summary>
public enum PressValue
{
    /// <summary>Abweichung der Taktzeit vom Soll-Takt des Artikels in Prozent.</summary>
    CycleDeviationPercent,

    /// <summary>Presskraftaufbau in Sekunden.</summary>
    BuildUpSeconds,
}

/// <summary>
/// Lesende Abfragen des Maschinen-Reports für Prozesswerte mit Toleranzband (Taktzeit, Presskraftaufbau, Ofentemperatur, Kartons) aus
/// der Prozessstatistik. Die Klassen des Histogramms liegen auf den Toleranzgrenzen (<see cref="ProcessBands"/>: Takt ±5 %,
/// Presskraftaufbau 0,6 bis 1,0 s, Ofen 175 bis 185 °C), damit „außerhalb des Toleranzbands“ ohne Rohwerte stimmt. Die Kennzahlen
/// selbst (OEE, Pareto, Kosten, Zuverlässigkeit) kommen nicht von hier, sondern aus der Kennzahlen-Bibliothek.
/// </summary>
public sealed class MachineReportQueries(RollupReader reader, ShiftCalendar calendar)
{
    /// <summary>Verlauf eines Prozesswerts der Rundtische in gleich langen Abschnitten samt Zahl der Takte außerhalb des Toleranzbands.</summary>
    public Task<IReadOnlyList<BandBucket>> PressBucketsAsync(
        string stationId, PressValue value, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin) =>
        BandAsync(
            stationId, value switch
            {
                PressValue.CycleDeviationPercent => SeriesNames.CycleDeviation,
                PressValue.BuildUpSeconds => SeriesNames.BuildUp,
                _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
            },
            from, to, bucket, origin);

    /// <summary>Verlauf der Ofentemperatur (Zone 2) während der Haltephase (ab 120 °C) mit Werten außerhalb des Toleranzbands.</summary>
    public Task<IReadOnlyList<BandBucket>> OvenBucketsAsync(
        string stationId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin) =>
        BandAsync(stationId, SeriesNames.OvenHold, from, to, bucket, origin);

    /// <summary>Gezählte Kartons einer Verpackung in gleich langen Abschnitten (Gut und Ausschuss).</summary>
    public async Task<IReadOnlyList<UnitBucket>> UnitBucketsAsync(
        string stationId, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin)
    {
        var buckets = await BucketsAsync(stationId, SeriesNames.Cartons, from, to, bucket, origin);
        return [.. buckets.Where(b => b.Stat.Count > 0).Select(b => new UnitBucket(b.StartUtc, (long)Math.Round(b.Stat.Sum), b.Stat.Count))];
    }

    private async Task<IReadOnlyList<BandBucket>> BandAsync(
        string stationId, string series, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin)
    {
        var spec = SeriesCatalog.For(stationId, series).Spec!;
        var buckets = await BucketsAsync(stationId, series, from, to, bucket, origin);
        return [.. buckets.Where(b => b.Stat.Count > 0).Select(b => new BandBucket(b.StartUtc, b.Stat.Mean, spec.Outside(b.Stat.Hist), b.Stat.Count))];
    }

    private async Task<IReadOnlyList<StatBucket>> BucketsAsync(
        string stationId, string series, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin)
    {
        var latest = await reader.ReadOverallWatermarkAsync() ?? to;
        return await reader.ReadBucketsAsync(stationId, series, from, to, bucket, origin, latest, calendar);
    }
}
