using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Kompakte Textform der Ereignisse in Redis (ein Feld je Stream-Eintrag bzw. Hash-Wert). Die Felder trennt das Zeichen
/// <c>U+001F</c> (Einheitentrenner), das in Notizen und Texten nicht vorkommt.
/// </summary>
public static class EventCodec
{
    private const char Separator = '\u001f';

    public static string Pack(LossEvent loss)
    {
        ArgumentNullException.ThrowIfNull(loss);
        return string.Join(Separator,
            loss.Id, loss.StationId, loss.Assembly, loss.Category, loss.Reason, loss.Cause,
            Stamp(loss.StartUtc), Stamp(loss.EndUtc), Num(loss.AffectedPieces), loss.Acknowledged ? "1" : "0", loss.Note ?? string.Empty,
            Stamp(loss.TimestampUtc));
    }

    public static LossEvent UnpackLoss(string packed)
    {
        var f = packed.Split(Separator);
        return new LossEvent(
            f[1], FromStamp(f[11])!.Value, f[0], f[2], Enum.Parse<LossCategory>(f[3]), f[4], f[5],
            FromStamp(f[6])!.Value, FromStamp(f[7]), int.Parse(f[8], CultureInfo.InvariantCulture), f[9] == "1", f[10].Length == 0 ? null : f[10]);
    }

    public static string Pack(AlarmRecord alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return string.Join(Separator,
            alarm.Id, alarm.StationId, alarm.Kind, alarm.Severity, alarm.Assembly, alarm.Text,
            Stamp(alarm.ActiveSinceUtc), Stamp(alarm.AcknowledgedUtc), Stamp(alarm.ClearedUtc), alarm.LossEventId ?? string.Empty,
            Stamp(alarm.TimestampUtc));
    }

    public static AlarmRecord UnpackAlarm(string packed)
    {
        var f = packed.Split(Separator);
        return new AlarmRecord(
            f[1], FromStamp(f[10])!.Value, f[0], Enum.Parse<AlarmKind>(f[2]), Enum.Parse<AlarmSeverity>(f[3]), f[4], f[5],
            FromStamp(f[6])!.Value, FromStamp(f[7]), FromStamp(f[8]), f[9].Length == 0 ? null : f[9]);
    }

    public static string Pack(StateChange state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return string.Join(Separator, state.State, state.OrderId ?? string.Empty);
    }

    public static StateChange UnpackState(string station, DateTimeOffset at, string packed)
    {
        var f = packed.Split(Separator);
        return new StateChange(station, at, Enum.Parse<OperatingState>(f[0]), f[1].Length == 0 ? null : f[1]);
    }

    public static string Pack(OrderEvent order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return string.Join(Separator, order.StationId, order.OrderId, order.Kind, Num(order.Quantity));
    }

    public static OrderEvent UnpackOrder(DateTimeOffset at, string packed)
    {
        var f = packed.Split(Separator);
        return new OrderEvent(f[0], at, f[1], Enum.Parse<OrderEventKind>(f[2]), int.Parse(f[3], CultureInfo.InvariantCulture));
    }

    /// <summary>Zeitpunkt als UTC-Ticks (verlustfrei, die Dauern rechnen in Ticks); leer für „kein Zeitpunkt“.</summary>
    private static string Stamp(DateTimeOffset? value) =>
        value is { } v ? v.UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty;

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? FromStamp(string text) =>
        text.Length == 0 ? null : new DateTimeOffset(long.Parse(text, CultureInfo.InvariantCulture), TimeSpan.Zero);
}
