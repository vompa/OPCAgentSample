using OPCAgentSample.Domain.History;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>Stufen der Rückverfolgung Mischcharge → Pressenlos → Ofencharge → Palette.</summary>
public enum TraceKind
{
    PressLot,
    OvenCharge,
    Pallet,
}

public sealed partial class RollupReader
{
    public async Task<bool> TraceExistsAsync(TraceKind kind, string id) =>
        await Database.KeyExistsAsync(kind switch
        {
            TraceKind.PressLot => RollupKeys.Lot(id),
            TraceKind.OvenCharge => RollupKeys.Charge(id),
            TraceKind.Pallet => RollupKeys.Pallet(id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        });

    /// <summary>Pressenlose einer Mischungscharge, nach Beginn sortiert.</summary>
    public async Task<IReadOnlyList<PressLot>> ReadLotsOfBatchAsync(string batchId) =>
        await ReadLotsAsync([.. (await Database.SetMembersAsync(RollupKeys.BatchLots(batchId))).Select(v => v.ToString())]);

    public async Task<IReadOnlyList<PressLot>> ReadLotsAsync(IReadOnlyCollection<string> lotIds)
    {
        var batch = Database.CreateBatch();
        var reads = lotIds.Select(id => (id, task: batch.HashGetAllAsync(RollupKeys.Lot(id)))).ToList();
        batch.Execute();
        var lots = new List<PressLot>();
        foreach (var (id, task) in reads)
        {
            var map = (await task).ToDictionary(e => (string)e.Name!, e => e.Value);
            if (map.Count == 0) continue;

            var start = new DateTimeOffset((long)map["b"], TimeSpan.Zero);
            lots.Add(new PressLot(
                map["st"].ToString(), start, id, map["m"].ToString(), map["a"].ToString(), map["s"].ToString(), start,
                new DateTimeOffset((long)map["e"], TimeSpan.Zero), (int)map["g"]));
        }

        return [.. lots.OrderBy(l => l.StartUtc).ThenBy(l => l.LotId, StringComparer.Ordinal)];
    }

    /// <summary>Verknüpfungen Ofencharge ↔ Pressenlos in beide Richtungen (je nach gesetztem Filter), sortiert nach Charge und Los.</summary>
    public async Task<IReadOnlyList<(string ChargeId, string LotId)>> ReadChargeLotsAsync(
        IReadOnlyCollection<string>? lotIds, IReadOnlyCollection<string>? chargeIds)
    {
        var links = new SortedSet<(string, string)>();
        foreach (var lot in lotIds ?? [])
        {
            foreach (var charge in await Database.SetMembersAsync(RollupKeys.LotCharges(lot))) links.Add((charge.ToString(), lot));
        }

        foreach (var charge in chargeIds ?? [])
        {
            foreach (var lot in await Database.SetMembersAsync(RollupKeys.ChargeLots(charge))) links.Add((charge, lot.ToString()));
        }

        return [.. links.OrderBy(l => l.Item1, StringComparer.Ordinal).ThenBy(l => l.Item2, StringComparer.Ordinal)];
    }

    /// <summary>Ofenchargen mit den Kennungen ihrer Pressenlose.</summary>
    public async Task<IReadOnlyList<OvenCharge>> ReadChargesAsync(IReadOnlyCollection<string> chargeIds)
    {
        var batch = Database.CreateBatch();
        var reads = chargeIds.Select(id => (id, task: batch.HashGetAllAsync(RollupKeys.Charge(id)), lots: batch.SetMembersAsync(RollupKeys.ChargeLots(id)))).ToList();
        batch.Execute();
        var charges = new List<OvenCharge>();
        foreach (var (id, task, lots) in reads)
        {
            var map = (await task).ToDictionary(e => (string)e.Name!, e => e.Value);
            if (map.Count == 0) continue;

            var unloaded = new DateTimeOffset((long)map["u"], TimeSpan.Zero);
            charges.Add(new OvenCharge(
                map["st"].ToString(), unloaded, id, (int)map["pl"], (int)map["d"], (int)map["n"], new DateTimeOffset((long)map["l"], TimeSpan.Zero),
                unloaded, [.. (await lots).Select(v => v.ToString()).Order(StringComparer.Ordinal)], (int)map["r"] == 1, (int)map["k"] == 1));
        }

        return charges;
    }

    /// <summary>Ofenchargen, die ab dem Zeitpunkt entladen wurden, neueste zuerst.</summary>
    public async Task<IReadOnlyList<OvenCharge>> ReadChargesUnloadedSinceAsync(DateTimeOffset from)
    {
        var ids = await Database.SortedSetRangeByScoreAsync(RollupKeys.ChargeIndex, from.ToUnixTimeMilliseconds());
        var charges = await ReadChargesAsync([.. ids.Select(v => v.ToString())]);
        return [.. charges.OrderByDescending(c => c.UnloadedUtc).ThenBy(c => c.ChargeId, StringComparer.Ordinal)];
    }

    public async Task<IReadOnlyList<PalletRecord>> ReadPalletsAsync(IReadOnlyCollection<string> palletIds)
    {
        var batch = Database.CreateBatch();
        var reads = palletIds.Select(id => (id, task: batch.HashGetAllAsync(RollupKeys.Pallet(id)), charges: batch.SetMembersAsync(RollupKeys.PalletCharges(id)))).ToList();
        batch.Execute();
        var pallets = new List<PalletRecord>();
        foreach (var (id, task, charges) in reads)
        {
            var map = (await task).ToDictionary(e => (string)e.Name!, e => e.Value);
            if (map.Count == 0) continue;

            var shipped = (long)map["s"];
            pallets.Add(new PalletRecord(
                map["st"].ToString(), new DateTimeOffset((long)map["t"], TimeSpan.Zero), id, map["a"].ToString(), (int)map["c"], (int)map["p"],
                [.. (await charges).Select(v => v.ToString()).Order(StringComparer.Ordinal)],
                shipped == 0 ? null : new DateTimeOffset(shipped, TimeSpan.Zero)));
        }

        return [.. pallets.OrderBy(p => p.TimestampUtc).ThenBy(p => p.PalletId, StringComparer.Ordinal)];
    }

    /// <summary>Paletten, in die die Ofenchargen eingegangen sind.</summary>
    public async Task<IReadOnlyList<PalletRecord>> ReadPalletsOfChargesAsync(IReadOnlyCollection<string> chargeIds)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var charge in chargeIds)
        {
            foreach (var pallet in await Database.SetMembersAsync(RollupKeys.ChargePallets(charge))) ids.Add(pallet.ToString());
        }

        return await ReadPalletsAsync(ids);
    }

    /// <summary>Gefertigte Takte eines Auftrags seit seinem Beginn.</summary>
    public async Task<long> ReadOrderCyclesAsync(string orderId)
    {
        var value = await Database.HashGetAsync(RollupKeys.OrderCount(orderId), "c");
        return value.HasValue ? (long)value : 0;
    }

    /// <summary>Takte einer Pressform seit dem Einbau (innerhalb der Historie).</summary>
    public async Task<long> ReadMouldCyclesAsync(string mouldId)
    {
        var value = await Database.HashGetAsync(RollupKeys.MouldCount(mouldId), "c");
        return value.HasValue ? (long)value : 0;
    }
}
