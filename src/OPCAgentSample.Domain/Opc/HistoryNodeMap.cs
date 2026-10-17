using OPCAgentSample.Domain.History;
using G = OPCAgentSample.Domain.Opc.StationGroups;
using V = OPCAgentSample.Domain.Opc.StationVariables;
using E = OPCAgentSample.Domain.Opc.EventVariables;

namespace OPCAgentSample.Domain.Opc;

/// <summary>Historisierte Variable, deren Wert je Takt (<see cref="CycleRecord"/>) oder je Minute vorliegt.</summary>
public sealed record HistoryNode(string Group, string Variable);

/// <summary>Minutenwert-Variable (<see cref="MinuteValue.Variable"/> = <paramref name="MinuteVariable"/>).</summary>
public sealed record MinuteNode(string Group, string Variable, string MinuteVariable);

/// <summary>Ereignis-Variable der Gruppe <see cref="StationGroups.Events"/> mit dem Typ des Datensatzes im JSON-Wert.</summary>
public sealed record EventNode(string Variable, Type RecordType);

/// <summary>
/// Welche Knoten einer Station historisiert sind und auf welche Datensätze der Domäne sie abgebildet werden. Gemeinsamer
/// Vertrag: Der Simulator baut seine historisierten Variablen daraus, der Collector liest genau diese Knoten per HistoryRead.
/// </summary>
public static class HistoryNodeMap
{
    /// <summary>Betriebszustand der Station (Wert = <see cref="OperatingState"/> als Zahl); die Zustandswechsel mit Auftrag stehen in den Ereignissen.</summary>
    public static HistoryNode State { get; } = new(G.State, V.OperatingState);

    /// <summary>
    /// Takt-Variablen eines Rundtischs (alle Werte eines Takts tragen denselben Zeitstempel): Prozesswerte und Zyklus-Metadaten.
    /// </summary>
    public static IReadOnlyList<HistoryNode> CycleNodes { get; } =
    [
        new(G.Process, V.CycleTime), new(G.Process, V.PressForce), new(G.Process, V.PressForceBuildUp),
        new(G.Process, V.FillWeight), new(G.Process, V.Thickness),
        new(G.Cycle, V.CycleTargetTime), new(G.Cycle, V.CycleOrderId), new(G.Cycle, V.CycleArticleId),
        new(G.Cycle, V.CycleMixtureBatch), new(G.Cycle, V.CycleScrapReason),
    ];

    /// <summary>Ereignis-Variablen aller Stationen.</summary>
    public static IReadOnlyList<EventNode> CommonEventNodes { get; } =
    [
        new(E.Losses, typeof(LossEvent)), new(E.Alarms, typeof(AlarmRecord)),
        new(E.StateChanges, typeof(StateChange)), new(E.ShiftPlans, typeof(ShiftPlanRecord)),
    ];

    private static readonly MinuteNode[] s_counters =
    [
        new(G.Counters, V.GoodPieces, MinuteVariables.GoodCount),
        new(G.Counters, V.ScrapPieces, MinuteVariables.ScrapCount),
        new(G.Energy, V.EnergyKwh, MinuteVariables.EnergyKwh),
    ];

    private static readonly MinuteNode[] s_rotaryTable =
    [
        new(G.Process, V.MouldTemperature, MinuteVariables.MouldTemperature),
        new(G.Material, V.MixtureLevelKg, MinuteVariables.MixtureLevelKg),
        new(G.Material, V.FabricMagazine, MinuteVariables.FabricMagazine),
        new(G.Material, V.LabelMagazine, MinuteVariables.LabelMagazine),
        new(G.Hydraulics, V.OilTemperature, MinuteVariables.OilTemperature),
        new(G.Hydraulics, V.SystemPressure, MinuteVariables.SystemPressure),
    ];

    private static readonly MinuteNode[] s_oven =
    [
        new(G.Capacity, V.OccupiedPlaces, MinuteVariables.OccupiedPlaces),
        new(G.Temperature, V.Zone1Actual, MinuteVariables.Zone1Temperature),
        new(G.Temperature, V.Zone2Actual, MinuteVariables.Zone2Temperature),
        new(G.Temperature, V.Zone3Actual, MinuteVariables.Zone3Temperature),
    ];

    private static readonly MinuteNode[] s_buffer =
    [
        new(G.Inventory, V.PlateCount, MinuteVariables.PlateCount),
        new(G.Inventory, V.MeanWait, MinuteVariables.MeanWaitMinutes),
        new(G.Inventory, V.OldestWait, MinuteVariables.OldestWaitMinutes),
    ];

    private static readonly MinuteNode[] s_packaging =
    [
        new(G.Packaging, V.CheckweigherRejects, MinuteVariables.CheckweigherRejects),
        new(G.Packaging, V.CartonMagazine, MinuteVariables.CartonMagazine),
        new(G.Packaging, V.LabelMagazine, MinuteVariables.LabelMagazine),
    ];

    private static readonly EventNode[] s_orderEvents = [new(E.OrderEvents, typeof(OrderEvent))];

    private static readonly EventNode[] s_rotaryTableEvents = [.. s_orderEvents, new(E.PressLots, typeof(PressLot))];

    private static readonly EventNode[] s_ovenEvents = [new(E.OvenCharges, typeof(OvenCharge))];

    private static readonly EventNode[] s_packagingEvents =
        [.. s_orderEvents, new(E.Pallets, typeof(PalletRecord)), new(E.UnitCounts, typeof(UnitCountRecord))];

    /// <summary>Minutenwert-Variablen der Halle-A-Klimamessung (NodeId <see cref="NodeIdScheme.ClimateVariable"/>, Quelle <see cref="MinuteVariables.HallAClimateSource"/>).</summary>
    public static IReadOnlyList<(string Variable, string MinuteVariable)> ClimateNodes { get; } =
    [
        (V.Temperature, MinuteVariables.Temperature),
        (V.Humidity, MinuteVariables.Humidity),
    ];

    /// <summary>Minutenwert-Variablen einer Station ihres Typs (Zähler und Energie nur bei Rundtisch und Verpackung).</summary>
    public static IReadOnlyList<MinuteNode> MinuteNodes(StationType type) => type switch
    {
        StationType.RotaryTable => [.. s_counters, .. s_rotaryTable],
        StationType.Oven => s_oven,
        StationType.Buffer => s_buffer,
        StationType.Packaging => [.. s_counters, .. s_packaging],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Ereignis-Variablen einer Station ihres Typs, einschließlich der gemeinsamen.</summary>
    public static IReadOnlyList<EventNode> EventNodes(StationType type) => type switch
    {
        StationType.RotaryTable => [.. CommonEventNodes, .. s_rotaryTableEvents],
        StationType.Oven => [.. CommonEventNodes, .. s_ovenEvents],
        StationType.Buffer => CommonEventNodes,
        StationType.Packaging => [.. CommonEventNodes, .. s_packagingEvents],
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Ereignis-Variablen, die ein Typ zusätzlich zu <see cref="CommonEventNodes"/> hat.</summary>
    public static IReadOnlyList<EventNode> ExtraEventNodes(StationType type) => type switch
    {
        StationType.RotaryTable => s_rotaryTableEvents,
        StationType.Oven => s_ovenEvents,
        StationType.Buffer => [],
        StationType.Packaging => s_packagingEvents,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Name des Minutenwerts für eine Variable (Gruppe und Variable aus dem Typ der Station); Fehler, wenn sie keine hat.</summary>
    public static string MinuteVariableOf(string group, string variable)
    {
        var nodes = Enum.GetValues<StationType>().SelectMany(MinuteNodes);
        return nodes.FirstOrDefault(n => n.Group == group && n.Variable == variable)?.MinuteVariable
            ?? throw new ArgumentException($"{group}.{variable} ist kein Minutenwert.", nameof(variable));
    }
}
