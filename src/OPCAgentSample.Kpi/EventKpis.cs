using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Kpi;

/// <summary>Eintrag der Liste „Störungen und Alarme“ eines Zeitfensters.</summary>
public sealed record DisruptionEntry(
    string StationId, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string Text, TimeSpan StandstillInWindow, bool IsAlarmOnly);

/// <summary>Überproduktion (über Auftrag oder vor Bedarf) als eigene Kennzahl ohne OEE-Wirkung.</summary>
public sealed record OverproductionEntry(string StationId, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, int Pieces, string Cause);

public static class EventKpis
{
    /// <summary>
    /// Störungen und Alarme eines Fensters: Störungen und Kurzstopps, die das Fenster überlappen (Stillstand auf das
    /// Fenster und den Datenstand geschnitten), plus Alarme (Störung, Zustandswarnung) ohne eigenes Stillstandsereignis.
    /// Prozesswarnungen und Hinweise sind Meldungen und zählen nicht.
    /// </summary>
    public static IReadOnlyList<DisruptionEntry> Disruptions(
        IEnumerable<LossEvent> events, IEnumerable<AlarmRecord> alarms, TimeWindow window, DateTimeOffset nowUtc)
    {
        var visible = window.Intersect(window.StartUtc, nowUtc);
        var result = new List<DisruptionEntry>();
        foreach (var loss in Latest(events, e => e.Id))
        {
            if (loss.Category is not (LossCategory.Breakdown or LossCategory.MinorStop)) continue;
            var overlap = new TimeWindow(loss.StartUtc, loss.EndUtc ?? nowUtc).Intersect(visible);
            if (overlap.IsEmpty) continue;
            result.Add(new DisruptionEntry(loss.StationId, loss.StartUtc, loss.EndUtc, loss.Cause, overlap.Duration, IsAlarmOnly: false));
        }

        foreach (var alarm in Latest(alarms, a => a.Id))
        {
            if (!alarm.IsAlarm || alarm.LossEventId is not null) continue;
            var active = new TimeWindow(alarm.ActiveSinceUtc, alarm.ClearedUtc ?? nowUtc);
            if (active.Intersect(visible).IsEmpty && !visible.Contains(alarm.ActiveSinceUtc)) continue;
            result.Add(new DisruptionEntry(alarm.StationId, alarm.ActiveSinceUtc, alarm.ClearedUtc, alarm.Text, TimeSpan.Zero, IsAlarmOnly: true));
        }

        return [.. result.OrderBy(entry => entry.StartUtc)];
    }

    /// <summary>Überproduktionsereignisse, die im Fenster beginnen.</summary>
    public static IReadOnlyList<OverproductionEntry> Overproduction(IEnumerable<LossEvent> events, TimeWindow window) =>
        [.. Latest(events, e => e.Id)
            .Where(e => e.Category == LossCategory.Overproduction && window.Contains(e.StartUtc))
            .OrderBy(e => e.StartUtc)
            .Select(e => new OverproductionEntry(e.StationId, e.StartUtc, e.EndUtc, e.AffectedPieces, e.Cause))];

    /// <summary>Neuester Stand je Id (Ereignisse und Alarme werden mehrfach gemeldet).</summary>
    public static IEnumerable<T> Latest<T>(IEnumerable<T> records, Func<T, string> id)
        where T : HistoryRecord
    {
        var latest = new Dictionary<string, T>();
        foreach (var record in records)
        {
            var key = id(record);
            if (!latest.TryGetValue(key, out var known) || record.TimestampUtc >= known.TimestampUtc) latest[key] = record;
        }

        return latest.Values;
    }
}
