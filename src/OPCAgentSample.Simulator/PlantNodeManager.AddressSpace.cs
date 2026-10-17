using Opc.Ua;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulator.Model;
using V = OPCAgentSample.Domain.Opc.StationVariables;
using G = OPCAgentSample.Domain.Opc.StationGroups;

namespace OPCAgentSample.Simulator;

// Aufbau des Adressraums: Enum-Datentypen, ObjectTypes, Werk mit Hallen, Stationen, Klima, Schichtkalender, Simulation.
internal sealed partial class PlantNodeManager
{
    private const string UnitNamespaceUri = "http://www.opcfoundation.org/UA/units/un/cefact";

    private readonly Dictionary<string, BaseObjectTypeState> _typeNodes = [];

    private NodeId Id(string identifier) => new(identifier, NamespaceIndex);

    private QualifiedName BrowseNameOf(string name) => new(name, NamespaceIndex);

    private void BuildEnumerations(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        AddEnumeration(PlantEnumerations.OperatingStateName, PlantEnumerations.OperatingStates, externalReferences);
        AddEnumeration(PlantEnumerations.ScrapGradeName, PlantEnumerations.ScrapGrades, externalReferences);
    }

    private void AddEnumeration(string name, IReadOnlyList<string> labels, IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        var type = new DataTypeState
        {
            SymbolicName = name,
            NodeId = Id(NodeIdScheme.Type(name)),
            BrowseName = BrowseNameOf(name),
            DisplayName = new LocalizedText("de", name),
            SuperTypeId = DataTypeIds.Enumeration,
            IsAbstract = false,
        };
        var strings = new PropertyState<LocalizedText[]>(type)
        {
            SymbolicName = BrowseNames.EnumStrings,
            ReferenceTypeId = ReferenceTypes.HasProperty,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            NodeId = Id($"{NodeIdScheme.Type(name)}.{BrowseNames.EnumStrings}"),
            BrowseName = new QualifiedName(BrowseNames.EnumStrings),
            DisplayName = new LocalizedText(BrowseNames.EnumStrings),
            DataType = DataTypeIds.LocalizedText,
            ValueRank = ValueRanks.OneDimension,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = [.. labels.Select(label => new LocalizedText("de", label))],
        };
        type.AddChild(strings);
        AddExternalReference(DataTypeIds.Enumeration, ReferenceTypeIds.HasSubtype, false, type.NodeId, externalReferences);
        AddPredefinedNode(SystemContext, type);
    }

    private void BuildTypes(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        foreach (var spec in PlantTypes.All)
        {
            var type = new BaseObjectTypeState
            {
                SymbolicName = spec.Name,
                NodeId = Id(NodeIdScheme.Type(spec.Name)),
                BrowseName = BrowseNameOf(spec.Name),
                DisplayName = new LocalizedText("de", spec.Name),
                Description = new LocalizedText("de", spec.Description),
                SuperTypeId = spec.BaseName is null ? ObjectTypeIds.BaseObjectType : Id(NodeIdScheme.Type(spec.BaseName)),
                IsAbstract = false,
            };
            if (spec.BaseName is null)
                AddExternalReference(ObjectTypeIds.BaseObjectType, ReferenceTypeIds.HasSubtype, false, type.NodeId, externalReferences);
            else
                _typeNodes[spec.BaseName].AddReference(ReferenceTypeIds.HasSubtype, false, type.NodeId);

            foreach (var group in spec.Groups) AddGroup(type, NodeIdScheme.Type(spec.Name), group, declaration: true, sourceId: null);
            _typeNodes[spec.Name] = type;
            AddPredefinedNode(SystemContext, type);
        }
    }

    private void BuildPlant(IDictionary<NodeId, IList<IReference>> externalReferences)
    {
        if (!externalReferences.TryGetValue(ObjectIds.ObjectsFolder, out var objectsReferences))
            externalReferences[ObjectIds.ObjectsFolder] = objectsReferences = new List<IReference>();

        var plant = NewFolder(null, NodeIdScheme.PlantFolder, "Werk");
        plant.AddReference(ReferenceTypes.Organizes, true, ObjectIds.ObjectsFolder);
        objectsReferences.Add(new NodeStateReference(ReferenceTypes.Organizes, false, plant.NodeId));

        BuildSimulation(plant);
        BuildShiftCalendar(plant);
        foreach (var hallId in PlantLayout.Halls)
        {
            if (hallId == PlantLayout.HallA) BuildHallA(plant);
            else BuildOtherHall(plant, hallId);
        }

        AddPredefinedNode(SystemContext, plant);
        foreach (var node in _stations.Values)
        {
            AddRootNotifier(node.Object);
            AddExternalReference(ObjectIds.Server, ReferenceTypeIds.HasNotifier, false, node.Object.NodeId, externalReferences);
        }
    }

    private void BuildSimulation(FolderState plant)
    {
        var folder = NewFolder(plant, NodeIdScheme.SimulationObject, "Simulation");
        AddSimpleVariable(folder, V.SimulationTime, ValueKind.DateTime);
        AddSimpleVariable(folder, V.SimulationSpeed, ValueKind.Double);
        AddSimpleVariable(folder, V.HistoryStart, ValueKind.DateTime);
    }

    private void BuildShiftCalendar(FolderState plant)
    {
        var folder = NewFolder(plant, NodeIdScheme.ShiftCalendarObject, "Schichtkalender");
        AddSimpleVariable(folder, V.CurrentShift, ValueKind.String);
        AddSimpleVariable(folder, V.ShiftKey, ValueKind.String);
        AddSimpleVariable(folder, V.ShiftStart, ValueKind.DateTime);
        AddSimpleVariable(folder, V.ShiftEnd, ValueKind.DateTime);
        AddSimpleVariable(folder, V.ProductionDay, ValueKind.String);
        AddSimpleVariable(folder, V.InProduction, ValueKind.Boolean);
    }

    private void BuildHallA(FolderState plant)
    {
        var hall = NewFolder(plant, NodeIdScheme.Hall(PlantLayout.HallA), "HalleA");

        var climate = NewFolder(hall, NodeIdScheme.ClimateObject, "Klima");
        AddSimpleVariable(climate, V.Temperature, ValueKind.Double, Units.Celsius,
            new MinuteHistory(Domain.History.MinuteVariables.Temperature), Domain.History.MinuteVariables.HallAClimateSource);
        AddSimpleVariable(climate, V.Humidity, ValueKind.Double, Units.Percent,
            new MinuteHistory(Domain.History.MinuteVariables.Humidity), Domain.History.MinuteVariables.HallAClimateSource);
        AddSimpleVariable(climate, V.TemperatureMin, ValueKind.Double, Units.Celsius);
        AddSimpleVariable(climate, V.TemperatureMax, ValueKind.Double, Units.Celsius);
        AddSimpleVariable(climate, V.HumidityMin, ValueKind.Double, Units.Percent);
        AddSimpleVariable(climate, V.HumidityMax, ValueKind.Double, Units.Percent);
        AddSimpleVariable(climate, V.InRange, ValueKind.Boolean);

        var areaFolders = new Dictionary<Area, FolderState>();
        foreach (var station in PlantLayout.HallAStations)
        {
            if (!areaFolders.TryGetValue(station.Area, out var areaFolder))
            {
                var name = NodeIdScheme.AreaFolder(station.Area);
                areaFolders[station.Area] = areaFolder = NewFolder(hall, NodeIdScheme.AreaFolderId(station.HallId, station.Area), name);
            }

            AddStation(areaFolder, station);
        }
    }

    private void AddStation(FolderState areaFolder, Station station)
    {
        var typeSpec = PlantTypes.For(station.Type);
        var includeOptional = station.Type is StationType.RotaryTable or StationType.Packaging;
        var identifier = NodeIdScheme.Station(station);
        var node = new BaseObjectState(areaFolder)
        {
            SymbolicName = station.Id,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = Id(NodeIdScheme.Type(typeSpec.Name)),
            NodeId = Id(identifier),
            BrowseName = BrowseNameOf(station.Id),
            DisplayName = new LocalizedText("de", station.Id),
            Description = new LocalizedText("de", station.Name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.SubscribeToEvents,
        };
        areaFolder.AddChild(node);

        FolderState? alarmFolder = null;
        foreach (var group in PlantTypes.InstanceGroups(typeSpec, includeOptional))
        {
            var folder = AddGroup(node, identifier, group, declaration: false, sourceId: station.Id);
            if (group.Name == G.Alarms) alarmFolder = folder;
        }

        _stations[station.Id] = new StationNode(station, node, alarmFolder!);
    }

    private void BuildOtherHall(FolderState plant, string hallId)
    {
        var hall = NewFolder(plant, NodeIdScheme.Hall(hallId), NodeIdScheme.Hall(hallId));
        var stateFolder = NewFolder(hall, $"{NodeIdScheme.Hall(hallId)}.{G.State}", G.State);
        AddSimpleVariable(stateFolder, V.OperatingState, ValueKind.OperatingState);

        var counters = NewFolder(hall, $"{NodeIdScheme.Hall(hallId)}.{G.Counters}", G.Counters);
        var stationId = hallId == PlantLayout.HallB ? PlantComparison.HallBStation : PlantComparison.HallCStation;
        foreach (var week in PlantComparison.Figures.Where(f => f.HallStationId == stationId).GroupBy(f => f.WeekKey))
        {
            var weekFolder = NewFolder(counters, $"{counters.NodeId.Identifier}.{week.Key}", week.Key);
            foreach (var figures in week)
            {
                var code = ShiftCalendar.ShiftCode(figures.Shift);
                var shiftFolder = NewFolder(weekFolder, $"{weekFolder.NodeId.Identifier}.{code}", code);
                AddSimpleVariable(shiftFolder, V.GoodPieces, ValueKind.Int64);
                AddSimpleVariable(shiftFolder, V.ScrapPieces, ValueKind.Int64);
                AddSimpleVariable(shiftFolder, V.PlannedQuantity, ValueKind.Double);
                AddSimpleVariable(shiftFolder, V.PlanningMinutes, ValueKind.Double, Units.Minute);
                AddSimpleVariable(shiftFolder, V.RunMinutes, ValueKind.Double, Units.Minute);
            }
        }
    }

    private FolderState NewFolder(NodeState? parent, string identifier, string name)
    {
        var folder = new FolderState(parent)
        {
            SymbolicName = name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = ObjectTypeIds.FolderType,
            NodeId = Id(identifier),
            BrowseName = BrowseNameOf(name),
            DisplayName = new LocalizedText("de", name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            EventNotifier = EventNotifiers.None,
        };
        parent?.AddChild(folder);
        return folder;
    }

    private FolderState AddGroup(NodeState parent, string parentIdentifier, GroupSpec group, bool declaration, string? sourceId)
    {
        var folder = NewFolder(parent, $"{parentIdentifier}.{group.Name}", group.Name);
        if (declaration)
            folder.ModellingRuleId = group.Optional ? ObjectIds.ModellingRule_Optional : ObjectIds.ModellingRule_Mandatory;

        foreach (var variable in group.Variables)
            AddVariable(folder, folder.NodeId.Identifier.ToString()!, variable, declaration, sourceId);
        return folder;
    }

    private void AddSimpleVariable(
        NodeState parent, string name, ValueKind kind, UnitInfo? unit = null, HistoryBinding? history = null, string? sourceId = null) =>
        AddVariable(parent, ((NodeId)parent.NodeId).Identifier.ToString()!, new VariableSpec(name, kind, unit, history), false, sourceId);

    private void AddVariable(NodeState parent, string parentIdentifier, VariableSpec spec, bool declaration, string? sourceId)
    {
        var identifier = $"{parentIdentifier}.{spec.Name}";
        var historizing = spec.History is not null && !declaration;
        var access = (byte)(AccessLevels.CurrentRead | (historizing ? AccessLevels.HistoryRead : AccessLevels.None));
        var variable = new BaseDataVariableState(parent)
        {
            SymbolicName = spec.Name,
            ReferenceTypeId = ReferenceTypes.Organizes,
            TypeDefinitionId = VariableTypeIds.BaseDataVariableType,
            NodeId = Id(identifier),
            BrowseName = BrowseNameOf(spec.Name),
            DisplayName = new LocalizedText("de", spec.Name),
            WriteMask = AttributeWriteMask.None,
            UserWriteMask = AttributeWriteMask.None,
            DataType = ValueMapper.DataType(spec.Kind, NamespaceIndex),
            ValueRank = ValueRanks.Scalar,
            AccessLevel = access,
            UserAccessLevel = access,
            Historizing = historizing,
            Value = ValueMapper.DefaultValue(spec.Kind),
            StatusCode = declaration ? StatusCodes.Good : StatusCodes.BadWaitingForInitialData,
            Timestamp = DateTime.UtcNow,
        };
        if (declaration) variable.ModellingRuleId = ObjectIds.ModellingRule_Mandatory;
        if (spec.Unit is { } unit) AddEngineeringUnits(variable, identifier, unit);
        parent.AddChild(variable);

        if (declaration) return;
        _variables[identifier] = new VariableEntry(variable, spec.Kind);
        if (spec.History is not null) _history[identifier] = new HistoryEntry(spec.History, sourceId!, spec.Kind);
    }

    private void AddEngineeringUnits(BaseDataVariableState variable, string identifier, UnitInfo unit)
    {
        var information = new PropertyState<EUInformation>(variable)
        {
            SymbolicName = BrowseNames.EngineeringUnits,
            ReferenceTypeId = ReferenceTypes.HasProperty,
            TypeDefinitionId = VariableTypeIds.PropertyType,
            NodeId = Id($"{identifier}.{BrowseNames.EngineeringUnits}"),
            BrowseName = new QualifiedName(BrowseNames.EngineeringUnits),
            DisplayName = new LocalizedText(BrowseNames.EngineeringUnits),
            DataType = DataTypeIds.EUInformation,
            ValueRank = ValueRanks.Scalar,
            AccessLevel = AccessLevels.CurrentRead,
            UserAccessLevel = AccessLevels.CurrentRead,
            Value = new EUInformation
            {
                NamespaceUri = UnitNamespaceUri,
                UnitId = UnitId(unit.Code),
                DisplayName = new LocalizedText(unit.Symbol),
                Description = new LocalizedText(unit.Symbol),
            },
        };
        variable.AddChild(information);
    }

    /// <summary>UNECE-Code (z. B. "CEL") als Unit-Id nach OPC 10000-8: ASCII-Zeichen als Big-Endian-Zahl.</summary>
    private static int UnitId(string code) => code.Aggregate(0, (id, c) => (id << 8) | c);
}
