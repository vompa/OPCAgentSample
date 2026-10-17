using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

public sealed partial class RollupEngine
{
    private const string PayloadField = "d";

    // ---- Schreiben: Prozessstatistik ---------------------------------------------------------------------------

    /// <summary>
    /// Schreibt die berührten Fächer (15 min, bei Rundtischen auch 1 min), die Statistik der Schicht und – als Summe der Kinder –
    /// Tag, Woche, Monat, Quartal und Jahr jeder Reihe, die in diesem Fenster neue Werte bekommen hat.
    /// </summary>
    private void WriteStats(TxWriter tx, RunningShift run, ShiftCalendar calendar)
    {
        var quarter = PeriodKeys.Quarter(run.Day);
        var year = PeriodKeys.Year(run.Day);
        foreach (var series in run.AllSeries.Where(s => s.IsDirty))
        {
            var info = series.Info;
            WriteBuckets(tx, info, "15m", run.DayKey, series.Coarse, series.DirtyCoarse, Retention.FifteenMinutes);
            if (info.HasFine) WriteBuckets(tx, info, "1m", run.DayKey, series.Fine, series.DirtyFine, Retention.OneMinute);

            var owner = StatOwner.Of(info);
            var shiftStat = series.ShiftStat();
            (string ShiftKey, ProcessStat Value)? current = (run.Period.Key, shiftStat);
            WriteStat(tx, info, RollupLevel.Shift.Code(), quarter, run.Period.Key, shiftStat, Retention.Shift);
            WriteStat(tx, info, RollupLevel.Day.Code(), year, run.DayKey, _stats!.Day(owner, run.DayKey, current)!, Retention.DayAndWeek);
            foreach (var level in RollupLevels.AboveDay)
            {
                var key = PeriodKeys.Of(level, run.Day, calendar);
                WriteStat(tx, info, level.Code(), "-", key, _stats.Parent(owner, level, key, current)!, Retention.For(level));
            }
        }
    }

    private static void WriteBuckets(
        TxWriter tx, SeriesInfo info, string resolution, string dayKey, Dictionary<int, ProcessStat> buckets, HashSet<int> dirty, TimeSpan ttl)
    {
        if (dirty.Count == 0) return;

        var entries = dirty.Select(slot => new HashEntry(slot, buckets[slot].Encode(wide: false))).ToArray();
        tx.HashSet(RollupKeys.Stats(info.Station, info.Variable, resolution, dayKey), entries, ttl);
    }

    private static void WriteStat(TxWriter tx, SeriesInfo info, string resolution, string container, string field, ProcessStat stat, TimeSpan ttl) =>
        tx.HashSet(RollupKeys.Stats(info.Station, info.Variable, resolution, container), [new HashEntry(field, stat.Encode(wide: true))], ttl);

    // ---- Schreiben: Ereignisse, Zustand, Rückverfolgung, Ringpuffer ---------------------------------------------

    private async Task WriteStreamsAsync(TxWriter tx, IDatabase database, RollupContext context, WindowWork work, ProductionPeriod? shift)
    {
        var hall = context.HallId;
        await WriteLossesAsync(tx, database, hall, work);
        await WriteAlarmsAsync(tx, database, hall, work);
        await WriteStatesAndOrdersAsync(tx, database, hall, work);
        WriteTraceability(tx, work);
        await WriteRingBuffersAsync(tx, database, work);
        WriteCounts(tx, work);
        if (work.FirstClimateTemperature is { } temperature && shift is not null)
        {
            tx.HashSetIfAbsent(RollupKeys.ShiftInfo(shift.Key), "ct0", temperature.ToString("R", CultureInfo.InvariantCulture), Retention.Shift);
        }
    }

    /// <summary>
    /// Geschlossene Verlustereignisse stehen im Stream (Id = Zeitpunkt der Endemeldung, aufsteigend), offene in einem Hash, weil eine
    /// Stream-Id nicht nachträglich geändert werden kann. Ein neuer Stand ersetzt den alten (<c>:done</c> kennt die Stream-Id).
    /// </summary>
    private async Task WriteLossesAsync(TxWriter tx, IDatabase database, string hall, WindowWork work)
    {
        if (work.Losses.Count == 0) return;

        var stream = RollupKeys.LossStream(hall);
        var open = RollupKeys.LossOpen(hall);
        var done = RollupKeys.LossDone(hall);
        var ordered = work.Losses.Values.OrderBy(l => l.EndUtc ?? DateTimeOffset.MaxValue).ThenBy(l => l.Id, StringComparer.Ordinal).ToList();
        var previous = await database.HashGetAsync(done, [.. ordered.Select(l => (RedisValue)l.Id)]);
        var ids = await IdsAsync(database, stream);
        for (var i = 0; i < ordered.Count; i++)
        {
            var loss = ordered[i];
            if (!previous[i].IsNull) tx.StreamDelete(stream, previous[i].ToString());

            tx.HashDelete(open, loss.Id);
            if (loss.EndUtc is { } end)
            {
                var id = ids.Next(end);
                tx.StreamAdd(stream, id, [new NameValueEntry(PayloadField, EventCodec.Pack(loss))]);
                tx.HashSet(done, [new HashEntry(loss.Id, id)], Retention.Events);
            }
            else
            {
                tx.HashSet(open, [new HashEntry(loss.Id, EventCodec.Pack(loss))], Retention.Events);
                tx.HashDelete(done, loss.Id);
            }
        }

        tx.Expire(stream, Retention.Events);
    }

    private async Task WriteAlarmsAsync(TxWriter tx, IDatabase database, string hall, WindowWork work)
    {
        if (work.Alarms.Count == 0) return;

        var stream = RollupKeys.AlarmStream(hall);
        var open = RollupKeys.AlarmOpen(hall);
        var done = RollupKeys.AlarmDone(hall);
        var ordered = work.Alarms.Values.OrderBy(a => a.ClearedUtc ?? DateTimeOffset.MaxValue).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        var previous = await database.HashGetAsync(done, [.. ordered.Select(a => (RedisValue)a.Id)]);
        var ids = await IdsAsync(database, stream);
        for (var i = 0; i < ordered.Count; i++)
        {
            var alarm = ordered[i];
            if (!previous[i].IsNull) tx.StreamDelete(stream, previous[i].ToString());

            tx.HashDelete(open, alarm.Id);
            if (alarm.ClearedUtc is { } cleared)
            {
                var id = ids.Next(cleared);
                tx.StreamAdd(stream, id, [new NameValueEntry(PayloadField, EventCodec.Pack(alarm))]);
                tx.HashSet(done, [new HashEntry(alarm.Id, id)], Retention.Events);
            }
            else
            {
                tx.HashSet(open, [new HashEntry(alarm.Id, EventCodec.Pack(alarm))], Retention.Events);
                tx.HashDelete(done, alarm.Id);
            }
        }

        tx.Expire(stream, Retention.Events);
    }

    private async Task WriteStatesAndOrdersAsync(TxWriter tx, IDatabase database, string hall, WindowWork work)
    {
        foreach (var group in work.States.GroupBy(s => s.StationId))
        {
            var stream = RollupKeys.StateStream(group.Key);
            var ids = await IdsAsync(database, stream);
            foreach (var state in group.OrderBy(s => s.TimestampUtc))
            {
                tx.StreamAdd(stream, ids.Next(state.TimestampUtc), [new NameValueEntry(PayloadField, EventCodec.Pack(state))]);
            }

            tx.Expire(stream, Retention.Events);
        }

        if (work.Orders.Count == 0) return;

        var orderStream = RollupKeys.OrderStream(hall);
        var orderIds = await IdsAsync(database, orderStream);
        foreach (var order in work.Orders.OrderBy(o => o.TimestampUtc))
        {
            tx.StreamAdd(orderStream, orderIds.Next(order.TimestampUtc), [new NameValueEntry(PayloadField, EventCodec.Pack(order))]);
        }

        tx.Expire(orderStream, Retention.Events);
    }

    /// <summary>Rückverfolgung: ein Hash je Los, Charge und Palette plus Verknüpfungsmengen in beide Richtungen.</summary>
    private static void WriteTraceability(TxWriter tx, WindowWork work)
    {
        var ttl = Retention.Events;
        foreach (var lot in work.Lots)
        {
            tx.HashSet(RollupKeys.Lot(lot.LotId),
            [
                new("st", lot.StationId), new("m", lot.MixtureBatchId), new("a", lot.ArticleId), new("s", lot.ShiftKey),
                new("b", lot.StartUtc.UtcTicks), new("e", lot.EndUtc.UtcTicks), new("g", lot.GoodPieces),
            ], ttl);
            tx.SetAdd(RollupKeys.BatchLots(lot.MixtureBatchId), lot.LotId);
            tx.Expire(RollupKeys.BatchLots(lot.MixtureBatchId), ttl);
        }

        foreach (var charge in work.Charges)
        {
            tx.HashSet(RollupKeys.Charge(charge.ChargeId),
            [
                new("st", charge.StationId), new("pl", charge.Place), new("d", charge.DiameterMm), new("n", charge.Plates),
                new("l", charge.LoadedUtc.UtcTicks), new("u", charge.UnloadedUtc.UtcTicks),
                new("r", charge.RotationTestPassed ? 1 : 0), new("k", charge.BreakTestPassed ? 1 : 0),
            ], ttl);
            tx.SortedSetAdd(RollupKeys.ChargeIndex, charge.ChargeId, charge.UnloadedUtc.ToUnixTimeMilliseconds());
            if (charge.PressLotIds.Count == 0) continue;

            tx.SetAdd(RollupKeys.ChargeLots(charge.ChargeId), [.. charge.PressLotIds.Select(l => (RedisValue)l)]);
            tx.Expire(RollupKeys.ChargeLots(charge.ChargeId), ttl);
            foreach (var lotId in charge.PressLotIds)
            {
                tx.SetAdd(RollupKeys.LotCharges(lotId), charge.ChargeId);
                tx.Expire(RollupKeys.LotCharges(lotId), ttl);
            }
        }

        foreach (var pallet in work.Pallets)
        {
            tx.HashSet(RollupKeys.Pallet(pallet.PalletId),
            [
                new("st", pallet.StationId), new("a", pallet.ArticleId), new("c", pallet.Cartons), new("p", pallet.PiecesPerCarton),
                new("s", pallet.ShippedUtc?.UtcTicks ?? 0), new("t", pallet.TimestampUtc.UtcTicks),
            ], ttl);
            if (pallet.OvenChargeIds.Count == 0) continue;

            tx.SetAdd(RollupKeys.PalletCharges(pallet.PalletId), [.. pallet.OvenChargeIds.Select(c => (RedisValue)c)]);
            tx.Expire(RollupKeys.PalletCharges(pallet.PalletId), ttl);
            foreach (var chargeId in pallet.OvenChargeIds)
            {
                tx.SetAdd(RollupKeys.ChargePallets(chargeId), pallet.PalletId);
                tx.Expire(RollupKeys.ChargePallets(chargeId), ttl);
            }
        }
    }

    /// <summary>Ringpuffer der letzten 1000 Takte je Rundtisch und Minutenreihen (vier Stunden) je Variable.</summary>
    private async Task WriteRingBuffersAsync(TxWriter tx, IDatabase database, WindowWork work)
    {
        foreach (var (station, cycles) in work.Cycles)
        {
            var stream = RollupKeys.RingBuffer(station);
            var ids = await IdsAsync(database, stream);
            foreach (var c in cycles.TakeLast(Retention.RingBufferCycles))
            {
                tx.StreamAdd(stream, ids.Next(c.TimestampUtc), RingFields.ToFields(c), Retention.RingBufferCycles);
            }

            tx.Expire(stream, Retention.Events);
        }

        foreach (var ((station, variable), minutes) in work.Minutes)
        {
            var stream = RollupKeys.MinuteRing(station, variable);
            var ids = await IdsAsync(database, stream);
            foreach (var minute in minutes.TakeLast(Retention.MinuteRingLength))
            {
                tx.StreamAdd(
                    stream, ids.Next(minute.TimestampUtc),
                    [new NameValueEntry(PayloadField, minute.Value.ToString("R", CultureInfo.InvariantCulture))], Retention.MinuteRingLength);
            }

            tx.Expire(stream, Retention.OneMinute);
        }
    }

    /// <summary>Takte je Auftrag und Hübe je Pressform zählen mit dem Fenster weiter (der Wasserstand im selben <c>MULTI</c> verhindert Doppelzählung).</summary>
    private static void WriteCounts(TxWriter tx, WindowWork work)
    {
        foreach (var (station, article) in work.PackagingArticles)
        {
            tx.HashSet(RollupKeys.LastKnown(station), [new HashEntry("a", article)], Retention.Events);
        }

        foreach (var (orderId, (station, cycles)) in work.OrderCycles)
        {
            tx.HashIncrement(RollupKeys.OrderCount(orderId), "c", cycles);
            tx.HashSetIfAbsent(RollupKeys.OrderCount(orderId), "st", station, Retention.Events);
        }

        foreach (var (mouldId, cycles) in work.MouldCycles)
        {
            tx.HashIncrement(RollupKeys.MouldCount(mouldId), "c", cycles);
        }
    }

    private async Task<StreamIds> IdsAsync(IDatabase database, string stream)
    {
        if (_ids.TryGetValue(stream, out var known)) return known;

        var ids = await StreamIds.LoadAsync(database, stream);
        _ids[stream] = ids;
        return ids;
    }
}

/// <summary>Felder eines Takts im Ringpuffer (kurze Namen, Zahlen verlustfrei als Text).</summary>
internal static class RingFields
{
    public static NameValueEntry[] ToFields(CycleRecord c) =>
    [
        new("o", c.OrderId ?? string.Empty),
        new("a", c.ArticleId),
        new("m", c.MixtureBatchId),
        new("t", c.TargetCycleSeconds.ToString("R", CultureInfo.InvariantCulture)),
        new("c", c.CycleTimeSeconds.ToString("R", CultureInfo.InvariantCulture)),
        new("f", c.PressForceKn.ToString("R", CultureInfo.InvariantCulture)),
        new("b", c.PressForceBuildUpSeconds.ToString("R", CultureInfo.InvariantCulture)),
        new("w", c.FillWeightGrams.ToString("R", CultureInfo.InvariantCulture)),
        new("d", c.ThicknessMm.ToString("R", CultureInfo.InvariantCulture)),
        new("x", c.Scrap?.ToString() ?? string.Empty),
    ];

    public static CycleRecord FromEntry(string station, StreamEntry entry)
    {
        string Field(string name) => entry.Values.First(v => v.Name == name).Value.ToString();
        double Number(string name) => double.Parse(Field(name), CultureInfo.InvariantCulture);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(entry.Id.ToString().Split('-')[0], CultureInfo.InvariantCulture));
        var scrap = Field("x");
        return new CycleRecord(
            station, at, Field("o").Length == 0 ? null : Field("o"), Field("a"), Field("m"), Number("c"), Number("t"), Number("f"),
            Number("b"), Number("w"), Number("d"), scrap.Length == 0 ? null : Enum.Parse<ScrapReason>(scrap));
    }
}
