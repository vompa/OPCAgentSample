using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using V = OPCAgentSample.Domain.Opc.StationVariables;
using G = OPCAgentSample.Domain.Opc.StationGroups;

namespace OPCAgentSample.Simulator.Model;

/// <summary>
/// Die ObjectTypes des Informationsmodells (Spezifikation Abschnitt 4) als Daten: Aus ihnen entstehen sowohl die
/// Typ-Deklarationen unter <c>Typen</c> als auch die Instanzen der Stationen.
/// </summary>
internal static class PlantTypes
{
    public const string StationTypeName = "StationType";
    public const string RotaryTableTypeName = "RundtischType";
    public const string OvenTypeName = "OfenType";
    public const string BufferTypeName = "PufferType";
    public const string PackagingTypeName = "VerpackungType";

    /// <summary>Namen der Teilstationen eines Rundtischs und die zugehörige Baugruppe der Verlustereignisse.</summary>
    public static readonly IReadOnlyList<(string Name, string Assembly)> SubStations =
    [
        ("Gewebezufuehrung1", Assemblies.FabricFeed1),
        ("Gewebezufuehrung2", Assemblies.FabricFeed2),
        ("Dosierung", Assemblies.Dosing),
        ("Abstreifer", Assemblies.Stripper),
        ("Etikett", Assemblies.LabelFeed),
        ("Presse", Assemblies.Press),
        ("Ausstoss", Assemblies.Ejector),
    ];

    public static IReadOnlyList<TypeSpec> All => [Station, RotaryTable, Oven, Buffer, Packaging];

    public static TypeSpec For(Domain.StationType type) => type switch
    {
        Domain.StationType.RotaryTable => RotaryTable,
        Domain.StationType.Oven => Oven,
        Domain.StationType.Buffer => Buffer,
        Domain.StationType.Packaging => Packaging,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static TypeSpec Get(string name) => All.First(t => t.Name == name);

    /// <summary>Gruppen eines Typs einschließlich der Basistypen; gleichnamige Gruppen werden zusammengeführt.</summary>
    public static IReadOnlyList<GroupSpec> InstanceGroups(TypeSpec type, bool includeOptional)
    {
        var chain = new List<TypeSpec>();
        for (var current = type; current is not null; current = current.BaseName is null ? null : Get(current.BaseName))
        {
            chain.Insert(0, current);
        }

        var groups = new List<GroupSpec>();
        foreach (var group in chain.SelectMany(t => t.Groups))
        {
            if (group.Optional && !includeOptional) continue;
            var index = groups.FindIndex(g => g.Name == group.Name);
            if (index < 0) groups.Add(group);
            else groups[index] = groups[index] with { Variables = [.. groups[index].Variables, .. group.Variables] };
        }

        return groups;
    }

    private static readonly TypeSpec Station = new(StationTypeName, null,
        "Basis aller Stationen: Identität, Zustand, Aufträge, Zähler, Energie, Ereignisse und Alarme.",
        [
            Group(G.Identity, false,
                Text(V.StationId), Text(V.Designation), Text(V.AreaName), Text(V.Manufacturer), Number(V.YearOfConstruction, ValueKind.Int32)),
            Group(G.State, false,
                new(V.OperatingState, ValueKind.OperatingState, null, new StateHistory()),
                Number(V.StateSince, ValueKind.DateTime),
                Text(V.MachineryItemState)),
            Group(G.Order, true,
                Text(V.OrderId), Text(V.ArticleId), Number(V.OrderQuantity, ValueKind.Int32), Number(V.OrderActual, ValueKind.Int32),
                Number(V.TargetCycle, ValueKind.Double, Units.Second), Number(V.ShiftPlanQuantity, ValueKind.Int32)),
            Group(G.Counters, true,
                Minute(V.GoodPieces, ValueKind.Int32, null),
                Minute(V.ScrapPieces, ValueKind.Int32, null),
                Number(V.OperatingHours, ValueKind.Double, Units.Hour),
                Number(V.TotalCycles, ValueKind.Int64)),
            Group(G.Energy, true,
                Minute(V.EnergyKwh, ValueKind.Double, Units.KiloWattHour)),
            Group(G.Events, false, EventVariableSpecs(HistoryNodeMap.CommonEventNodes)),
            Group(G.Alarms, false, Number(V.ActiveAlarms, ValueKind.Int32), Number(V.ActiveMessages, ValueKind.Int32)),
        ]);

    private static readonly TypeSpec RotaryTable = new(RotaryTableTypeName, StationTypeName,
        "Rundtisch-Presse für Trennscheiben mit Prozess-, Material-, Werkzeug- und Hydraulikwerten.",
        [
            Group(G.Process, false,
                Cycle(V.CycleTime, Units.Second, c => c.CycleTimeSeconds),
                Number(V.CycleTimeMean, ValueKind.Double, Units.Second),
                Number(V.CycleTimeTarget, ValueKind.Double, Units.Second),
                Cycle(V.PressForce, Units.KiloNewton, c => c.PressForceKn),
                Cycle(V.PressForceBuildUp, Units.Second, c => c.PressForceBuildUpSeconds),
                Number(V.PressTime, ValueKind.Double, Units.Second),
                Minute(V.MouldTemperature, ValueKind.Double, Units.Celsius),
                Cycle(V.FillWeight, Units.Gram, c => c.FillWeightGrams),
                Number(V.FillWeightMean, ValueKind.Double, Units.Gram),
                Number(V.FillWeightTarget, ValueKind.Double, Units.Gram),
                Number(V.FillWeightTolerance, ValueKind.Double, Units.Gram),
                Cycle(V.Thickness, Units.Millimetre, c => c.ThicknessMm)),
            Group(G.Cycle, false,
                Cycle(V.CycleTargetTime, Units.Second, c => c.TargetCycleSeconds),
                new(V.CycleOrderId, ValueKind.String, null, new CycleHistory(c => c.OrderId ?? string.Empty)),
                new(V.CycleArticleId, ValueKind.String, null, new CycleHistory(c => c.ArticleId)),
                new(V.CycleMixtureBatch, ValueKind.String, null, new CycleHistory(c => c.MixtureBatchId)),
                new(V.CycleScrapReason, ValueKind.ScrapGrade, null, new CycleHistory(c => c.Scrap is { } s ? (int)s + 1 : 0))),
            Group(G.SubStations, false, [.. SubStations.SelectMany(s => new[]
            {
                Text(s.Name + V.StateSuffix),
                Number(s.Name + V.FaultCountSuffix, ValueKind.Int32),
            })]),
            Group(G.Material, false,
                Text(V.Mixture), Text(V.BatchId),
                Minute(V.MixtureLevelKg, ValueKind.Double, Units.Kilogram),
                Number(V.ProcessableUntil, ValueKind.DateTime),
                Minute(V.FabricMagazine, ValueKind.Int32, Units.Piece),
                Minute(V.LabelMagazine, ValueKind.Int32, Units.Piece)),
            Group(G.Tool, false,
                Text(V.MouldId), Number(V.StrokesSinceChange, ValueKind.Int32), Number(V.StrokeLimit, ValueKind.Int32)),
            Group(G.Hydraulics, false,
                Minute(V.OilTemperature, ValueKind.Double, Units.Celsius),
                Minute(V.SystemPressure, ValueKind.Double, Units.Bar)),
            Group(G.ScrapReasons, false,
                Number(V.ScrapWeight, ValueKind.Int32), Number(V.ScrapThickness, ValueKind.Int32), Number(V.ScrapFabric, ValueKind.Int32),
                Number(V.ScrapLabel, ValueKind.Int32), Number(V.ScrapBreakage, ValueKind.Int32)),
            Group(G.Events, false, EventVariableSpecs(HistoryNodeMap.ExtraEventNodes(Domain.StationType.RotaryTable))),
        ]);

    private static readonly TypeSpec Oven = new(OvenTypeName, StationTypeName,
        "Aushärteofen mit Chargenplätzen, Temperaturzonen, Zuordnungsregel und Sicherheitsdaten.",
        [
            Group(G.Capacity, false,
                Number(V.Places, ValueKind.Int32),
                Minute(V.OccupiedPlaces, ValueKind.Int32, null),
                Number(V.Occupancy, ValueKind.Double, Units.Percent),
                Number(V.OccupancyNow, ValueKind.Double, Units.Percent)),
            Group(G.ActiveCharge, false,
                Text(V.ChargeId), Number(V.ChargeDiameter, ValueKind.Int32, Units.Millimetre), Number(V.ChargePlates, ValueKind.Int32),
                Text(V.ProfileStep), Number(V.RemainingMinutes, ValueKind.Double, Units.Minute)),
            Group(G.Temperature, false,
                Minute(V.Zone1Actual, ValueKind.Double, Units.Celsius),
                Minute(V.Zone2Actual, ValueKind.Double, Units.Celsius),
                Minute(V.Zone3Actual, ValueKind.Double, Units.Celsius),
                Number(V.Zone1Target, ValueKind.Double, Units.Celsius),
                Number(V.Zone2Target, ValueKind.Double, Units.Celsius),
                Number(V.Zone3Target, ValueKind.Double, Units.Celsius)),
            Group(G.Planning, false, Number(V.NextRelease, ValueKind.DateTime), Text(V.AssignmentRule)),
            Group(G.Safety, false, Text(V.DoorLockState), Number(V.LastInspection, ValueKind.DateTime)),
            Group(G.Events, false, EventVariableSpecs(HistoryNodeMap.ExtraEventNodes(Domain.StationType.Oven))),
        ]);

    private static readonly TypeSpec Buffer = new(BufferTypeName, StationTypeName,
        "Ofenplatten-Puffer zwischen Pressen und Öfen.",
        [
            Group(G.Inventory, false,
                Number(V.BufferCapacity, ValueKind.Int32),
                Minute(V.PlateCount, ValueKind.Int32, null),
                Minute(V.MeanWait, ValueKind.Double, Units.Minute),
                Minute(V.OldestWait, ValueKind.Double, Units.Minute)),
        ]);

    private static readonly TypeSpec Packaging = new(PackagingTypeName, StationTypeName,
        "Verpackstation mit Kartontyp, Magazinen, Kontrollwaage und Umreifer.",
        [
            Group(G.Packaging, false,
                Text(V.CartonType), Number(V.PiecesPerCarton, ValueKind.Int32),
                Number(V.CartonsActual, ValueKind.Int32), Number(V.CartonsTarget, ValueKind.Double),
                Minute(V.CheckweigherRejects, ValueKind.Int32, null),
                Minute(V.CartonMagazine, ValueKind.Int32, Units.Piece),
                Minute(V.LabelMagazine, ValueKind.Int32, Units.Piece),
                Text(V.StrapperState)),
            Group(G.Events, false, EventVariableSpecs(HistoryNodeMap.ExtraEventNodes(Domain.StationType.Packaging))),
        ]);

    // Minutenwert-Variablen tragen zunächst einen leeren Namen; ihn ergänzt die Gruppe aus dem gemeinsamen Vertrag (HistoryNodeMap).
    private static GroupSpec Group(string name, bool optional, params VariableSpec[] variables) =>
        new(name, optional, [.. variables.Select(v => v.History is MinuteHistory { Variable.Length: 0 }
            ? v with { History = new MinuteHistory(HistoryNodeMap.MinuteVariableOf(name, v.Name)) }
            : v)]);

    private static VariableSpec Text(string name) => new(name, ValueKind.String);

    private static VariableSpec Number(string name, ValueKind kind, UnitInfo? unit = null) => new(name, kind, unit);

    private static VariableSpec Minute(string name, ValueKind kind, UnitInfo? unit) =>
        new(name, kind, unit, new MinuteHistory(string.Empty));

    private static VariableSpec Cycle(string name, UnitInfo unit, Func<CycleRecord, object> select) =>
        new(name, ValueKind.Double, unit, new CycleHistory(c => select(c)));

    private static VariableSpec[] EventVariableSpecs(IEnumerable<EventNode> nodes) =>
        [.. nodes.Select(n => new VariableSpec(n.Variable, ValueKind.String, null, new RecordHistory(n.RecordType)))];
}
