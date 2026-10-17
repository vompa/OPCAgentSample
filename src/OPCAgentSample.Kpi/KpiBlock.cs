using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>Grundgrößen und Stillstand je Pareto-Grund einer Station in einem Kennzahlenblock.</summary>
public sealed record StationBlock(ProductionFigures Figures, IReadOnlyDictionary<string, ReasonFigures> Reasons)
{
    public static StationBlock Empty { get; } = new(ProductionFigures.Empty, new Dictionary<string, ReasonFigures>());

    public StationBlock Add(StationBlock other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Figures.Add(other.Figures), ReasonMath.Merge([Reasons, other.Reasons]));
    }
}

/// <summary>
/// Kennzahlenblock eines logischen Zeitbereichs (Schicht, Tag, Woche) mit Datenstand <see cref="SourceUntilUtc"/>.
/// Enthält die Summen je Station; jede Sicht (Station, Bereich, Halle) entsteht durch Addition, nie durch Mitteln.
/// </summary>
public sealed class KpiBlock
{
    public KpiBlock(string key, DateTimeOffset sourceUntilUtc, IReadOnlyDictionary<string, StationBlock> stations)
    {
        Key = key;
        SourceUntilUtc = sourceUntilUtc;
        Stations = stations;
    }

    public string Key { get; }

    /// <summary>Wasserstand: bis zu diesem Zeitpunkt sind die Daten berücksichtigt („Datenstand“).</summary>
    public DateTimeOffset SourceUntilUtc { get; }

    public IReadOnlyDictionary<string, StationBlock> Stations { get; }

    public ProductionFigures Total => Figures(Stations.Keys);

    public ProductionFigures Figures(string stationId) =>
        Stations.TryGetValue(stationId, out var block) ? block.Figures : ProductionFigures.Empty;

    public ProductionFigures Figures(IEnumerable<string> stationIds) =>
        ProductionFigures.Sum(stationIds.Select(Figures));

    public IReadOnlyDictionary<string, ReasonFigures> Reasons(IEnumerable<string> stationIds) =>
        ReasonMath.Merge(stationIds.Where(Stations.ContainsKey).Select(id => Stations[id].Reasons));

    /// <summary>Addiert Blöcke (z. B. Schichten zu Tag oder Woche, Halle A mit Vergleichsdaten B/C).</summary>
    public static KpiBlock Combine(string key, IEnumerable<KpiBlock> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var stations = new Dictionary<string, StationBlock>();
        var sourceUntil = DateTimeOffset.MinValue;
        foreach (var part in parts)
        {
            sourceUntil = part.SourceUntilUtc > sourceUntil ? part.SourceUntilUtc : sourceUntil;
            foreach (var (stationId, block) in part.Stations)
            {
                stations[stationId] = stations.TryGetValue(stationId, out var sum) ? sum.Add(block) : block;
            }
        }

        return new KpiBlock(key, sourceUntil, stations);
    }
}

internal static class ReasonMath
{
    public static IReadOnlyDictionary<string, ReasonFigures> Merge(IEnumerable<IReadOnlyDictionary<string, ReasonFigures>> parts)
    {
        var merged = new Dictionary<string, ReasonFigures>();
        foreach (var part in parts)
        {
            foreach (var (reason, figures) in part)
            {
                merged[reason] = merged.TryGetValue(reason, out var sum) ? sum.Add(figures) : figures;
            }
        }

        return merged;
    }
}
