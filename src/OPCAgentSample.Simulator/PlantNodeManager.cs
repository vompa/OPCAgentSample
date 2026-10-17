using Opc.Ua;
using Opc.Ua.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Simulator.History;
using OPCAgentSample.Simulator.Live;
using OPCAgentSample.Simulator.Model;

namespace OPCAgentSample.Simulator;

/// <summary>
/// Adressraum der simulierten Halle (Namespace <see cref="NodeIdScheme.NamespaceUri"/>): Typen, Werk/Hallen/Stationen,
/// Live-Werte, HistoryRead (Raw) und Alarms &amp; Conditions. Der Adressraum ist rein lesend: keine schreibbaren Variablen,
/// keine Steuer-Methoden, die Methoden der Conditions werden abgewiesen.
/// </summary>
internal sealed partial class PlantNodeManager : CustomNodeManager2
{
    private readonly SimulatorRuntime _runtime;
    private readonly Dictionary<string, VariableEntry> _variables = [];
    private readonly Dictionary<string, HistoryEntry> _history = [];
    private readonly Dictionary<string, StationNode> _stations = [];

    public PlantNodeManager(IServerInternal server, ApplicationConfiguration configuration, SimulatorRuntime runtime)
        : base(server, configuration, NodeIdScheme.NamespaceUri)
    {
        _runtime = runtime;
        SystemContext.NodeIdFactory = this;
    }

    /// <summary>Index des Anlagen-Namespaces im Server (soll <see cref="NodeIdScheme.ExpectedNamespaceIndex"/> sein).</summary>
    public ushort PlantNamespaceIndex => NamespaceIndexes[0];

    public override void CreateAddressSpace(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        lock (Lock)
        {
            BuildEnumerations(externalReferences);
            BuildTypes(externalReferences);
            BuildPlant(externalReferences);
            ApplyValues(_runtime.Feed.Compute(_runtime.Clock.UtcNow));
            CreateInitialConditions();
        }
    }

    /// <summary>Übernimmt die Live-Werte und Alarmänderungen eines Aktualisierungsschritts.</summary>
    public void Refresh(IReadOnlyDictionary<string, LiveValue> values, IReadOnlyList<Domain.History.AlarmRecord> alarmChanges)
    {
        lock (Lock)
        {
            ApplyValues(values);
            foreach (var change in alarmChanges) ApplyAlarm(change, report: true);
        }
    }

    /// <summary>Setzt nur geänderte Werte; der Zeitstempel ist die Simulationszeit des Wertes.</summary>
    private void ApplyValues(IReadOnlyDictionary<string, LiveValue> values)
    {
        foreach (var (identifier, live) in values)
        {
            if (!_variables.TryGetValue(identifier, out var entry)) continue;

            var value = ValueMapper.ToNodeValue(entry.Kind, live.Value);
            var status = value is null ? StatusCodes.BadNoData : StatusCodes.Good;
            var variable = entry.Variable;
            if (variable.StatusCode == status && Equals(variable.Value, value)) continue;

            variable.Value = value;
            variable.StatusCode = status;
            variable.Timestamp = live.Timestamp.UtcDateTime;
            variable.ClearChangeMasks(SystemContext, false);
        }
    }

    private sealed record VariableEntry(BaseDataVariableState Variable, ValueKind Kind);

    private sealed record HistoryEntry(HistoryBinding Binding, string SourceId, ValueKind Kind);

    private sealed record StationNode(Station Station, BaseObjectState Object, FolderState AlarmFolder);
}
