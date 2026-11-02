using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OPCAgentSample.Simulator.Observability;

/// <summary>Messpunkte des Simulators: <c>simulator.history.reads</c> (HistoryRead-Aufrufe) und <c>simulator.history.values</c> (gelieferte Werte).</summary>
public static class SimulatorTelemetry
{
    public const string Name = "OPCAgentSample.Simulator";

    public const string HistoryReads = "simulator.history.reads";
    public const string HistoryValues = "simulator.history.values";
    public const string HistoryReadSpan = "simulator.history_read";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name, "1.0.0");

    private static readonly Counter<long> Reads =
        Meter.CreateCounter<long>(HistoryReads, description: "OPC-UA-HistoryRead-Aufrufe (je Knoten)");

    private static readonly Counter<long> Values =
        Meter.CreateCounter<long>(HistoryValues, description: "Über HistoryRead gelieferte Werte");

    public static void HistoryRead(long values)
    {
        Reads.Add(1);
        Values.Add(values);
    }
}
