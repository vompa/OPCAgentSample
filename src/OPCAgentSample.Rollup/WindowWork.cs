using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Rollup;

/// <summary>Alles, was ein Aufnahmefenster zusätzlich zu den Summen schreibt; wird beim Durchlauf der Datensätze gesammelt.</summary>
internal sealed class WindowWork
{
    /// <summary>Neuester Stand je Verlustereignis (Beginn- und Endemeldung ersetzen sich).</summary>
    public Dictionary<string, LossEvent> Losses { get; } = [];

    public Dictionary<string, AlarmRecord> Alarms { get; } = [];

    public List<StateChange> States { get; } = [];

    public List<OrderEvent> Orders { get; } = [];

    public List<PressLot> Lots { get; } = [];

    public List<OvenCharge> Charges { get; } = [];

    public List<PalletRecord> Pallets { get; } = [];

    /// <summary>Takte je Rundtisch in zeitlicher Reihenfolge (für den Ringpuffer).</summary>
    public Dictionary<string, List<CycleRecord>> Cycles { get; } = [];

    /// <summary>Minutenwerte je Station und Variable in zeitlicher Reihenfolge (für die Minutenreihen).</summary>
    public Dictionary<(string Station, string Variable), List<MinuteValue>> Minutes { get; } = [];

    /// <summary>Takte je Auftrag in diesem Fenster und die Station, an der sie entstanden.</summary>
    public Dictionary<string, (string Station, long Cycles)> OrderCycles { get; } = [];

    /// <summary>Zuletzt gezählter Artikel je Verpackung in diesem Fenster.</summary>
    public Dictionary<string, string> PackagingArticles { get; } = [];

    /// <summary>Takte je Pressform in diesem Fenster.</summary>
    public Dictionary<string, long> MouldCycles { get; } = [];

    /// <summary>Tage, deren Zähler in diesem Fenster neu zu summieren sind (Tag → Stationen).</summary>
    public Dictionary<string, HashSet<string>> DirtyDays { get; } = [];

    /// <summary>Beginnzeitpunkte der Ereignisse, die vor der laufenden Schicht begonnen haben (lösen die Neuberechnung früherer Schichten aus).</summary>
    public List<DateTimeOffset> EarlierEventStarts { get; } = [];

    /// <summary>Erste Hallentemperatur dieses Fensters (Wert zu Schichtbeginn, falls es die erste der Schicht ist).</summary>
    public double? FirstClimateTemperature { get; set; }

    public void MarkDirtyDay(string dayKey, string station)
    {
        if (!DirtyDays.TryGetValue(dayKey, out var stations)) DirtyDays[dayKey] = stations = [];
        stations.Add(station);
    }
}
