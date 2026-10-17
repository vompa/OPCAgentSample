using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Script;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Meldet Alarme in ihren Ständen (aktiv, quittiert, beendet) mit derselben Id.</summary>
internal static class AlarmVersions
{
    private static readonly TimeSpan s_acknowledgeDelay = TimeSpan.FromMinutes(2);

    /// <summary>Störungsalarm zu einem Stillstandsereignis der Kategorie Störung.</summary>
    public static void AddFaultAlarm(PlannedLoss loss, TimeWindow window, List<HistoryRecord> records)
    {
        var id = $"AL-{loss.Id}";
        var acknowledged = loss.StartUtc + s_acknowledgeDelay;
        AlarmRecord Version(DateTimeOffset at, DateTimeOffset? ack, DateTimeOffset? cleared) =>
            new(loss.StationId, at, id, AlarmKind.Fault, AlarmSeverity.High, loss.Assembly, loss.Cause, loss.StartUtc, ack, cleared, loss.Id);

        if (window.Contains(loss.StartUtc)) records.Add(Version(loss.StartUtc, null, null));
        if (acknowledged < loss.EndUtc && window.Contains(acknowledged)) records.Add(Version(acknowledged, acknowledged, null));
        if (window.Contains(loss.EndUtc)) records.Add(Version(loss.EndUtc, acknowledged < loss.EndUtc ? acknowledged : loss.EndUtc, loss.EndUtc));
    }

    public static void AddScripted(ScriptedAlarm alarm, TimeWindow window, List<HistoryRecord> records)
    {
        AlarmRecord Version(DateTimeOffset at, DateTimeOffset? cleared) =>
            new(alarm.StationId, at, alarm.Id, alarm.Kind, alarm.Severity, alarm.Assembly, alarm.Text, alarm.ActiveUtc, null, cleared, null);

        if (alarm.ClearedUtc == alarm.ActiveUtc)
        {
            if (window.Contains(alarm.ActiveUtc)) records.Add(Version(alarm.ActiveUtc, alarm.ClearedUtc));
            return;
        }

        if (window.Contains(alarm.ActiveUtc)) records.Add(Version(alarm.ActiveUtc, null));
        if (alarm.ClearedUtc is { } cleared && window.Contains(cleared)) records.Add(Version(cleared, cleared));
    }
}
