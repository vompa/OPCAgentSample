using System.Globalization;

namespace OPCAgentSample.Server.Collector;

/// <summary>Umwandlung von OPC-Werten (Variant-Inhalt) in die Typen der Domäne.</summary>
internal static class OpcValues
{
    /// <summary>Zeitstempel (DateTime, UTC) als <see cref="DateTimeOffset"/>.</summary>
    public static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc), TimeSpan.Zero);

    public static DateTimeOffset ToUtc(object? value) => value switch
    {
        DateTime dateTime => ToUtc(dateTime),
        DateTimeOffset offset => offset.ToUniversalTime(),
        _ => throw new InvalidCastException($"{value?.GetType().Name ?? "null"} ist kein Zeitpunkt."),
    };

    public static double ToDouble(object? value) => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    public static int ToInt32(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
}
