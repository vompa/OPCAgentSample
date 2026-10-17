using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

public sealed partial class RollupReader
{
    /// <summary>
    /// Ereignisse stehen mit dem Zeitpunkt ihrer Endemeldung als Stream-Id. Kein Ereignis des Szenarios dauert länger als 24 Stunden;
    /// die Abfrage eines Fensters liest deshalb bis 24 Stunden über dessen Ende hinaus und filtert auf Überschneidung.
    /// </summary>
    public static readonly TimeSpan MaxEventDuration = TimeSpan.FromHours(24);

    /// <summary>Verlustereignisse, die das Fenster berühren (neuester Stand je Id), nach Beginn sortiert.</summary>
    public async Task<IReadOnlyList<LossEvent>> ReadLossesAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string>? stations = null, string hall = PlantLayout.HallA)
    {
        var closed = await Database.StreamRangeAsync(RollupKeys.LossStream(hall), MillisecondsOf(from), MillisecondsOf(to + MaxEventDuration));
        var open = await Database.HashGetAllAsync(RollupKeys.LossOpen(hall));
        var losses = closed.Select(e => EventCodec.UnpackLoss(Payload(e))).Concat(open.Select(e => EventCodec.UnpackLoss(e.Value.ToString())));
        return [.. losses
            .Where(l => l.StartUtc < to && (l.EndUtc is null || l.EndUtc > from) && (stations is null || stations.Contains(l.StationId)))
            .OrderBy(l => l.StartUtc)
            .ThenBy(l => l.Id, StringComparer.Ordinal)];
    }

    /// <summary>Verlustereignisse, die den Zeitraum einer Schicht berühren (für die Wiederaufnahme und die Neuberechnung).</summary>
    public Task<IReadOnlyList<LossEvent>> ReadLossesOverlappingAsync(DateTimeOffset from, DateTimeOffset to) => ReadLossesAsync(from, to);

    /// <summary>Alarme, die im Fenster aktiv waren (aktiv seit vor dem Ende und nicht vor dem Beginn beendet), nach Aktivierung sortiert.</summary>
    public async Task<IReadOnlyList<AlarmRecord>> ReadAlarmsAsync(
        DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string>? stations = null, string hall = PlantLayout.HallA)
    {
        var closed = await Database.StreamRangeAsync(RollupKeys.AlarmStream(hall), MillisecondsOf(from), MillisecondsOf(to + MaxEventDuration));
        var open = await Database.HashGetAllAsync(RollupKeys.AlarmOpen(hall));
        var alarms = closed.Select(e => EventCodec.UnpackAlarm(Payload(e))).Concat(open.Select(e => EventCodec.UnpackAlarm(e.Value.ToString())));
        return [.. alarms
            .Where(a => a.ActiveSinceUtc < to && (a.ClearedUtc is null || a.ClearedUtc > from) && (stations is null || stations.Contains(a.StationId)))
            .OrderBy(a => a.ActiveSinceUtc)
            .ThenBy(a => a.Id, StringComparer.Ordinal)];
    }

    /// <summary>Alarme, die noch nicht beendet sind (Störungen, Zustandswarnungen und Meldungen).</summary>
    public async Task<IReadOnlyList<AlarmRecord>> ReadOpenAlarmsAsync(string hall = PlantLayout.HallA)
    {
        var open = await Database.HashGetAllAsync(RollupKeys.AlarmOpen(hall));
        return [.. open.Select(e => EventCodec.UnpackAlarm(e.Value.ToString())).OrderBy(a => a.ActiveSinceUtc).ThenBy(a => a.Id, StringComparer.Ordinal)];
    }

    /// <summary>Letzter Zustandswechsel einer Station bis zum Zeitpunkt (einschließlich) oder <see langword="null"/>.</summary>
    public async Task<StateChange?> ReadStateAtAsync(string station, DateTimeOffset at)
    {
        var rows = await Database.StreamRangeAsync(
            RollupKeys.StateStream(station), "-", MillisecondsOf(at), count: 1, messageOrder: Order.Descending);
        return rows.Length == 0 ? null : EventCodec.UnpackState(station, TimeOf(rows[0]), Payload(rows[0]));
    }

    /// <summary>Auftragsereignisse im Fenster, nach Zeit sortiert.</summary>
    public async Task<IReadOnlyList<OrderEvent>> ReadOrderEventsAsync(DateTimeOffset from, DateTimeOffset to, string hall = PlantLayout.HallA)
    {
        var rows = await Database.StreamRangeAsync(RollupKeys.OrderStream(hall), MillisecondsOf(from), MillisecondsOf(to));
        return [.. rows.Select(r => EventCodec.UnpackOrder(TimeOf(r), Payload(r))).Where(o => o.TimestampUtc >= from && o.TimestampUtc < to)];
    }

    /// <summary>Die letzten Takte eines Rundtischs (neueste zuletzt), höchstens <paramref name="count"/>, bis zum Zeitpunkt (ausgeschlossen).</summary>
    public async Task<IReadOnlyList<CycleRecord>> ReadRecentCyclesAsync(string station, DateTimeOffset until, int count = Retention.RingBufferCycles)
    {
        var rows = await Database.StreamRangeAsync(
            RollupKeys.RingBuffer(station), "-", MillisecondsOf(until), count: count, messageOrder: Order.Descending);
        return [.. rows.Select(r => RingFields.FromEntry(station, r)).Reverse().Where(c => c.TimestampUtc < until)];
    }

    /// <summary>Letzte Minutenwerte einer Variablen bis zum Zeitpunkt (ausgeschlossen), älteste zuerst.</summary>
    public async Task<IReadOnlyList<(DateTimeOffset At, double Value)>> ReadMinutesAsync(
        string station, string variable, DateTimeOffset from, DateTimeOffset until)
    {
        var rows = await Database.StreamRangeAsync(
            RollupKeys.MinuteRing(station, variable), MillisecondsOf(from), MillisecondsOf(until));
        return [.. rows.Select(r => (TimeOf(r), double.Parse(Payload(r), CultureInfo.InvariantCulture)))
            .Where(m => m.Item1 >= from && m.Item1 < until)];
    }

    /// <summary>Alle Statistiken der Schichten (Suchlauf über die Schlüssel): Reihe (<c>Station|Variable</c>), Schichtschlüssel, Fach.</summary>
    internal async Task<IReadOnlyList<(string Owner, string ShiftKey, ProcessStat Stat)>> ReadShiftStatsAsync()
    {
        var result = new List<(string, string, ProcessStat)>();
        foreach (var key in await ScanAsync($"ps:{RollupKeys.StatsVersion}:*:*:{RollupLevel.Shift.Code()}:*"))
        {
            var parts = key.Split(':');
            var info = SeriesCatalog.For(parts[2], parts[3]);
            foreach (var entry in await Database.HashGetAllAsync(key))
            {
                result.Add((StatOwner.Of(info), (string)entry.Name!, ProcessStat.Decode((byte[])entry.Value!, info.Bins, wide: true)));
            }
        }

        return result;
    }

    /// <summary>Statistik der Fächer einer Reihe aus einem Hash (feine Auflösung: schmale Zähler im Histogramm).</summary>
    public async Task<IReadOnlyDictionary<string, ProcessStat>> ReadStatsAsync(
        string station, string variable, string resolution, string container, IReadOnlyCollection<string>? fields = null)
    {
        var info = SeriesCatalog.For(station, variable);
        var wide = resolution is not ("1m" or "15m");
        var key = RollupKeys.Stats(station, variable, resolution, container);
        HashEntry[] entries;
        if (fields is null)
        {
            entries = await Database.HashGetAllAsync(key);
        }
        else
        {
            var names = fields.Select(f => (RedisValue)f).ToArray();
            var values = await Database.HashGetAsync(key, names);
            entries = [.. names.Zip(values, (name, value) => new HashEntry(name, value)).Where(e => !e.Value.IsNull)];
        }

        return entries.ToDictionary(e => (string)e.Name!, e => ProcessStat.Decode((byte[])e.Value!, info.Bins, wide));
    }

    /// <summary>Hallentemperatur im ersten Minutenwert der Schicht oder <see langword="null"/>.</summary>
    public async Task<double?> ReadClimateAtShiftStartAsync(string shiftKey)
    {
        var value = await Database.HashGetAsync(RollupKeys.ShiftInfo(shiftKey), "ct0");
        return value.HasValue ? double.Parse(value.ToString(), CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Zuletzt gezählter Artikel einer Verpackung oder <see langword="null"/>.</summary>
    public async Task<string?> ReadLastPackagingArticleAsync(string station)
    {
        var value = await Database.HashGetAsync(RollupKeys.LastKnown(station), "a");
        return value.HasValue ? value.ToString() : null;
    }

    /// <summary>Wasserstand einer Quelle (bis hierhin, ausgeschlossen, ist alles aufgenommen) oder <see langword="null"/>.</summary>
    public async Task<DateTimeOffset?> ReadWatermarkAsync(string sourceId)
    {
        var value = await Database.HashGetAsync(RollupKeys.Watermarks, sourceId);
        return value.HasValue ? new DateTimeOffset((long)value, TimeSpan.Zero) : null;
    }

    /// <summary>Beginn der Rohdaten-Historie (erste Schicht mit Daten) oder <see langword="null"/>, solange nichts aufgenommen ist.</summary>
    public async Task<DateTimeOffset?> ReadRawStartAsync()
    {
        var value = await Database.StringGetAsync(RollupKeys.RawStart);
        return value.HasValue ? new DateTimeOffset((long)value, TimeSpan.Zero) : null;
    }

    /// <summary>Der Datenstand aller Quellen zusammen: der kleinste Wasserstand; <see langword="null"/> ohne Aufnahme.</summary>
    public async Task<DateTimeOffset?> ReadOverallWatermarkAsync()
    {
        var all = await Database.HashGetAllAsync(RollupKeys.Watermarks);
        return all.Length == 0 ? null : new DateTimeOffset(all.Min(e => (long)e.Value), TimeSpan.Zero);
    }

    private static string MillisecondsOf(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static string Payload(StreamEntry entry) => entry.Values.First(v => v.Name == "d").Value.ToString();

    private static DateTimeOffset TimeOf(StreamEntry entry) =>
        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(entry.Id.ToString().Split('-')[0], CultureInfo.InvariantCulture));
}
