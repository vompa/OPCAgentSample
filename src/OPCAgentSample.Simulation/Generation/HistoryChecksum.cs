using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Prüfsumme (FNV-1a, 64 Bit) über alle Felder eines Datenstroms; prozessunabhängig stabil. Belegt die
/// Reproduzierbarkeit (gleicher Seed → gleiche Prüfsumme) und erlaubt dem Collector einen Abgleich nach dem Nachladen.
/// </summary>
public sealed class HistoryChecksum
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public ulong Value { get; private set; } = Offset;

    public long Count { get; private set; }

    public static HistoryChecksum Of(IEnumerable<HistoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var checksum = new HistoryChecksum();
        foreach (var record in records)
        {
            checksum.Add(record);
        }

        return checksum;
    }

    public void Add(HistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Count++;
        Add(record.GetType().Name);
        Add(record.StationId);
        Add(record.TimestampUtc.UtcTicks);
        switch (record)
        {
            case CycleRecord c:
                Add(c.OrderId); Add(c.ArticleId); Add(c.MixtureBatchId); Add(c.CycleTimeSeconds); Add(c.TargetCycleSeconds);
                Add(c.PressForceKn); Add(c.PressForceBuildUpSeconds); Add(c.FillWeightGrams); Add(c.ThicknessMm); Add((long)(c.Scrap ?? (ScrapReason)(-1)));
                break;
            case MinuteValue m:
                Add(m.Variable); Add(m.Value);
                break;
            case UnitCountRecord u:
                Add(u.ArticleId); Add(u.Good); Add(u.Scrap); Add(u.TargetCycleSeconds);
                break;
            case LossEvent l:
                Add(l.Id); Add(l.Category.ToString()); Add(l.Reason); Add(l.Assembly); Add(l.Cause); Add(l.StartUtc.UtcTicks);
                Add(l.EndUtc?.UtcTicks ?? 0); Add(l.AffectedPieces); Add(l.Acknowledged ? 1 : 0); Add(l.Note);
                break;
            case AlarmRecord a:
                Add(a.Id); Add(a.Kind.ToString()); Add(a.Severity.ToString()); Add(a.Text); Add(a.ActiveSinceUtc.UtcTicks);
                Add(a.AcknowledgedUtc?.UtcTicks ?? 0); Add(a.ClearedUtc?.UtcTicks ?? 0); Add(a.LossEventId);
                break;
            case StateChange s:
                Add(s.State.ToString()); Add(s.OrderId);
                break;
            case OrderEvent o:
                Add(o.OrderId); Add(o.Kind.ToString()); Add(o.Quantity);
                break;
            case ShiftPlanRecord p:
                Add(p.ShiftKey); Add(p.PlannedQuantity);
                break;
            case PressLot lot:
                Add(lot.LotId); Add(lot.MixtureBatchId); Add(lot.GoodPieces);
                break;
            case OvenCharge charge:
                Add(charge.ChargeId); Add(charge.Place); Add(charge.Plates); Add(string.Join(',', charge.PressLotIds));
                break;
            case PalletRecord pallet:
                Add(pallet.PalletId); Add(pallet.Cartons); Add(string.Join(',', pallet.OvenChargeIds)); Add(pallet.ShippedUtc?.UtcTicks ?? 0);
                break;
            default:
                Add(record.ToString());
                break;
        }
    }

    private void Add(string? text)
    {
        foreach (var character in text ?? "\0")
        {
            Mix(character);
        }

        Mix(0xFFFF);
    }

    private void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    private void Add(long value)
    {
        for (var shift = 0; shift < 64; shift += 16)
        {
            Mix((ulong)((value >> shift) & 0xFFFF));
        }
    }

    private void Mix(ulong value) => Value = (Value ^ value) * Prime;
}
