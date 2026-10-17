using System.Globalization;
using Opc.Ua;
using OPCAgentSample.Domain.Opc;

namespace OPCAgentSample.Simulator.Model;

/// <summary>Wandelt Werte der Quellen (double, int, DateTimeOffset …) in den Datentyp des OPC-Knotens.</summary>
internal static class ValueMapper
{
    public static NodeId DataType(ValueKind kind, ushort namespaceIndex) => kind switch
    {
        ValueKind.Double => DataTypeIds.Double,
        ValueKind.Int32 => DataTypeIds.Int32,
        ValueKind.Int64 => DataTypeIds.Int64,
        ValueKind.String => DataTypeIds.String,
        ValueKind.Boolean => DataTypeIds.Boolean,
        ValueKind.DateTime => DataTypeIds.DateTime,
        ValueKind.OperatingState => new NodeId(NodeIdScheme.Type(PlantEnumerations.OperatingStateName), namespaceIndex),
        ValueKind.ScrapGrade => new NodeId(NodeIdScheme.Type(PlantEnumerations.ScrapGradeName), namespaceIndex),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public static object DefaultValue(ValueKind kind) => kind switch
    {
        ValueKind.Double => 0.0,
        ValueKind.Int32 or ValueKind.OperatingState or ValueKind.ScrapGrade => 0,
        ValueKind.Int64 => 0L,
        ValueKind.String => string.Empty,
        ValueKind.Boolean => false,
        ValueKind.DateTime => DateTime.MinValue,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Wert im Datentyp des Knotens; <c>null</c> bleibt <c>null</c> (keine Daten).</summary>
    public static object? ToNodeValue(ValueKind kind, object? value)
    {
        if (value is null) return null;
        return kind switch
        {
            ValueKind.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            ValueKind.Int32 or ValueKind.OperatingState or ValueKind.ScrapGrade => ToInt32(value),
            ValueKind.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            ValueKind.String => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            ValueKind.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            ValueKind.DateTime => value switch
            {
                DateTimeOffset offset => offset.UtcDateTime,
                DateTime dateTime => dateTime.ToUniversalTime(),
                _ => throw new InvalidCastException($"{value.GetType().Name} ist kein Zeitpunkt."),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    private static int ToInt32(object value) =>
        value is double number ? (int)Math.Round(number) : Convert.ToInt32(value, CultureInfo.InvariantCulture);
}
