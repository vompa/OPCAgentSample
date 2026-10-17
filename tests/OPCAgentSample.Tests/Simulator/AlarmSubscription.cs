using System.Collections.Concurrent;
using Opc.Ua;
using Opc.Ua.Client;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>Ein empfangenes Alarm-Ereignis (Felder der Select-Klauseln von <see cref="AlarmSubscription.CreateFilter"/>).</summary>
internal sealed record AlarmEvent(
    string ConditionName,
    string SourceName,
    NodeId SourceNode,
    ushort Severity,
    bool Active,
    bool Acknowledged,
    bool Retain,
    string ClassName,
    string Message,
    byte[] EventId,
    DateTime Time);

/// <summary>Sammelt Alarm-Ereignisse einer Event-Subscription für die Tests.</summary>
internal sealed class AlarmSubscription
{
    private readonly ConcurrentQueue<AlarmEvent> _events = new();
    private Subscription? _subscription;

    public IReadOnlyList<AlarmEvent> Events => [.. _events];

    public static EventFilter CreateFilter()
    {
        var filter = new EventFilter();
        filter.AddSelectClause(ObjectTypeIds.ConditionType, BrowseNames.ConditionName);
        filter.AddSelectClause(ObjectTypeIds.BaseEventType, BrowseNames.SourceName);
        filter.AddSelectClause(ObjectTypeIds.BaseEventType, BrowseNames.SourceNode);
        filter.AddSelectClause(ObjectTypeIds.BaseEventType, BrowseNames.Severity);
        filter.SelectClauses.Add(Clause(ObjectTypeIds.AlarmConditionType, BrowseNames.ActiveState, BrowseNames.Id));
        filter.SelectClauses.Add(Clause(ObjectTypeIds.AcknowledgeableConditionType, BrowseNames.AckedState, BrowseNames.Id));
        filter.AddSelectClause(ObjectTypeIds.ConditionType, BrowseNames.Retain);
        filter.AddSelectClause(ObjectTypeIds.ConditionType, BrowseNames.ConditionClassName);
        filter.AddSelectClause(ObjectTypeIds.BaseEventType, BrowseNames.Message);
        filter.AddSelectClause(ObjectTypeIds.BaseEventType, BrowseNames.EventId);
        filter.AddSelectClause(ObjectTypeIds.BaseEventType, BrowseNames.Time);
        return filter;
    }

    public void Attach(Subscription subscription, ISession session) => _subscription = subscription;

    /// <summary>Fordert die aktiven Alarme erneut an (ConditionRefresh).</summary>
    public Task RefreshAsync(CancellationToken ct = default) =>
        _subscription is null ? Task.CompletedTask : _subscription.ConditionRefreshAsync(ct);

    public void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs args)
    {
        if (args.NotificationValue is not EventFieldList fields || fields.EventFields.Count < 11) return;
        var f = fields.EventFields;
        if (f[0].Value is not string name) return; // Refresh-Start/-Ende und fremde Ereignisse haben keinen Condition-Namen
        _events.Enqueue(new AlarmEvent(
            name,
            ((string?)f[1].Value) ?? string.Empty,
            (NodeId)f[2].Value,
            (ushort)f[3].Value,
            f[4].Value is true,
            f[5].Value is true,
            f[6].Value is true,
            ((LocalizedText?)f[7].Value)?.Text ?? string.Empty,
            ((LocalizedText?)f[8].Value)?.Text ?? string.Empty,
            (byte[])f[9].Value,
            (DateTime)f[10].Value));
    }

    private static SimpleAttributeOperand Clause(NodeId typeId, params string[] path) => new()
    {
        TypeDefinitionId = typeId,
        BrowsePath = [.. path.Select(p => new QualifiedName(p))],
        AttributeId = Attributes.Value,
    };
}
