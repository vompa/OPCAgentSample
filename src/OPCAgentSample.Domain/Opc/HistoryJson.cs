using System.Text.Json;
using System.Text.Json.Serialization;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Domain.Opc;

/// <summary>
/// JSON-Format der Ereignis-Variablen (siehe <see cref="EventVariables"/>): Datensätze der Domäne mit PascalCase-Namen,
/// Enums als Text und ISO-8601-Zeitstempeln. Der Simulator schreibt sie, der Collector liest sie mit denselben Optionen
/// in die Domänentypen zurück.
/// </summary>
public static class HistoryJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(HistoryRecord record) => JsonSerializer.Serialize(record, record.GetType(), Options);

    public static TRecord Deserialize<TRecord>(string json) where TRecord : HistoryRecord =>
        JsonSerializer.Deserialize<TRecord>(json, Options) ?? throw new JsonException("Leerer Datensatz.");

    /// <summary>Liest einen Datensatz des angegebenen Typs (aus <see cref="HistoryNodeMap.EventNodes"/>).</summary>
    public static HistoryRecord Deserialize(string json, Type recordType) =>
        JsonSerializer.Deserialize(json, recordType, Options) as HistoryRecord ?? throw new JsonException("Leerer Datensatz.");
}
