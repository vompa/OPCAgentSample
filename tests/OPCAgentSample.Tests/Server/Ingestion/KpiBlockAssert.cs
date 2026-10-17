using OPCAgentSample.Kpi;

namespace OPCAgentSample.Tests.Server.Ingestion;

/// <summary>Vergleicht Kennzahlenblöcke Station für Station (Grundgrößen und Stillstand je Grund, ohne Datenstand).</summary>
internal static class KpiBlockAssert
{
    public static void Equal(KpiBlock expected, KpiBlock actual, IEnumerable<string>? onlyStations = null)
    {
        var stations = onlyStations?.ToList() ?? [.. expected.Stations.Keys.Union(actual.Stations.Keys).Order()];
        foreach (var station in stations)
        {
            Assert.True(expected.Stations.ContainsKey(station) == actual.Stations.ContainsKey(station), $"Station {station} fehlt auf einer Seite.");
            if (!expected.Stations.TryGetValue(station, out var left)) continue;

            var right = actual.Stations[station];
            Assert.True(left.Figures == right.Figures, $"{station}: Grundgrößen unterscheiden sich.\n  erwartet {left.Figures}\n  tatsächlich {right.Figures}");
            Assert.Equal(
                left.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)),
                right.Reasons.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Duration, p.Value.Count)));
        }
    }
}
