using Opc.Ua;
using ISession = Opc.Ua.Client.ISession;

namespace OPCAgentSample.Server.Collector;

/// <summary>HistoryRead Raw für mehrere Knoten, seitenweise über ContinuationPoints, bis alle Werte des Fensters vorliegen.</summary>
internal static class OpcHistoryClient
{
    /// <summary>Knoten je Dienstaufruf; hält die Antworten klein (der Simulator begrenzt Nachrichten auf 4 MB).</summary>
    private const int NodesPerRequest = 10;

    /// <summary>
    /// Liest die Werte in [<paramref name="from"/>, <paramref name="until"/>) für jeden Knoten und liefert sie je Knoten in
    /// Zeitreihenfolge zurück (gleiche Reihenfolge wie <paramref name="nodes"/>). Ein schlechter Statuscode eines Knotens ist ein Fehler.
    /// </summary>
    public static async Task<List<DataValue>[]> ReadRawAsync(
        ISession session, IReadOnlyList<NodeId> nodes, DateTimeOffset from, DateTimeOffset until, int pageSize,
        CancellationToken cancellationToken)
    {
        var results = new List<DataValue>[nodes.Count];
        for (var offset = 0; offset < nodes.Count; offset += NodesPerRequest)
        {
            var chunk = nodes.Skip(offset).Take(NodesPerRequest).ToList();
            var values = await ReadChunkAsync(session, chunk, from, until, pageSize, cancellationToken);
            for (var i = 0; i < chunk.Count; i++) results[offset + i] = values[i];
        }

        return results;
    }

    private static async Task<List<DataValue>[]> ReadChunkAsync(
        ISession session, List<NodeId> nodes, DateTimeOffset from, DateTimeOffset until, int pageSize, CancellationToken cancellationToken)
    {
        var details = new ReadRawModifiedDetails
        {
            StartTime = from.UtcDateTime,
            EndTime = until.UtcDateTime,
            NumValuesPerNode = (uint)pageSize,
            // Der Standardwert der SDK-Klasse ist true (Modifikationen lesen); der Server weist das ab.
            IsReadModified = false,
            ReturnBounds = false,
        };

        var values = nodes.Select(_ => new List<DataValue>()).ToArray();
        var continuation = new byte[nodes.Count][];
        var pending = Enumerable.Range(0, nodes.Count).ToList();
        while (pending.Count > 0)
        {
            var request = new HistoryReadValueIdCollection(pending.Select(i => new HistoryReadValueId
            {
                NodeId = nodes[i],
                ContinuationPoint = continuation[i],
            }));
            var response = await session.HistoryReadAsync(
                null, new ExtensionObject(details), TimestampsToReturn.Source, false, request, cancellationToken);
            if (response.Results.Count != pending.Count)
                throw new InvalidOperationException($"HistoryRead lieferte {response.Results.Count} Ergebnisse für {pending.Count} Knoten.");

            var next = new List<int>();
            for (var k = 0; k < pending.Count; k++)
            {
                var index = pending[k];
                var result = response.Results[k];
                if (StatusCode.IsBad(result.StatusCode))
                    throw new ServiceResultException(result.StatusCode, $"HistoryRead {nodes[index]}: {result.StatusCode}");

                if (ExtensionObject.ToEncodeable(result.HistoryData) is HistoryData data) values[index].AddRange(data.DataValues);
                if (result.ContinuationPoint is { Length: > 0 } point)
                {
                    continuation[index] = point;
                    next.Add(index);
                }
            }

            pending = next;
        }

        return values;
    }
}
