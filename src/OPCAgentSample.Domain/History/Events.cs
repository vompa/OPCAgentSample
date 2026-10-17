namespace OPCAgentSample.Domain.History;

/// <summary>
/// Verlust- oder Stillstandsereignis (Spezifikation Abschnitt 6). Ein Ereignis wird bei Beginn mit offenem Ende
/// (<see cref="EndUtc"/> = <c>null</c>) und bei Ende erneut mit derselben <see cref="Id"/> gemeldet; Verbraucher ersetzen
/// den Stand je <see cref="Id"/>. <see cref="Reason"/> ist der Pareto-Grund (<see cref="LossReasons"/>),
/// <see cref="Cause"/> die konkrete Ursache.
/// </summary>
public sealed record LossEvent(
    string StationId,
    DateTimeOffset TimestampUtc,
    string Id,
    string Assembly,
    LossCategory Category,
    string Reason,
    string Cause,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    int AffectedPieces,
    bool Acknowledged,
    string? Note) : HistoryRecord(StationId, TimestampUtc)
{
    public bool IsOpen => EndUtc is null;

    /// <summary>Steht die Station während des Ereignisses (alle Kategorien außer Taktverlust und Überproduktion)?</summary>
    public bool IsStandstill => Category is not (LossCategory.SpeedLoss or LossCategory.Overproduction);
}

public enum AlarmSeverity
{
    Info,
    Low,
    Medium,
    High,
}

/// <summary>Art einer Meldung: Störung und Zustandswarnung sind Alarme, Prozesswarnung und Hinweis sind Meldungen.</summary>
public enum AlarmKind
{
    Fault,
    ConditionWarning,
    ProcessWarning,
    Notice,
}

/// <summary>
/// Alarm oder Meldung (Alarms &amp; Conditions). Wird bei Aktivierung, Quittierung und Ende mit derselben <see cref="Id"/>
/// gemeldet. <see cref="LossEventId"/> verknüpft eine Störungsmeldung mit ihrem Stillstandsereignis.
/// </summary>
public sealed record AlarmRecord(
    string StationId,
    DateTimeOffset TimestampUtc,
    string Id,
    AlarmKind Kind,
    AlarmSeverity Severity,
    string Assembly,
    string Text,
    DateTimeOffset ActiveSinceUtc,
    DateTimeOffset? AcknowledgedUtc,
    DateTimeOffset? ClearedUtc,
    string? LossEventId) : HistoryRecord(StationId, TimestampUtc)
{
    public bool IsAlarm => Kind is AlarmKind.Fault or AlarmKind.ConditionWarning;
}

public enum OrderEventKind
{
    Started,
    QuantityReached,
    Completed,
}

/// <summary>Auftragsereignis an einer Station; <see cref="Quantity"/> ist die bis dahin gefertigte Menge.</summary>
public sealed record OrderEvent(string StationId, DateTimeOffset TimestampUtc, string OrderId, OrderEventKind Kind, int Quantity)
    : HistoryRecord(StationId, TimestampUtc);
