using Opc.Ua;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using V = OPCAgentSample.Domain.Opc.StationVariables;

namespace OPCAgentSample.Server.Collector;

/// <summary>
/// Bildet gelesene OPC-Werte auf die Datensätze der Domäne ab – die Umkehrung dessen, was der Simulator aus den
/// Datensätzen des Generators macht (Takt-Variablen, Minutenwerte, JSON der Ereignis-Variablen).
/// </summary>
internal static class OpcRecordMapper
{
    /// <summary>
    /// Takte eines Rundtischs aus den Spalten von <see cref="HistoryNodeMap.CycleNodes"/> (gleiche Reihenfolge). Alle Spalten
    /// eines Takts tragen denselben Zeitstempel; ein Unterschied in Anzahl oder Zeit ist ein Datenfehler.
    /// </summary>
    public static IEnumerable<HistoryRecord> Cycles(string stationId, IReadOnlyList<List<DataValue>> columns)
    {
        var nodes = HistoryNodeMap.CycleNodes;
        if (columns.Count != nodes.Count) throw new ArgumentException("Falsche Anzahl Takt-Spalten.", nameof(columns));

        var column = nodes.Select((node, index) => (node.Variable, index)).ToDictionary(p => p.Variable, p => columns[p.index]);
        var cycleTime = column[V.CycleTime];
        foreach (var other in columns)
        {
            if (other.Count != cycleTime.Count)
                throw new InvalidDataException($"{stationId}: Taktwerte sind nicht deckungsgleich ({other.Count} statt {cycleTime.Count}).");
        }

        for (var i = 0; i < cycleTime.Count; i++)
        {
            var timestamp = OpcValues.ToUtc(cycleTime[i].SourceTimestamp);
            foreach (var other in columns)
            {
                if (other[i].SourceTimestamp != cycleTime[i].SourceTimestamp)
                    throw new InvalidDataException($"{stationId}: Zeitstempel eines Takts weichen zwischen den Knoten ab ({timestamp:O}).");
            }

            var order = (string?)column[V.CycleOrderId][i].Value;
            var scrap = OpcValues.ToInt32(column[V.CycleScrapReason][i].Value);
            yield return new CycleRecord(
                stationId,
                timestamp,
                string.IsNullOrEmpty(order) ? null : order,
                (string)column[V.CycleArticleId][i].Value,
                (string)column[V.CycleMixtureBatch][i].Value,
                OpcValues.ToDouble(cycleTime[i].Value),
                OpcValues.ToDouble(column[V.CycleTargetTime][i].Value),
                OpcValues.ToDouble(column[V.PressForce][i].Value),
                OpcValues.ToDouble(column[V.PressForceBuildUp][i].Value),
                OpcValues.ToDouble(column[V.FillWeight][i].Value),
                OpcValues.ToDouble(column[V.Thickness][i].Value),
                scrap == 0 ? null : (ScrapReason)(scrap - 1));
        }
    }

    /// <summary>Minutenwerte einer Variablen; der Zeitstempel des Wertes ist der Beginn der Minute.</summary>
    public static IEnumerable<HistoryRecord> Minutes(string stationId, string minuteVariable, List<DataValue> values) =>
        values.Where(v => StatusCode.IsGood(v.StatusCode))
            .Select(v => new MinuteValue(stationId, OpcValues.ToUtc(v.SourceTimestamp), minuteVariable, OpcValues.ToDouble(v.Value)));

    /// <summary>Datensätze einer Ereignis-Variable: Der Wert ist der Datensatz als JSON, ein leerer Wert (noch kein Ereignis) entfällt.</summary>
    public static IEnumerable<HistoryRecord> Events(EventNode node, List<DataValue> values) =>
        values.Where(v => v.Value is string { Length: > 0 })
            .Select(v => HistoryJson.Deserialize((string)v.Value, node.RecordType));
}
