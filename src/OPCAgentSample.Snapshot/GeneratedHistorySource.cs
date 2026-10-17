using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Snapshot;

/// <summary>
/// Historie des Szenarios als Quelle der Aufnahme: Roh-Datensätze des Generators bis zur Demo-Uhr, die Tageskennzahlen der
/// Zeit davor (Historie B) und die Zähler der Hallen B und C – in zeitlicher Reihenfolge, wie sie der Collector per HistoryRead lieferte.
/// Die Tests und das Werkzeug zur Momentaufnahme nutzen dieselbe Quelle und damit dieselben Daten.
/// </summary>
public sealed class GeneratedHistorySource : IHistorySource
{
    public const string Id = "halle-a";

    private readonly IReadOnlyList<HistoryRecord> _records;

    /// <param name="scenario">Das Szenario.</param>
    /// <param name="generator">Generator des Szenarios.</param>
    /// <param name="raw">Roh-Datensätze bis zur Demo-Uhr (<see cref="HistoryGenerator.GenerateUntilDemo"/>).</param>
    /// <param name="startUtc">Frühester Zeitpunkt der Quelle; Standard: Beginn der Tageskennzahlen (Historie B).</param>
    public GeneratedHistorySource(
        ScenarioDefinition scenario, HistoryGenerator generator, IReadOnlyList<HistoryRecord> raw, DateTimeOffset? startUtc = null)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _records = Combine(scenario, generator, raw);
        HistoryStartUtc = startUtc ?? scenario.Calendar.DayPeriod(scenario.DailyHistoryStart).StartUtc;
    }

    /// <summary>Die komplette Historie des Szenarios bis zur Demo-Uhr (erzeugt sie, rund 1,5 Mio. Datensätze).</summary>
    public static GeneratedHistorySource CreateDefault()
    {
        var scenario = HallAScenario.Create();
        var generator = new HistoryGenerator(scenario);
        return new GeneratedHistorySource(scenario, generator, [.. generator.GenerateUntilDemo()]);
    }

    public string SourceId => Id;

    public DateTimeOffset HistoryStartUtc { get; }

    /// <summary>Roh-Datensätze, Tageskennzahlen vor dem ersten Rohtag und Zähler der Hallen B/C, nach Zeit sortiert.</summary>
    public static IReadOnlyList<HistoryRecord> Combine(ScenarioDefinition scenario, HistoryGenerator generator, IReadOnlyList<HistoryRecord> raw)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(raw);
        var daily = DailyKpiHistory.Build(generator);
        var firstRawDay = scenario.Calendar.ProductionDayOf(scenario.HistoryStartUtc + TimeSpan.FromHours(12));
        var synthetic = daily.Where(d => d.Day < firstRawDay);
        return [.. raw.Concat(synthetic).Concat(CounterHalls.Days(scenario.Calendar)).OrderBy(r => r.TimestampUtc)];
    }

    public IAsyncEnumerable<HistoryRecord> ReadAsync(
        DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken = default) =>
        Window(fromUtc, untilUtc).ToAsyncEnumerable();

    private IEnumerable<HistoryRecord> Window(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        for (var i = LowerBound(fromUtc); i < _records.Count && _records[i].TimestampUtc < untilUtc; i++)
        {
            yield return _records[i];
        }
    }

    /// <summary>Index des ersten Datensatzes mit Zeitstempel ab <paramref name="fromUtc"/> (die Liste ist sortiert).</summary>
    private int LowerBound(DateTimeOffset fromUtc)
    {
        int low = 0, high = _records.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_records[middle].TimestampUtc < fromUtc) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}
