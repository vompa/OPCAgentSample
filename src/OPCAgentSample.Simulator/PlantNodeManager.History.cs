using Opc.Ua;
using Opc.Ua.Server;
using OPCAgentSample.Simulator.History;
using OPCAgentSample.Simulator.Model;
using OPCAgentSample.Simulator.Observability;

namespace OPCAgentSample.Simulator;

// HistoryRead (Raw) für die historisierten Variablen. Seitenweises Lesen über ContinuationPoints.
internal sealed partial class PlantNodeManager
{
    protected override void HistoryReadRawModified(
        ServerSystemContext context,
        ReadRawModifiedDetails details,
        TimestampsToReturn timestampsToReturn,
        IList<HistoryReadValueId> nodesToRead,
        IList<HistoryReadResult> results,
        IList<ServiceResult> errors,
        List<NodeHandle> nodesToProcess,
        IDictionary<NodeId, NodeState> cache)
    {
        // Ein Span je HistoryRead-Dienstaufruf (nicht je Knoten und Seite, das wären hunderte).
        using var span = SimulatorTelemetry.Source.StartActivity(SimulatorTelemetry.HistoryReadSpan);
        span?.SetTag("opc.nodes", nodesToProcess.Count);
        foreach (var handle in nodesToProcess)
        {
            var source = ValidateNode(context, handle, cache);
            if (source is null)
            {
                errors[handle.Index] = StatusCodes.BadNodeIdUnknown;
                continue;
            }

            if (details.IsReadModified || !_history.TryGetValue(handle.NodeId.Identifier as string ?? string.Empty, out var entry))
            {
                errors[handle.Index] = StatusCodes.BadHistoryOperationUnsupported;
                continue;
            }

            errors[handle.Index] = ReadPage(context, details, entry, nodesToRead[handle.Index], results[handle.Index]);
        }
    }

    protected override void HistoryReleaseContinuationPoints(
        ServerSystemContext context,
        IList<HistoryReadValueId> nodesToRead,
        IList<ServiceResult> errors,
        List<NodeHandle> nodesToProcess,
        IDictionary<NodeId, NodeState> cache)
    {
        foreach (var handle in nodesToProcess)
        {
            var point = nodesToRead[handle.Index].ContinuationPoint;
            if (point is { Length: 16 }) context.OperationContext.Session.RestoreHistoryContinuationPoint(point);
            errors[handle.Index] = ServiceResult.Good;
        }
    }

    private ServiceResult ReadPage(
        ServerSystemContext context, ReadRawModifiedDetails details, HistoryEntry entry, HistoryReadValueId request, HistoryReadResult result)
    {
        HistoryCursor cursor;
        if (request.ContinuationPoint is { Length: > 0 } continuation)
        {
            // ContinuationPoints sind Guids (16 Byte); alles andere ist ungültig.
            if (continuation.Length != Guid.Empty.ToByteArray().Length
                || context.OperationContext.Session.RestoreHistoryContinuationPoint(continuation) is not HistoryCursor restored)
                return StatusCodes.BadContinuationPointInvalid;
            cursor = restored;
        }
        else
        {
            if (details.StartTime == DateTime.MinValue && details.EndTime == DateTime.MinValue) return StatusCodes.BadInvalidArgument;
            cursor = NewCursor(details);
        }

        var limit = ValueLimit(details.NumValuesPerNode);
        var points = Enumerate(entry, cursor).Take(limit + 1).ToList();
        var page = points.Take(limit).ToList();

        var values = new DataValueCollection(page.Count);
        foreach (var point in page) values.Add(ToDataValue(entry.Kind, point));
        result.HistoryData = new ExtensionObject(new HistoryData { DataValues = values });
        result.StatusCode = StatusCodes.Good;
        SimulatorTelemetry.HistoryRead(page.Count);
        result.ContinuationPoint = points.Count > limit ? SaveContinuation(context, cursor, page) : null;
        return ServiceResult.Good;
    }

    private int ValueLimit(uint requested) =>
        requested == 0 ? _runtime.Options.MaxValuesPerRead : (int)Math.Min(requested, (uint)_runtime.Options.MaxValuesPerRead);

    /// <summary>
    /// Fenster [Von, Bis) aus Start- und Endzeit. Endet das Fenster vor dem Start oder fehlt der Start, wird rückwärts
    /// gelesen (neueste Werte zuerst); fehlt die Endzeit, gilt die aktuelle Simulationszeit.
    /// </summary>
    private HistoryCursor NewCursor(ReadRawModifiedDetails details)
    {
        var unspecifiedStart = details.StartTime == DateTime.MinValue;
        var unspecifiedEnd = details.EndTime == DateTime.MinValue;
        var start = unspecifiedStart ? _runtime.History.Start : ToOffset(details.StartTime);
        var end = unspecifiedEnd ? _runtime.History.End : ToOffset(details.EndTime);
        var reverse = unspecifiedStart || (!unspecifiedEnd && end < start);
        if (!reverse) return new HistoryCursor(false, start, end, 0);

        var tick = TimeSpan.FromTicks(1);
        return unspecifiedStart
            ? new HistoryCursor(true, start, end + tick, 0)
            : new HistoryCursor(true, end + tick, start + tick, 0);
    }

    private IEnumerable<HistoryPoint> Enumerate(HistoryEntry entry, HistoryCursor cursor)
    {
        var points = _runtime.History.Read(entry.Binding, entry.SourceId, cursor.From, cursor.Until);

        // Vorwärts liegen die bereits gelieferten Werte mit dem Zeitstempel des Fensterbeginns am Anfang; rückwärts die ersten Werte.
        return cursor.Reverse ? points.Reverse().Skip(cursor.Skip) : points.Skip(cursor.Skip);
    }

    private static byte[] SaveContinuation(ServerSystemContext context, HistoryCursor cursor, List<HistoryPoint> page)
    {
        HistoryCursor next;
        if (cursor.Reverse)
        {
            next = cursor with { Skip = cursor.Skip + page.Count };
        }
        else
        {
            var last = page[^1].Timestamp;
            var sameTimestamp = page.Count(point => point.Timestamp == last);
            next = cursor with { From = last, Skip = (last == cursor.From ? cursor.Skip : 0) + sameTimestamp };
        }

        var id = Guid.NewGuid();
        context.OperationContext.Session.SaveHistoryContinuationPoint(id, next);
        return id.ToByteArray();
    }

    private static DataValue ToDataValue(ValueKind kind, HistoryPoint point)
    {
        var timestamp = point.Timestamp.UtcDateTime;
        return new DataValue
        {
            WrappedValue = new Variant(ValueMapper.ToNodeValue(kind, point.Value)),
            StatusCode = StatusCodes.Good,
            SourceTimestamp = timestamp,
            ServerTimestamp = timestamp,
        };
    }

    private static DateTimeOffset ToOffset(DateTime utc) => new(DateTime.SpecifyKind(utc.ToUniversalTime(), DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>Lesestand eines Historienabrufs; wird als ContinuationPoint in der Sitzung abgelegt.</summary>
    private sealed record HistoryCursor(bool Reverse, DateTimeOffset From, DateTimeOffset Until, int Skip);
}
