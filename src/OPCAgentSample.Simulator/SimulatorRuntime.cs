using OPCAgentSample.Domain;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulator.History;
using OPCAgentSample.Simulator.Live;

namespace OPCAgentSample.Simulator;

/// <summary>Gemeinsamer Zustand von Knotenverwaltung und Aktualisierungsschleife: Generator, Uhr, Live-Strom, Historie.</summary>
internal sealed class SimulatorRuntime
{
    private SimulatorRuntime(
        SimulatorOptions options, HistoryGenerator generator, SimulationClock clock, LiveFeed feed, HistoryService history)
    {
        Options = options;
        Generator = generator;
        Clock = clock;
        Feed = feed;
        History = history;
    }

    public SimulatorOptions Options { get; }

    public HistoryGenerator Generator { get; }

    public SimulationClock Clock { get; }

    public LiveFeed Feed { get; }

    public HistoryService History { get; }

    public ScenarioDefinition Scenario => Generator.Scenario;

    /// <summary>
    /// Erzeugt die Laufzeit und spielt die Historie bis zur Startzeit ein (ca. 2 s). Die Uhr steht dabei auf der Startzeit
    /// und läuft erst mit <see cref="StartClock"/> an, damit die Startzeit exakt der konfigurierten entspricht.
    /// </summary>
    public static SimulatorRuntime Create(SimulatorOptions options)
    {
        var scenario = HallAScenario.Create();
        var generator = new HistoryGenerator(scenario, options.Seed);
        var start = options.ResolveStart(scenario.Calendar, scenario.HistoryStartUtc);
        var clock = new SimulationClock(start, speed: 0);
        var feed = new LiveFeed(generator, clock);
        feed.Advance(start);
        feed.State.RecordAlarmChanges = true;
        return new SimulatorRuntime(options, generator, clock, feed, new HistoryService(generator, clock));
    }

    /// <summary>Lässt die Simulationsuhr mit der konfigurierten Geschwindigkeit laufen (ab ihrer aktuellen Zeit).</summary>
    public void StartClock() => Clock.SetSpeed(Options.Speed);
}
