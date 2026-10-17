using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Simulator.Model;

namespace OPCAgentSample.Simulator.Live;

/// <summary>
/// Laufender Stand, der sich nur aus dem Datensatzstrom des Generators ergibt (letzter Takt, letzte Minutenwerte,
/// Zähler seit Schichtbeginn, Störzähler, offene Verluste, Alarmstände). Beim Start wird die Historie bis zur Startzeit
/// eingespielt, danach jedes Zeitfenster der Simulation. Nicht threadsicher: nur die Aktualisierungsschleife schreibt.
/// </summary>
internal sealed class PlantState
{
    private readonly ShiftCalendar _calendar;
    private readonly Dictionary<string, StationAccumulator> _stations = [];
    private readonly Dictionary<(string Station, string Variable), MinuteValue> _minutes = [];
    private readonly Dictionary<(string Station, string Kind), HistoryRecord> _lastRecords = [];
    private readonly Dictionary<string, AlarmRecord> _alarms = [];
    private readonly List<AlarmRecord> _alarmChanges = [];
    private ProductionPeriod? _shift;

    public PlantState(ShiftCalendar calendar)
    {
        _calendar = calendar;
    }

    /// <summary>Sammelt Alarmänderungen für die Meldungen (erst nach dem Einspielen der Historie).</summary>
    public bool RecordAlarmChanges { get; set; }

    public IReadOnlyCollection<AlarmRecord> LatestAlarms => _alarms.Values;

    public StationAccumulator Station(string stationId)
    {
        if (!_stations.TryGetValue(stationId, out var accumulator)) _stations[stationId] = accumulator = new StationAccumulator();
        return accumulator;
    }

    public MinuteValue? Minute(string stationId, string variable) =>
        _minutes.TryGetValue((stationId, variable), out var value) ? value : null;

    /// <summary>Letzter Datensatz der Art <paramref name="kind"/> (Typname) an der Station.</summary>
    public HistoryRecord? LastRecord(string stationId, string kind) =>
        _lastRecords.TryGetValue((stationId, kind), out var record) ? record : null;

    public int ActiveCount(string stationId, bool alarms) =>
        _alarms.Values.Count(a => a.StationId == stationId && a.ClearedUtc is null && a.IsAlarm == alarms);

    /// <summary>Liefert die seit dem letzten Aufruf angefallenen Alarmstände und leert die Liste.</summary>
    public IReadOnlyList<AlarmRecord> DrainAlarmChanges()
    {
        if (_alarmChanges.Count == 0) return [];
        var drained = _alarmChanges.ToList();
        _alarmChanges.Clear();
        return drained;
    }

    public void Ingest(HistoryRecord record)
    {
        switch (record)
        {
            case CycleRecord cycle:
                IngestCycle(cycle);
                return;
            case MinuteValue minute:
                _minutes[(minute.StationId, minute.Variable)] = minute;
                return;
            case StateChange change:
                IngestStateChange(change);
                break;
            case LossEvent loss:
                IngestLoss(loss);
                break;
            case AlarmRecord alarm:
                _alarms[alarm.Id] = alarm;
                if (RecordAlarmChanges) _alarmChanges.Add(alarm);
                break;
            case UnitCountRecord units:
                Station(units.StationId).TotalCycles += units.Good + units.Scrap;
                break;
        }

        _lastRecords[(record.StationId, record.GetType().Name)] = record;
    }

    private void IngestCycle(CycleRecord cycle)
    {
        if (_shift is null || !_shift.Contains(cycle.TimestampUtc)) _shift = _calendar.ShiftPeriodOf(cycle.TimestampUtc);
        var station = Station(cycle.StationId);
        if (station.ShiftKey != _shift.Key) station.StartShift(_shift.Key);
        station.Last = cycle;
        station.TotalCycles++;
        if (cycle.Scrap is { } reason) station.ScrapByReason[(int)reason]++;
        if (cycle.OrderId is { } order) station.OrderCycles[order] = station.OrderCycles.GetValueOrDefault(order) + 1;
    }

    private void IngestStateChange(StateChange change)
    {
        var station = Station(change.StationId);
        station.AccumulateRunning(change.TimestampUtc);
        station.State = change.State;
        station.StateSince = change.TimestampUtc;
    }

    private void IngestLoss(LossEvent loss)
    {
        var station = Station(loss.StationId);
        if (loss.Category is LossCategory.Breakdown or LossCategory.MinorStop && station.SeenLosses.Add(loss.Id))
        {
            station.FaultCounts[loss.Assembly] = station.FaultCounts.GetValueOrDefault(loss.Assembly) + 1;
        }

        if (loss.IsOpen) station.OpenLosses[loss.Id] = loss;
        else station.OpenLosses.Remove(loss.Id);
    }
}

/// <summary>Zähler und Merker einer Station.</summary>
internal sealed class StationAccumulator
{
    public CycleRecord? Last { get; set; }

    public long TotalCycles { get; set; }

    public string ShiftKey { get; private set; } = string.Empty;

    /// <summary>Ausschuss der laufenden Schicht je <see cref="ScrapReason"/> (Index = Wert des Enums).</summary>
    public int[] ScrapByReason { get; private set; } = new int[Enum.GetValues<ScrapReason>().Length];

    public Dictionary<string, int> OrderCycles { get; } = [];

    public HashSet<string> SeenLosses { get; } = [];

    public Dictionary<string, int> FaultCounts { get; } = [];

    public Dictionary<string, LossEvent> OpenLosses { get; } = [];

    public OperatingState? State { get; set; }

    public DateTimeOffset StateSince { get; set; }

    public TimeSpan RunningTime { get; private set; }

    public void StartShift(string shiftKey)
    {
        ShiftKey = shiftKey;
        ScrapByReason = new int[ScrapByReason.Length];
    }

    /// <summary>Addiert die Dauer des bisherigen Zustands, falls die Station lief.</summary>
    public void AccumulateRunning(DateTimeOffset until)
    {
        if (State is OperatingState.Running or OperatingState.RunningWithoutOrder) RunningTime += until - StateSince;
    }

    /// <summary>Betriebszeit bis <paramref name="now"/> einschließlich des laufenden Zustands.</summary>
    public TimeSpan RunningTimeAt(DateTimeOffset now) =>
        State is OperatingState.Running or OperatingState.RunningWithoutOrder ? RunningTime + (now - StateSince) : RunningTime;

    public bool HasOpenLoss(string assembly) => OpenLosses.Values.Any(l => l.Assembly == assembly);
}
