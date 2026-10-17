namespace OPCAgentSample.Kpi;

/// <summary>Eine Zeile des Stillstands-Paretos.</summary>
public sealed record ParetoEntry(string Reason, TimeSpan Duration, int Count, decimal CostEuro);

/// <summary>
/// Stillstand nach Grund (Pareto): ungeplante Stillstände inkl. Kurzstopps, ohne geplante Stopps, Taktverluste und
/// Überproduktion. Sortiert nach Dauer absteigend; Kosten = Dauer × Kostensatz der jeweiligen Station.
/// </summary>
public static class DowntimePareto
{
    public static IReadOnlyList<ParetoEntry> Build(KpiBlock block, IEnumerable<string> stationIds, CostRateTable rates)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(rates);
        var entries = new Dictionary<string, ParetoEntry>();
        foreach (var stationId in stationIds)
        {
            if (!block.Stations.TryGetValue(stationId, out var station)) continue;
            foreach (var (reason, figures) in station.Reasons)
            {
                var cost = rates.DowntimeCost(stationId, figures.Duration);
                entries[reason] = entries.TryGetValue(reason, out var sum)
                    ? sum with { Duration = sum.Duration + figures.Duration, Count = sum.Count + figures.Count, CostEuro = sum.CostEuro + cost }
                    : new ParetoEntry(reason, figures.Duration, figures.Count, cost);
            }
        }

        return [.. entries.Values.OrderByDescending(entry => entry.Duration).ThenBy(entry => entry.Reason, StringComparer.Ordinal)];
    }

    /// <summary>Stillstand eines Grunds je Station (z. B. „9 von 14 Störungen Gewebezuführung an RT-03“).</summary>
    public static IReadOnlyDictionary<string, ParetoEntry> ByStation(
        KpiBlock block, IEnumerable<string> stationIds, string reason, CostRateTable rates)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(rates);
        var result = new Dictionary<string, ParetoEntry>();
        foreach (var stationId in stationIds)
        {
            if (block.Stations.TryGetValue(stationId, out var station) && station.Reasons.TryGetValue(reason, out var figures))
            {
                result[stationId] = new ParetoEntry(reason, figures.Duration, figures.Count, rates.DowntimeCost(stationId, figures.Duration));
            }
        }

        return result;
    }
}
