using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Opc.Ua;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Server.Ingestion;

namespace OPCAgentSample.Server.Collector;

/// <summary>
/// Historienquelle des Servers über OPC UA: liest die historisierten Knoten der Halle A per HistoryRead Raw (seitenweise,
/// je Station parallel) und bildet sie auf die Datensätze der Domäne ab. Das Ergebnis eines Fensters ist identisch mit
/// den Datensätzen des Generators, aus denen der Simulator die Knoten speist. Welche Knoten gelesen werden, legt der
/// gemeinsame Vertrag <see cref="HistoryNodeMap"/> fest; die NodeIds baut <see cref="NodeIdScheme"/>.
/// </summary>
/// <remarks>
/// Die Tageskennzahlen der Historie B (Quartal, Hallen B und C) liefert der OPC-Server nicht; sie sind nicht Teil dieser Quelle.
/// </remarks>
public sealed class OpcHistorySource(OpcSessionProvider sessions, IOptions<OpcOptions> options) : IHistorySource
{
    public const string Id = "opc-halle-a";

    public string SourceId => Id;

    /// <summary>
    /// Beginn der Historie laut Server. Der erste Zugriff wartet auf die erste Verbindung (höchstens
    /// <see cref="OpcOptions.ConnectWait"/>); danach ist der Wert bekannt und bleibt auch ohne Verbindung verfügbar.
    /// </summary>
    public DateTimeOffset HistoryStartUtc
    {
        get
        {
            if (sessions.HistoryStartUtc is { } known) return known;
            sessions.WaitForConnectionAsync(options.Value.ConnectWait, CancellationToken.None).GetAwaiter().GetResult();
            return sessions.HistoryStartUtc ?? throw new InvalidOperationException("Der OPC-Server hat den Beginn der Historie nicht geliefert.");
        }
    }

    public async IAsyncEnumerable<HistoryRecord> ReadAsync(
        DateTimeOffset fromUtc, DateTimeOffset untilUtc, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (fromUtc >= untilUtc) yield break;

        var connection = await sessions.WaitForConnectionAsync(options.Value.ConnectWait, cancellationToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Lost);
        List<HistoryRecord> records;
        try
        {
            records = await ReadWindowAsync(connection, fromUtc, untilUtc, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Die OPC-Sitzung ist während des Lesens der Historie ausgefallen.");
        }

        foreach (var record in records) yield return record;
    }

    private async Task<List<HistoryRecord>> ReadWindowAsync(
        OpcConnection connection, DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var limit = new SemaphoreSlim(settings.MaxParallelStations);
        var tasks = PlantLayout.HallAStations
            .Select(station => ReadLimitedAsync(limit, () => ReadStationAsync(connection, station, fromUtc, untilUtc, cancellationToken), cancellationToken))
            .Append(ReadLimitedAsync(limit, () => ReadClimateAsync(connection, fromUtc, untilUtc, cancellationToken), cancellationToken))
            .ToList();
        var parts = await Task.WhenAll(tasks);

        // Reihenfolge der Quellen ist fest, die Sortierung nach Zeit stabil: dasselbe Fenster liefert immer dieselbe Folge.
        return [.. parts.SelectMany(p => p).OrderBy(r => r.TimestampUtc)];
    }

    private static async Task<List<HistoryRecord>> ReadLimitedAsync(
        SemaphoreSlim limit, Func<Task<List<HistoryRecord>>> read, CancellationToken cancellationToken)
    {
        await limit.WaitAsync(cancellationToken);
        try
        {
            return await read();
        }
        finally
        {
            limit.Release();
        }
    }

    private async Task<List<HistoryRecord>> ReadStationAsync(
        OpcConnection connection, Station station, DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken)
    {
        var cycleNodes = station.Type == StationType.RotaryTable ? HistoryNodeMap.CycleNodes : [];
        var minuteNodes = HistoryNodeMap.MinuteNodes(station.Type);
        var eventNodes = HistoryNodeMap.EventNodes(station.Type);

        var ids = new List<NodeId>();
        ids.AddRange(cycleNodes.Select(n => connection.Node(NodeIdScheme.Variable(station, n.Group, n.Variable))));
        ids.AddRange(minuteNodes.Select(n => connection.Node(NodeIdScheme.Variable(station, n.Group, n.Variable))));
        ids.AddRange(eventNodes.Select(n => connection.Node(NodeIdScheme.Variable(station, StationGroups.Events, n.Variable))));
        var values = await OpcHistoryClient.ReadRawAsync(connection.Session, ids, fromUtc, untilUtc, options.Value.HistoryPageSize, cancellationToken);

        var records = new List<HistoryRecord>();
        var next = 0;
        if (cycleNodes.Count > 0)
        {
            records.AddRange(OpcRecordMapper.Cycles(station.Id, values[next..(next + cycleNodes.Count)]));
            next += cycleNodes.Count;
        }

        foreach (var node in minuteNodes) records.AddRange(OpcRecordMapper.Minutes(station.Id, node.MinuteVariable, values[next++]));
        foreach (var node in eventNodes) records.AddRange(OpcRecordMapper.Events(node, values[next++]));
        return records;
    }

    private async Task<List<HistoryRecord>> ReadClimateAsync(
        OpcConnection connection, DateTimeOffset fromUtc, DateTimeOffset untilUtc, CancellationToken cancellationToken)
    {
        var ids = HistoryNodeMap.ClimateNodes.Select(n => connection.Node(NodeIdScheme.ClimateVariable(n.Variable))).ToList();
        var values = await OpcHistoryClient.ReadRawAsync(connection.Session, ids, fromUtc, untilUtc, options.Value.HistoryPageSize, cancellationToken);

        var records = new List<HistoryRecord>();
        for (var i = 0; i < ids.Count; i++)
        {
            records.AddRange(OpcRecordMapper.Minutes(MinuteVariables.HallAClimateSource, HistoryNodeMap.ClimateNodes[i].MinuteVariable, values[i]));
        }

        return records;
    }
}
