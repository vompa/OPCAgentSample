using OPCAgentSample.Domain.History;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Erzeugt die reproduzierbare Historie der Halle A als zeitlich sortierten Datenstrom. Jedes Fenster ist ein exakter
/// Ausschnitt des Gesamtlaufs: <c>Generate(a, c)</c> = <c>Generate(a, b)</c> + <c>Generate(b, c)</c>. Damit setzt der
/// Simulator ab der Demo-Uhr live fort („nächste Datensätze ab Zeitpunkt t“), inklusive der weiter wachsenden Drift.
/// Der Strom wird tageweise erzeugt; es liegen nie alle Zykluswerte gleichzeitig im Speicher.
/// </summary>
public sealed class HistoryGenerator
{
    private static readonly TimeSpan s_slice = TimeSpan.FromHours(6);

    private readonly ProductionShiftIndex _shifts;
    private readonly List<ProductionStationModel> _stations;
    private readonly List<OvenModel> _ovens;
    private readonly BufferModel _buffer;
    private readonly ClimateModel _climate;

    public HistoryGenerator(ScenarioDefinition scenario, int seed = HallAScenario.DefaultSeed)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Scenario = scenario;
        Seed = seed;
        _shifts = new ProductionShiftIndex(scenario.Calendar, scenario.HistoryStartUtc);
        _stations = [.. scenario.ProductionStations.Select((spec, index) =>
            new ProductionStationModel(scenario, new StationPlan(scenario, spec, index), _shifts, seed))];
        _ovens = [.. scenario.Ovens.Select(spec => new OvenModel(scenario, spec, _shifts, seed, PressLotsFor))];
        _buffer = new BufferModel(scenario);
        _climate = new ClimateModel(scenario, seed);
    }

    public ScenarioDefinition Scenario { get; }

    public int Seed { get; }

    /// <summary>Historie von Mo 05.10.2026 00:00 bis zur Demo-Uhr.</summary>
    public IEnumerable<HistoryRecord> GenerateUntilDemo() => Generate(Scenario.HistoryStartUtc, Scenario.DemoTimeUtc);

    /// <summary>Alle Datensätze mit Zeitstempel in [<paramref name="fromUtc"/>, <paramref name="untilUtc"/>), sortiert.</summary>
    public IEnumerable<HistoryRecord> Generate(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        for (var sliceStart = fromUtc; sliceStart < untilUtc; sliceStart += s_slice)
        {
            var sliceEnd = sliceStart + s_slice < untilUtc ? sliceStart + s_slice : untilUtc;
            foreach (var record in Slice(sliceStart, sliceEnd))
            {
                yield return record;
            }
        }
    }

    /// <summary>Nur die Taktwerte eines Rundtischs im Fenster (z. B. für den Live-Simulator).</summary>
    public IEnumerable<CycleRecord> Cycles(string stationId, DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        return StationModel(stationId).CyclesBetween(fromUtc, untilUtc);
    }

    internal ProductionStationModel StationModel(string stationId) =>
        _stations.FirstOrDefault(s => s.StationId == stationId)
        ?? throw new ArgumentException($"Unbekannte Produktionsstation '{stationId}'.", nameof(stationId));

    internal OvenModel OvenModel(string ovenId) =>
        _ovens.FirstOrDefault(o => o.Spec.StationId == ovenId)
        ?? throw new ArgumentException($"Unbekannter Ofen '{ovenId}'.", nameof(ovenId));

    internal BufferModel Buffer => _buffer;

    internal ClimateModel Climate => _climate;

    internal IReadOnlyList<ProductionStationModel> Stations => _stations;

    internal IReadOnlyList<OvenModel> Ovens => _ovens;

    internal ProductionShiftIndex Shifts => _shifts;

    private IEnumerable<HistoryRecord> Slice(DateTimeOffset fromUtc, DateTimeOffset untilUtc)
    {
        var records = new List<HistoryRecord>();
        foreach (var station in _stations) records.AddRange(station.Generate(fromUtc, untilUtc, OvenChargesFor));
        foreach (var oven in _ovens) records.AddRange(oven.Generate(fromUtc, untilUtc));
        records.AddRange(_buffer.Generate(fromUtc, untilUtc));
        records.AddRange(_climate.Generate(fromUtc, untilUtc));
        return records.OrderBy(r => r.TimestampUtc);
    }

    private IReadOnlyList<string> OvenChargesFor(int diameterMm, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        [.. _ovens.Where(o => Serves(o, diameterMm)).SelectMany(o => o.ChargesUnloadedBetween(fromUtc, toUtc))];

    private IReadOnlyList<string> PressLotsFor(int diameterMm, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        [.. _stations.Where(s => s.IsPress && PressServes(s, diameterMm, fromUtc)).SelectMany(s => s.LotIdsOverlapping(fromUtc, toUtc))];

    private bool PressServes(ProductionStationModel station, int ovenDiameterMm, DateTimeOffset instant)
    {
        var diameter = Scenario.Article(station.Plan.AssignmentAt(instant).ArticleId).DiameterMm;
        return Serves(ovenDiameterMm, diameter);
    }

    private static bool Serves(OvenModel oven, int diameterMm) => Serves(oven.Spec.DiameterMm, diameterMm);

    /// <summary>Zuordnungsregel: Ø 180 und größer in den Ofen für große Scheiben, alle übrigen in den anderen.</summary>
    private static bool Serves(int ovenDiameterMm, int diameterMm) => (ovenDiameterMm >= 180) == (diameterMm >= 180);
}
