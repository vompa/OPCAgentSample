using OPCAgentSample.Domain;
using OPCAgentSample.Server.Kpis;

namespace OPCAgentSample.Server.Live;

/// <summary>
/// Live-Pfad der Aufnahme (Spezifikation Abschnitt 15, Datenfluss): Die Verdichtung (<c>RollupEngine</c>) schreibt Zähler,
/// Statistik und Ereignisse mit jedem Fenster. Danach bildet dieser Pfad die abgeleiteten Sichten neu – Stationszustände
/// <c>st:{id}</c>, den Meldungsstrom <c>ev:A:{prodtag}</c> und die aktiven Alarme – und meldet die Änderung an die Oberfläche.
/// </summary>
public sealed class LivePath(
    KpiBlockService blocks,
    StationStateStore states,
    EventFeed feed,
    ShiftCalendar calendar,
    ILiveNotifier? notifier = null)
{
    /// <summary>Bildet Stationszustände, Meldungen und aktive Alarme zum Datenstand neu und meldet sie.</summary>
    public async Task CompleteAsync(DateTimeOffset untilUtc, CancellationToken cancellationToken)
    {
        var shift = (await blocks.GetAsync(calendar.ShiftPeriodOf(untilUtc), cancellationToken)).Block;
        await states.RefreshAsync(untilUtc, shift, cancellationToken);
        await feed.SyncAsync(untilUtc, cancellationToken);
        await (notifier ?? NullLiveNotifier.Instance).NotifyAsync(
            LiveChangeKind.Station | LiveChangeKind.Alarm | LiveChangeKind.Feed, cancellationToken: cancellationToken);
    }
}
