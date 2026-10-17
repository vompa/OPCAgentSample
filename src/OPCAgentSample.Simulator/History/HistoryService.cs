using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulator.Model;

namespace OPCAgentSample.Simulator.History;

/// <summary>Ein Historienwert: Zeitpunkt des Datensatzes und Rohwert (double, int, string).</summary>
internal readonly record struct HistoryPoint(DateTimeOffset Timestamp, object Value);

/// <summary>
/// Liefert die Historie der Knoten aus dem <see cref="HistoryGenerator"/>. Zykluswerte werden je Rundtisch lazy erzeugt;
/// alle übrigen Datensätze werden in festen 6-Stunden-Abschnitten erzeugt und, sobald ein Abschnitt vollständig in der
/// Vergangenheit der Simulationsuhr liegt, nach Station und Wert gruppiert zwischengespeichert. Dadurch kostet das
/// Nachladen vieler Knoten über denselben Zeitraum den Generator nur einmal.
/// </summary>
internal sealed class HistoryService
{
    private const int MaxCachedSlices = 80;
    private static readonly TimeSpan s_lookahead = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_slice = TimeSpan.FromHours(6);

    private readonly HistoryGenerator _generator;
    private readonly ISimulationClock _clock;
    private readonly DateTimeOffset _origin;
    private readonly Lock _lock = new();
    private readonly Dictionary<long, SliceData> _cache = [];
    private readonly LinkedList<long> _usage = new();
    private PartialSlice? _partial;

    public HistoryService(HistoryGenerator generator, ISimulationClock clock)
    {
        _generator = generator;
        _clock = clock;
        _origin = generator.Scenario.HistoryStartUtc;
    }

    /// <summary>Beginn der Historie (Mo 05.10.2026 00:00 Ortszeit).</summary>
    public DateTimeOffset Start => _origin;

    /// <summary>Ende der Historie: die aktuelle Simulationszeit.</summary>
    public DateTimeOffset End => _clock.UtcNow;

    /// <summary>Werte in [<paramref name="from"/>, <paramref name="until"/>), zeitlich aufsteigend.</summary>
    public IEnumerable<HistoryPoint> Read(HistoryBinding binding, string sourceId, DateTimeOffset from, DateTimeOffset until)
    {
        from = from < _origin ? _origin : from;
        until = until > End ? End : until;
        if (from >= until) return [];

        return binding switch
        {
            CycleHistory cycle => _generator.Cycles(sourceId, from, until).Select(c => new HistoryPoint(c.TimestampUtc, cycle.Select(c))),
            MinuteHistory minute => Slices(from, until, sourceId, minute.Variable)
                .Select(r => new HistoryPoint(r.TimestampUtc, ((MinuteValue)r).Value)),
            StateHistory => Slices(from, until, sourceId, nameof(StateChange))
                .Select(r => new HistoryPoint(r.TimestampUtc, (int)((StateChange)r).State)),
            RecordHistory record => Slices(from, until, sourceId, record.RecordType.Name)
                .Select(r => new HistoryPoint(r.TimestampUtc, HistoryJson.Serialize(r))),
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, null),
        };
    }

    private IEnumerable<HistoryRecord> Slices(DateTimeOffset from, DateTimeOffset until, string stationId, string key)
    {
        var first = (long)Math.Floor((from - _origin) / s_slice);
        var last = (long)Math.Floor((until - _origin - TimeSpan.FromTicks(1)) / s_slice);
        for (var index = first; index <= last; index++)
        {
            if (!Slice(index).TryGetValue((stationId, key), out var records)) continue;
            foreach (var record in records)
            {
                if (record.TimestampUtc >= until) yield break;
                if (record.TimestampUtc >= from) yield return record;
            }
        }
    }

    private SliceData Slice(long index)
    {
        var start = _origin + (s_slice * index);
        var end = start + s_slice;

        lock (_lock)
        {
            if (end > End) return GrowPartial(index, start, end);

            if (_cache.TryGetValue(index, out var cached))
            {
                _usage.Remove(index);
                _usage.AddLast(index);
                return cached;
            }

            var data = CompletePartial(index, start, end) ?? Build(start, end);
            _cache[index] = data;
            _usage.AddLast(index);
            while (_cache.Count > MaxCachedSlices)
            {
                _cache.Remove(_usage.First!.Value);
                _usage.RemoveFirst();
            }

            return data;
        }
    }

    /// <summary>
    /// Der laufende Abschnitt (reicht bis in die Zukunft der Simulationsuhr) wächst mit der Uhr: es wird nur das
    /// neue Stück erzeugt und, kurz vorausgerechnet, an den bisherigen Stand angehängt. Jeder Stand ist unveränderlich,
    /// damit Leser ihn ohne Sperre durchlaufen können. Aufruf nur unter <see cref="_lock"/>.
    /// </summary>
    private SliceData GrowPartial(long index, DateTimeOffset start, DateTimeOffset end)
    {
        var now = End;
        var target = now + s_lookahead < end ? now + s_lookahead : end;
        if (_partial is null || _partial.Index != index) _partial = new PartialSlice(index, new SliceData(), start);
        if (target > _partial.BuiltUntil) _partial = Extend(_partial, target);
        return _partial.Data;
    }

    /// <summary>Der laufende Abschnitt ist vollständig vergangen: restlichen Teil ergänzen und übernehmen.</summary>
    private SliceData? CompletePartial(long index, DateTimeOffset start, DateTimeOffset end)
    {
        if (_partial is null || _partial.Index != index) return null;
        var completed = end > _partial.BuiltUntil ? Extend(_partial, end) : _partial;
        _partial = null;
        return completed.Data;
    }

    private PartialSlice Extend(PartialSlice slice, DateTimeOffset target)
    {
        var data = new SliceData();
        foreach (var (key, records) in slice.Data) data[key] = [.. records];
        Add(data, slice.BuiltUntil, target);
        return slice with { Data = data, BuiltUntil = target };
    }

    private SliceData Build(DateTimeOffset start, DateTimeOffset end)
    {
        var data = new SliceData();
        Add(data, start, end);
        return data;
    }

    private void Add(SliceData data, DateTimeOffset start, DateTimeOffset end)
    {
        if (start >= end) return;
        foreach (var record in _generator.Generate(start, end))
        {
            if (record is CycleRecord) continue;
            var key = record is MinuteValue minute ? minute.Variable : record.GetType().Name;
            if (!data.TryGetValue((record.StationId, key), out var list)) data[(record.StationId, key)] = list = [];
            list.Add(record);
        }
    }

    private sealed record PartialSlice(long Index, SliceData Data, DateTimeOffset BuiltUntil);

    private sealed class SliceData : Dictionary<(string Station, string Key), List<HistoryRecord>>;
}
