using Opc.Ua;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;

namespace OPCAgentSample.Simulator;

// Alarms & Conditions: jeder Alarm des Generators ist eine AlarmConditionType-Instanz unter der Station. Nur lesend:
// Acknowledge, Confirm, AddComment, Enable und Disable werden mit BadUserAccessDenied abgewiesen.
internal sealed partial class PlantNodeManager
{
    private readonly Dictionary<string, AlarmConditionState> _conditions = [];

    /// <summary>
    /// Vergibt die NodeIds der automatisch angelegten Unterknoten einer Condition (ActiveState, Acknowledge, …) lesbar aus
    /// dem Pfad, z. B. <c>HalleA.Pressen.RT-04.Alarme.A-0941.ActiveState.Id</c>. Ohne diese Regel behielten sie die
    /// Deklarations-Ids des Basistyps (Namespace 0) und wären im Adressraum nicht auffindbar.
    /// </summary>
    public override NodeId New(ISystemContext context, NodeState node)
    {
        if (node is BaseInstanceState { Parent.NodeId.IdType: IdType.String } instance && !string.IsNullOrEmpty(instance.BrowseName?.Name))
            return new NodeId($"{instance.Parent.NodeId.Identifier}.{instance.BrowseName.Name}", NamespaceIndex);
        return base.New(context, node);
    }

    /// <summary>Kernaussage der Demo: Der Server steuert nichts. Jeder Methodenaufruf im Namespace wird abgewiesen.</summary>
    protected override ServiceResult Call(ISystemContext context, CallMethodRequest methodToCall, MethodState method, CallMethodResult result) =>
        StatusCodes.BadUserAccessDenied;

    protected override ValueTask<ServiceResult> CallAsync(
        ISystemContext context, CallMethodRequest methodToCall, MethodState method, CallMethodResult result, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ServiceResult>(StatusCodes.BadUserAccessDenied);

    private void CreateInitialConditions()
    {
        var active = _runtime.Feed.State.LatestAlarms.Where(a => a.ClearedUtc is null).OrderBy(a => a.ActiveSinceUtc).ToList();
        foreach (var alarm in active) ApplyAlarm(alarm, report: false);
    }

    /// <summary>Überträgt einen Alarmstand (aktiv, quittiert, beendet) auf die Condition und meldet das Ereignis.</summary>
    private void ApplyAlarm(AlarmRecord record, bool report)
    {
        if (!_stations.TryGetValue(record.StationId, out var station)) return;
        if (!_conditions.TryGetValue(record.Id, out var condition))
        {
            condition = CreateCondition(station, record);
            _conditions[record.Id] = condition;
        }

        var active = record.ClearedUtc is null;
        var time = record.TimestampUtc.UtcDateTime;
        condition.SetActiveState(SystemContext, active);
        condition.SetAcknowledgedState(SystemContext, record.AcknowledgedUtc is not null);
        condition.Retain.Value = active;
        condition.Time.Value = time;
        condition.ReceiveTime.Value = time;
        condition.EventId.Value = Guid.NewGuid().ToByteArray();
        condition.ClearChangeMasks(SystemContext, true);
        if (report) OnReportEvent(SystemContext, station.Object, condition);
    }

    private AlarmConditionState CreateCondition(StationNode station, AlarmRecord record)
    {
        var condition = new AlarmConditionState(station.AlarmFolder);
        var identifier = NodeIdScheme.Alarm(station.Station, record.Id);
        condition.Create(SystemContext, Id(identifier), BrowseNameOf(record.Id), new LocalizedText("de", record.Id), true);
        condition.SymbolicName = record.Id;
        condition.ReferenceTypeId = ReferenceTypes.Organizes;

        condition.SourceNode.Value = station.Object.NodeId;
        condition.SourceName.Value = $"{station.Station.Id} / {record.Assembly}";
        condition.EventType.Value = ObjectTypeIds.AlarmConditionType;
        condition.ConditionName.Value = record.Id;
        condition.ConditionClassId.Value = ConditionClass(record.Kind);
        condition.ConditionClassName.Value = new LocalizedText("en", record.Kind.ToString());
        condition.BranchId.Value = NodeId.Null;
        condition.Message.Value = new LocalizedText("de", record.Text);
        condition.Quality.Value = StatusCodes.Good;
        condition.InputNode.Value = station.Object.NodeId;
        condition.SetEnableState(SystemContext, true);
        condition.SetSeverity(SystemContext, (EventSeverity)SeverityOf(record.Severity));
        condition.AutoReportStateChanges = false;

        condition.OnAcknowledge = DenyAcknowledge;
        condition.OnAddComment = DenyComment;
        condition.OnEnableDisable = DenyEnableDisable;

        station.AlarmFolder.AddChild(condition);
        AddPredefinedNode(SystemContext, condition);
        return condition;
    }

    private static ServiceResult DenyAcknowledge(ISystemContext context, ConditionState condition, byte[] eventId, LocalizedText comment) =>
        StatusCodes.BadUserAccessDenied;

    private static ServiceResult DenyComment(ISystemContext context, ConditionState condition, byte[] eventId, LocalizedText comment) =>
        StatusCodes.BadUserAccessDenied;

    private static ServiceResult DenyEnableDisable(ISystemContext context, ConditionState condition, bool enabling) =>
        StatusCodes.BadUserAccessDenied;

    /// <summary>Severity nach OPC UA (1 bis 1000); Hinweise liegen unter 200, Störungen darüber.</summary>
    private static ushort SeverityOf(AlarmSeverity severity) => severity switch
    {
        AlarmSeverity.Info => 100,
        AlarmSeverity.Low => 300,
        AlarmSeverity.Medium => 500,
        AlarmSeverity.High => 800,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    private static NodeId ConditionClass(AlarmKind kind) => kind switch
    {
        AlarmKind.Fault => ObjectTypeIds.SystemConditionClassType,
        AlarmKind.ConditionWarning => ObjectTypeIds.MaintenanceConditionClassType,
        _ => ObjectTypeIds.ProcessConditionClassType,
    };
}
