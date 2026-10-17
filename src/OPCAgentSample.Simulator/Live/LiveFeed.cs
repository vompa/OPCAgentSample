using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulator.History;
using OPCAgentSample.Simulator.Model;
using V = OPCAgentSample.Domain.Opc.StationVariables;
using G = OPCAgentSample.Domain.Opc.StationGroups;

namespace OPCAgentSample.Simulator.Live;

/// <summary>Aktueller Wert eines Knotens; <c>null</c> bedeutet "keine Daten" (Statuscode BadNoData).</summary>
internal readonly record struct LiveValue(object? Value, DateTimeOffset Timestamp);

/// <summary>
/// Rechnet die Live-Werte aller Knoten zur Simulationszeit: Zustand und Zähler aus <see cref="LiveStateProvider"/>,
/// Taktwerte, Minutenwerte, Störzähler und Ereignisse aus dem Datensatzstrom (<see cref="PlantState"/>).
/// </summary>
internal sealed class LiveFeed
{
    private const double PressTimeShareOfCycle = 0.55;
    private const int BufferCapacityPlates = 60;
    private static readonly TimeSpan s_lookahead = TimeSpan.FromMinutes(1);
    private static readonly DateTimeOffset s_lastDoorLockInspection = new(2026, 4, 14, 0, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, int> s_yearOfConstruction = new Dictionary<string, int>
    {
        ["RT-01"] = 2016, ["RT-02"] = 2016, ["RT-03"] = 2014, ["RT-04"] = 2019, ["PU-OFEN"] = 2012,
        ["OF-01"] = 2012, ["OF-02"] = 2015, ["VP-01"] = 2018, ["VP-02"] = 2021,
    };

    private readonly HistoryGenerator _generator;
    private readonly LiveStateProvider _provider;
    private readonly SimulationClock _clock;
    private readonly Queue<HistoryRecord> _pending = new();
    private DateTimeOffset _ingestedUntil;
    private DateTimeOffset _generatedUntil;

    public LiveFeed(HistoryGenerator generator, SimulationClock clock)
    {
        _generator = generator;
        _clock = clock;
        _provider = new LiveStateProvider(generator);
        State = new PlantState(generator.Scenario.Calendar);
        _ingestedUntil = generator.Scenario.HistoryStartUtc;
        _generatedUntil = _ingestedUntil;
    }

    public PlantState State { get; }

    /// <summary>
    /// Spielt alle Datensätze bis <paramref name="until"/> (exklusiv) ein, die noch nicht eingespielt sind. Der Generator
    /// hat je Aufruf feste Kosten (Schichten, Verlustlisten, Zustandswechsel); deshalb erzeugt er die Datensätze minutenweise
    /// voraus und der Sekundentakt entnimmt nur die fälligen. Reihenfolge und Inhalt sind dieselben wie bei Sekundenfenstern.
    /// </summary>
    public void Advance(DateTimeOffset until)
    {
        if (until <= _ingestedUntil) return;
        if (until > _generatedUntil) Generate(until);
        while (_pending.TryPeek(out var record) && record.TimestampUtc < until) State.Ingest(_pending.Dequeue());
        _ingestedUntil = until;
    }

    private void Generate(DateTimeOffset until)
    {
        if (until - _generatedUntil > s_lookahead)
        {
            // Großer Sprung (Start, hohe Geschwindigkeit): direkt einspielen statt zwischenzuspeichern.
            while (_pending.TryDequeue(out var queued)) State.Ingest(queued);
            foreach (var record in _generator.Generate(_generatedUntil, until)) State.Ingest(record);
            _generatedUntil = until;
        }

        var target = until + s_lookahead;
        foreach (var record in _generator.Generate(_generatedUntil, target)) _pending.Enqueue(record);
        _generatedUntil = target;
    }

    public IReadOnlyDictionary<string, LiveValue> Compute(DateTimeOffset now)
    {
        var values = new Dictionary<string, LiveValue>(1024);
        var scenario = _generator.Scenario;
        foreach (var station in PlantLayout.HallAStations) AddStation(values, station, now);
        AddClimate(values, scenario, now);
        AddShiftCalendar(values, scenario.Calendar, now);
        AddSimulation(values, scenario, now);
        AddOtherHalls(values, scenario.Calendar, now);
        return values;
    }

    private void AddStation(Dictionary<string, LiveValue> values, Station station, DateTimeOffset now)
    {
        void Put(string group, string name, object? value, DateTimeOffset? at = null) =>
            values[NodeIdScheme.Variable(station, group, name)] = new LiveValue(value, at ?? now);

        var accumulator = State.Station(station.Id);
        ProductionStationSnapshot? snapshot = station.Type is StationType.RotaryTable or StationType.Packaging
            ? _provider.Station(station.Id, now)
            : null;
        var oven = station.Type == StationType.Oven ? _provider.Oven(station.Id, now) : null;
        var state = snapshot?.State ?? oven?.State ?? OperatingState.Running;
        var since = snapshot?.StateSinceUtc ?? (accumulator.State is null ? _generator.Scenario.HistoryStartUtc : accumulator.StateSince);

        Put(G.Identity, V.StationId, station.Id);
        Put(G.Identity, V.Designation, station.Name);
        Put(G.Identity, V.AreaName, AreaLabel(station.Area));
        Put(G.Identity, V.Manufacturer, "Demo-Anlagenbau (fiktiv)");
        Put(G.Identity, V.YearOfConstruction, s_yearOfConstruction[station.Id]);
        Put(G.State, V.OperatingState, (int)state);
        Put(G.State, V.StateSince, since.UtcDateTime);
        Put(G.State, V.MachineryItemState, MachineryItemState(state));
        Put(G.Alarms, V.ActiveAlarms, State.ActiveCount(station.Id, alarms: true));
        Put(G.Alarms, V.ActiveMessages, State.ActiveCount(station.Id, alarms: false));

        switch (station.Type)
        {
            case StationType.RotaryTable:
                AddProduction(Put, snapshot!, accumulator, station, now);
                AddRotaryTable(Put, snapshot!, accumulator, station);
                break;
            case StationType.Packaging:
                AddProduction(Put, snapshot!, accumulator, station, now);
                AddPackaging(Put, snapshot!, accumulator, station);
                break;
            case StationType.Oven:
                AddOven(Put, oven!, station);
                break;
            case StationType.Buffer:
                AddBuffer(Put, _provider.Buffer(now));
                break;
        }

        AddEvents(values, station, now);
    }

    private void AddProduction(
        Action<string, string, object?, DateTimeOffset?> put, ProductionStationSnapshot snapshot, StationAccumulator accumulator,
        Station station, DateTimeOffset now)
    {
        var scenario = _generator.Scenario;
        var isPress = station.Type == StationType.RotaryTable;
        var order = snapshot.OrderId is null ? null : scenario.Orders.FirstOrDefault(o => o.Id == snapshot.OrderId);
        put(G.Order, V.OrderId, snapshot.OrderId ?? string.Empty, null);
        put(G.Order, V.ArticleId, snapshot.ArticleId, null);
        put(G.Order, V.OrderQuantity, isPress ? order?.Quantity ?? 0 : snapshot.PlannedShiftQuantity, null);
        put(G.Order, V.OrderActual, isPress ? OrderActual(accumulator, snapshot.OrderId) : (int)snapshot.PiecesSinceShiftStart, null);
        put(G.Order, V.TargetCycle, snapshot.TargetCycleSeconds, null);
        put(G.Order, V.ShiftPlanQuantity, snapshot.PlannedShiftQuantity, null);

        put(G.Counters, V.GoodPieces, (int)(snapshot.PiecesSinceShiftStart - snapshot.ScrapSinceShiftStart), null);
        put(G.Counters, V.ScrapPieces, (int)snapshot.ScrapSinceShiftStart, null);
        put(G.Counters, V.OperatingHours, accumulator.RunningTimeAt(now).TotalHours, null);
        put(G.Counters, V.TotalCycles, accumulator.TotalCycles, null);
        put(G.Energy, V.EnergyKwh, Minute(station.Id, MinuteVariables.EnergyKwh), null);
    }

    private void AddRotaryTable(
        Action<string, string, object?, DateTimeOffset?> put, ProductionStationSnapshot snapshot, StationAccumulator accumulator,
        Station station)
    {
        var scenario = _generator.Scenario;
        var cycle = accumulator.Last;
        DateTimeOffset? at = cycle?.TimestampUtc;
        var article = scenario.Article(snapshot.ArticleId);

        put(G.Process, V.CycleTime, cycle?.CycleTimeSeconds, at);
        put(G.Process, V.CycleTimeMean, snapshot.CycleTimeSeconds, null);
        put(G.Process, V.CycleTimeTarget, snapshot.TargetCycleSeconds, null);
        put(G.Process, V.PressForce, cycle?.PressForceKn, at);
        put(G.Process, V.PressForceBuildUp, cycle?.PressForceBuildUpSeconds, at);
        put(G.Process, V.PressTime, cycle is null ? null : cycle.CycleTimeSeconds * PressTimeShareOfCycle, at);
        put(G.Process, V.MouldTemperature, Minute(station.Id, MinuteVariables.MouldTemperature), null);
        put(G.Process, V.FillWeight, cycle?.FillWeightGrams, at);
        put(G.Process, V.FillWeightMean, snapshot.FillWeightMeanGrams, null);
        put(G.Process, V.FillWeightTarget, snapshot.FillWeightTargetGrams, null);
        put(G.Process, V.FillWeightTolerance, article.FillWeightToleranceGrams, null);
        put(G.Process, V.Thickness, cycle?.ThicknessMm, at);

        put(G.Cycle, V.CycleTargetTime, cycle?.TargetCycleSeconds, at);
        put(G.Cycle, V.CycleOrderId, cycle is null ? null : cycle.OrderId ?? string.Empty, at);
        put(G.Cycle, V.CycleArticleId, cycle?.ArticleId, at);
        put(G.Cycle, V.CycleMixtureBatch, cycle?.MixtureBatchId, at);
        put(G.Cycle, V.CycleScrapReason, cycle is null ? null : cycle.Scrap is { } s ? (int)s + 1 : 0, at);

        foreach (var (name, assembly) in PlantTypes.SubStations)
        {
            put(G.SubStations, name + V.StateSuffix, accumulator.HasOpenLoss(assembly) ? "Störung" : "OK", null);
            put(G.SubStations, name + V.FaultCountSuffix, accumulator.FaultCounts.GetValueOrDefault(assembly), null);
        }

        var batch = scenario.MixtureBatches.FirstOrDefault(b => b.Id == snapshot.MixtureBatchId);
        var mixture = snapshot.Materials.FirstOrDefault(m => m.Material == MinuteVariables.MixtureLevelKg);
        put(G.Material, V.Mixture, batch?.Recipe ?? string.Empty, null);
        put(G.Material, V.BatchId, snapshot.MixtureBatchId ?? string.Empty, null);
        put(G.Material, V.MixtureLevelKg, mixture?.Level, null);
        put(G.Material, V.ProcessableUntil, mixture?.ProcessableUntilUtc?.UtcDateTime, null);
        put(G.Material, V.FabricMagazine, MaterialLevel(snapshot, MinuteVariables.FabricMagazine), null);
        put(G.Material, V.LabelMagazine, MaterialLevel(snapshot, MinuteVariables.LabelMagazine), null);

        put(G.Tool, V.MouldId, snapshot.Mould?.MouldId, null);
        put(G.Tool, V.StrokesSinceChange, snapshot.Mould?.Strokes, null);
        put(G.Tool, V.StrokeLimit, snapshot.Mould?.StrokeLimit, null);

        put(G.Hydraulics, V.OilTemperature, Minute(station.Id, MinuteVariables.OilTemperature), null);
        put(G.Hydraulics, V.SystemPressure, Minute(station.Id, MinuteVariables.SystemPressure), null);

        var scrap = accumulator.ShiftKey == snapshot.Shift.Key ? accumulator.ScrapByReason : new int[accumulator.ScrapByReason.Length];
        put(G.ScrapReasons, V.ScrapWeight, scrap[(int)ScrapReason.Weight], null);
        put(G.ScrapReasons, V.ScrapThickness, scrap[(int)ScrapReason.Thickness], null);
        put(G.ScrapReasons, V.ScrapFabric, scrap[(int)ScrapReason.Fabric], null);
        put(G.ScrapReasons, V.ScrapLabel, scrap[(int)ScrapReason.Label], null);
        put(G.ScrapReasons, V.ScrapBreakage, scrap[(int)ScrapReason.Breakage], null);
    }

    private void AddPackaging(
        Action<string, string, object?, DateTimeOffset?> put, ProductionStationSnapshot snapshot, StationAccumulator accumulator,
        Station station)
    {
        var article = _generator.Scenario.PackagingArticle(snapshot.ArticleId);
        put(G.Packaging, V.CartonType, snapshot.ArticleId, null);
        put(G.Packaging, V.PiecesPerCarton, article.PiecesPerCarton, null);
        put(G.Packaging, V.CartonsActual, (int)snapshot.PiecesSinceShiftStart, null);
        put(G.Packaging, V.CartonsTarget, snapshot.PlannedSinceShiftStart, null);
        put(G.Packaging, V.CheckweigherRejects, (int)snapshot.ScrapSinceShiftStart, null);
        put(G.Packaging, V.CartonMagazine, MaterialLevel(snapshot, MinuteVariables.CartonMagazine), null);
        put(G.Packaging, V.LabelMagazine, MaterialLevel(snapshot, MinuteVariables.LabelMagazine), null);
        put(G.Packaging, V.StrapperState, accumulator.HasOpenLoss(Assemblies.Strapper) ? "Störung" : "Bereit", null);
    }

    private void AddOven(Action<string, string, object?, DateTimeOffset?> put, OvenSnapshot oven, Station station)
    {
        var spec = _generator.Scenario.Ovens.First(o => o.StationId == station.Id);
        var charge = oven.ActiveCharges.OrderBy(c => c.LoadedUtc).FirstOrDefault();
        var elapsed = charge is null ? TimeSpan.Zero : oven.AtUtc - charge.LoadedUtc;

        put(G.Capacity, V.Places, oven.Places, null);
        put(G.Capacity, V.OccupiedPlaces, oven.OccupiedPlaces, null);
        put(G.Capacity, V.Occupancy, oven.OccupancySinceShiftStart * 100, null);
        put(G.Capacity, V.OccupancyNow, 100.0 * oven.OccupiedPlaces / oven.Places, null);

        put(G.ActiveCharge, V.ChargeId, charge?.ChargeId ?? string.Empty, null);
        put(G.ActiveCharge, V.ChargeDiameter, charge?.DiameterMm ?? 0, null);
        put(G.ActiveCharge, V.ChargePlates, charge?.Plates ?? 0, null);
        put(G.ActiveCharge, V.ProfileStep, charge is null ? "Leer" : ProfileStep(spec, elapsed), null);
        put(G.ActiveCharge, V.RemainingMinutes, charge is null ? 0.0 : Math.Max(0, (charge.UnloadedUtc - oven.AtUtc).TotalMinutes), null);

        put(G.Temperature, V.Zone1Actual, Minute(station.Id, MinuteVariables.Zone1Temperature), null);
        put(G.Temperature, V.Zone2Actual, Minute(station.Id, MinuteVariables.Zone2Temperature), null);
        put(G.Temperature, V.Zone3Actual, Minute(station.Id, MinuteVariables.Zone3Temperature), null);
        put(G.Temperature, V.Zone1Target, oven.HoldTemperatureCelsius, null);
        put(G.Temperature, V.Zone2Target, oven.HoldTemperatureCelsius, null);
        put(G.Temperature, V.Zone3Target, oven.HoldTemperatureCelsius, null);

        put(G.Planning, V.NextRelease, oven.NextUnloadUtc?.UtcDateTime, null);
        put(G.Planning, V.AssignmentRule, oven.AssignmentRule, null);
        put(G.Safety, V.DoorLockState, "Verriegelt", null);
        put(G.Safety, V.LastInspection, s_lastDoorLockInspection.UtcDateTime, null);
    }

    private static void AddBuffer(Action<string, string, object?, DateTimeOffset?> put, BufferSnapshot buffer)
    {
        put(G.Inventory, V.BufferCapacity, BufferCapacityPlates, null);
        put(G.Inventory, V.PlateCount, buffer.Plates, null);
        put(G.Inventory, V.MeanWait, buffer.MeanWaitMinutes, null);
        put(G.Inventory, V.OldestWait, buffer.OldestWaitMinutes, null);
    }

    private void AddEvents(Dictionary<string, LiveValue> values, Station station, DateTimeOffset now)
    {
        var includeOptional = station.Type is StationType.RotaryTable or StationType.Packaging;
        foreach (var group in PlantTypes.InstanceGroups(PlantTypes.For(station.Type), includeOptional).Where(g => g.Name == G.Events))
        {
            foreach (var variable in group.Variables)
            {
                if (variable.History is not RecordHistory history) continue;
                var record = State.LastRecord(station.Id, history.RecordType.Name);
                values[NodeIdScheme.Variable(station, G.Events, variable.Name)] =
                    new LiveValue(record is null ? string.Empty : HistoryJson.Serialize(record), record?.TimestampUtc ?? now);
            }
        }
    }

    private void AddClimate(Dictionary<string, LiveValue> values, Simulation.Scenario.ScenarioDefinition scenario, DateTimeOffset now)
    {
        var climate = _provider.Climate(now);
        var range = scenario.ClimateRange;
        void Put(string name, object value) => values[NodeIdScheme.ClimateVariable(name)] = new LiveValue(value, now);
        Put(V.Temperature, climate.Celsius);
        Put(V.Humidity, climate.Humidity);
        Put(V.TemperatureMin, range.MinCelsius);
        Put(V.TemperatureMax, range.MaxCelsius);
        Put(V.HumidityMin, range.MinHumidity);
        Put(V.HumidityMax, range.MaxHumidity);
        Put(V.InRange, climate.InRange);
    }

    private static void AddShiftCalendar(Dictionary<string, LiveValue> values, ShiftCalendar calendar, DateTimeOffset now)
    {
        var period = calendar.ShiftPeriodOf(now);
        void Put(string name, object value) => values[NodeIdScheme.ShiftVariable(name)] = new LiveValue(value, now);
        Put(V.CurrentShift, ShiftCalendar.ShiftLabel(calendar.ShiftOf(now)));
        Put(V.ShiftKey, period.Key);
        Put(V.ShiftStart, period.StartUtc.UtcDateTime);
        Put(V.ShiftEnd, period.EndUtc.UtcDateTime);
        Put(V.ProductionDay, calendar.ProductionDayOf(now).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Put(V.InProduction, calendar.IsProductionTime(now));
    }

    private void AddSimulation(Dictionary<string, LiveValue> values, Simulation.Scenario.ScenarioDefinition scenario, DateTimeOffset now)
    {
        void Put(string name, object value) => values[NodeIdScheme.SimulationVariable(name)] = new LiveValue(value, now);
        Put(V.SimulationTime, now.UtcDateTime);
        Put(V.SimulationSpeed, _clock.Speed);
        Put(V.HistoryStart, scenario.HistoryStartUtc.UtcDateTime);
    }

    private static void AddOtherHalls(Dictionary<string, LiveValue> values, ShiftCalendar calendar, DateTimeOffset now)
    {
        var state = calendar.IsProductionTime(now) ? OperatingState.Running : OperatingState.Idle;
        foreach (var hallId in new[] { PlantLayout.HallB, PlantLayout.HallC })
        {
            var stationId = hallId == PlantLayout.HallB ? Simulation.Scenario.PlantComparison.HallBStation : Simulation.Scenario.PlantComparison.HallCStation;
            values[NodeIdScheme.HallState(hallId)] = new LiveValue((int)state, now);
            foreach (var figures in Simulation.Scenario.PlantComparison.Figures.Where(f => f.HallStationId == stationId))
            {
                var shiftCode = ShiftCalendar.ShiftCode(figures.Shift);
                void Put(string name, object value) =>
                    values[NodeIdScheme.HallCounter(hallId, figures.WeekKey, shiftCode, name)] = new LiveValue(value, now);
                Put(V.GoodPieces, figures.Figures.GoodPieces);
                Put(V.ScrapPieces, figures.Figures.ScrapPieces);
                Put(V.PlannedQuantity, (double)figures.Figures.PlannedQuantity);
                Put(V.PlanningMinutes, figures.Figures.PlanningTime.TotalMinutes);
                Put(V.RunMinutes, figures.Figures.RunTime.TotalMinutes);
            }
        }
    }

    private static int OrderActual(StationAccumulator accumulator, string? orderId) =>
        orderId is null ? 0 : accumulator.OrderCycles.GetValueOrDefault(orderId);

    private double? Minute(string stationId, string variable) => State.Minute(stationId, variable)?.Value;

    private static int? MaterialLevel(ProductionStationSnapshot snapshot, string variable) =>
        snapshot.Materials.FirstOrDefault(m => m.Material == variable) is { } material ? (int)Math.Round(material.Level) : null;

    private static string ProfileStep(Simulation.Scenario.OvenSpec spec, TimeSpan elapsed) =>
        elapsed < spec.HeatUp ? "Aufheizen" : elapsed < spec.HeatUp + spec.Hold ? "Halten" : "Abkühlen";

    private static string AreaLabel(Area area) => area switch
    {
        Area.Pressing => "Pressen",
        Area.Buffer => "Puffer",
        Area.Curing => "Aushärten",
        Area.Packaging => "Verpackung",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
    };

    private static string MachineryItemState(OperatingState state) => state switch
    {
        OperatingState.Running or OperatingState.RunningWithoutOrder => "Executing",
        OperatingState.Fault or OperatingState.PlannedMaintenance => "OutOfService",
        _ => "NotExecuting",
    };
}
