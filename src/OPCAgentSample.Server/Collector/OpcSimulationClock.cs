using OPCAgentSample.Domain;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Collector;

/// <summary>
/// Simulationsuhr des Servers aus dem OPC-Knoten <c>Werk.Simulation.Zeit</c>. Der Server hat in Produktion keine eigene Uhr:
/// <see cref="UtcNow"/> ist die zuletzt gelesene Simulationszeit, zwischen zwei Lesungen mit der Geschwindigkeit
/// (<c>Werk.Simulation.Geschwindigkeit</c>) fortgeschrieben. Ohne Verbindung steht sie auf dem letzten bekannten Stand.
/// </summary>
/// <remarks>
/// Die Uhr geht nie vor der Uhr des OPC-Servers: Der Zeitbezug einer Lesung ist der Empfang der Antwort (der Server hat
/// den Wert früher abgetastet), und der Knoten selbst hinkt dem Takt der Aktualisierungsschleife des Servers hinterher.
/// Ein Aufnahmefenster bis <see cref="UtcNow"/> liegt damit vollständig in der Historie, die der Server ausliefert – sonst
/// würde der Wasserstand über Datensätze hinwegspringen, die der Server noch gar nicht kannte.
/// </remarks>
public sealed class OpcSimulationClock(TimeProvider timeProvider) : IDemoClock
{
    /// <summary>Eine neuere Lesung, die höchstens so weit zurückliegt, gilt als veralteter Wert und wird ignoriert (Uhr bleibt monoton).</summary>
    private static readonly TimeSpan s_staleToleranceReal = TimeSpan.FromSeconds(10);

    private Anchor? _anchor;

    /// <summary>Wurde die Simulationszeit schon einmal gelesen?</summary>
    public bool HasReading => _anchor is not null;

    /// <summary>Vor der ersten Lesung die Demo-Uhr (Do 15.10.2026, 09:42), danach die Simulationszeit.</summary>
    public DateTimeOffset UtcNow => Evaluate(Volatile.Read(ref _anchor));

    /// <summary>Geschwindigkeit der Simulation (1 = Echtzeit, 0 = angehalten).</summary>
    public double Speed => Volatile.Read(ref _anchor)?.Speed ?? 0;

    /// <summary>Übernimmt eine Lesung. Eine Lesung, die nur knapp hinter der fortgeschriebenen Uhr liegt, ist ein veralteter Wert.</summary>
    public void Update(DateTimeOffset simulationUtc, double speed)
    {
        var current = Volatile.Read(ref _anchor);
        var now = timeProvider.GetTimestamp();
        if (current is { Frozen: false } && simulationUtc < Evaluate(current))
        {
            var behind = Evaluate(current) - simulationUtc;
            var tolerance = (Math.Abs(current.Speed) * s_staleToleranceReal.TotalSeconds * TimeSpan.TicksPerSecond) + (5 * TimeSpan.TicksPerSecond);
            if (behind.Ticks <= tolerance)
            {
                // Nur die Geschwindigkeit nachführen; der Zeitpunkt bleibt auf dem bisherigen, weiter fortgeschriebenen Stand.
                Volatile.Write(ref _anchor, new Anchor(Evaluate(current), now, speed, Frozen: false));
                return;
            }
        }

        Volatile.Write(ref _anchor, new Anchor(simulationUtc, now, speed, Frozen: false));
    }

    /// <summary>Hält die Uhr auf dem jetzigen Stand an (Verbindung verloren).</summary>
    public void Freeze()
    {
        var current = Volatile.Read(ref _anchor);
        if (current is null or { Frozen: true }) return;
        Volatile.Write(ref _anchor, new Anchor(Evaluate(current), timeProvider.GetTimestamp(), current.Speed, Frozen: true));
    }

    private DateTimeOffset Evaluate(Anchor? anchor)
    {
        if (anchor is null) return SimulationClock.DemoStart;
        if (anchor.Frozen || anchor.Speed == 0) return anchor.Simulation;
        var real = timeProvider.GetElapsedTime(anchor.Timestamp);
        return anchor.Simulation + (real * anchor.Speed);
    }

    private sealed record Anchor(DateTimeOffset Simulation, long Timestamp, double Speed, bool Frozen);
}
