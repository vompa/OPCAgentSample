using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Füllstand eines Materials mit Verbrauch je Minute bei Plantempo und ggf. Verarbeitbarkeitsgrenze.</summary>
public sealed record MaterialLevel(string Material, double Level, double ConsumptionPerMinute, DateTimeOffset? ProcessableUntilUtc)
{
    public MaterialReachResult Reach(DateTimeOffset nowUtc) =>
        MaterialReach.Compute(Material, Level, ConsumptionPerMinute, ProcessableUntilUtc, nowUtc);
}

/// <summary>Pressform an einer Station mit Hüben seit Einbau und aktuellem Hubtempo (letzte Stunde).</summary>
public sealed record MouldStatus(string MouldId, int Strokes, int StrokeLimit, double StrokesPerMinute)
{
    public MouldWearResult Wear(DateTimeOffset nowUtc) => MouldWear.Compute(Strokes, StrokeLimit, StrokesPerMinute, nowUtc);
}

/// <summary>Zustand einer Produktionsstation zu einem Zeitpunkt (Quelle der OPC-Knoten und des Redis-Zustands).</summary>
public sealed record ProductionStationSnapshot(
    string StationId,
    DateTimeOffset AtUtc,
    OperatingState State,
    DateTimeOffset StateSinceUtc,
    string? OrderId,
    string ArticleId,
    string? MixtureBatchId,
    ProductionPeriod Shift,
    int PlannedShiftQuantity,
    long PiecesSinceShiftStart,
    long ScrapSinceShiftStart,
    double PlannedSinceShiftStart,
    double? CycleTimeSeconds,
    double TargetCycleSeconds,
    double? BuildUpSeconds,
    double? FillWeightMeanGrams,
    double? FillWeightTargetGrams,
    IReadOnlyList<MaterialLevel> Materials,
    MouldStatus? Mould)
{
    public double PlanRatio => PlannedSinceShiftStart > 0 ? PiecesSinceShiftStart / PlannedSinceShiftStart : 0;

    public double? CycleDeviation => CycleTimeSeconds is { } cycle ? (cycle / TargetCycleSeconds) - 1 : null;

    public double? FillWeightDeviationGrams => FillWeightMeanGrams - FillWeightTargetGrams;

    /// <summary>Kürzeste Materialreichweite (begrenzendes Material).</summary>
    public MaterialReachResult? ShortestReach(DateTimeOffset nowUtc) =>
        Materials.Select(m => m.Reach(nowUtc)).OrderBy(r => r.Reach).FirstOrDefault();

    /// <summary>Messgrößen für die Stationsampel.</summary>
    public StationSignals Signals() => new(
        State,
        PlanRatio,
        HasRunningOrder: OrderId is not null,
        CycleDeviation,
        FillWeightDeviationGrams,
        Mould?.Wear(AtUtc).Wear,
        ShortestReach(AtUtc)?.Reach.TotalMinutes);
}

/// <summary>Zustand eines Ofens: belegte Plätze, Belegung seit Schichtbeginn, nächste Freigabe, laufende Chargen.</summary>
public sealed record OvenSnapshot(
    string OvenId,
    DateTimeOffset AtUtc,
    OperatingState State,
    int OccupiedPlaces,
    int Places,
    double OccupancySinceShiftStart,
    DateTimeOffset? NextUnloadUtc,
    string AssignmentRule,
    double HoldTemperatureCelsius,
    IReadOnlyList<OvenChargeInfo> ActiveCharges)
{
    public StationSignals Signals() => new(State, OvenOccupancy: OccupancySinceShiftStart);
}

public sealed record BufferSnapshot(DateTimeOffset AtUtc, int Plates, double MeanWaitMinutes, double OldestWaitMinutes);

public sealed record ClimateSnapshot(DateTimeOffset AtUtc, double Celsius, double Humidity, double CelsiusAtShiftStart, bool InRange);

/// <summary>
/// Liefert den Zustand jeder Station zu einem beliebigen Zeitpunkt aus demselben Modell wie die Historie. Das ist der
/// Fortsetzungspunkt für den Live-Simulator: Zustand zum Zeitpunkt t lesen, danach <see cref="HistoryGenerator.Generate"/>
/// für das nächste Intervall aufrufen. Die Drift an RT-04 wächst dabei weiter.
/// </summary>
public sealed class LiveStateProvider(HistoryGenerator generator)
{
    private const double KgPerGram = 0.001;
    private static readonly TimeSpan s_cycleWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_fillWeightWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_strokeRateWindow = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan s_stateLookback = TimeSpan.FromDays(4);

    public ProductionStationSnapshot Station(string stationId, DateTimeOffset atUtc)
    {
        var model = generator.StationModel(stationId);
        var scenario = generator.Scenario;
        var shift = generator.Shifts.At(atUtc) ?? generator.Shifts.LastStartedAtOrBefore(atUtc)
            ?? throw new InvalidOperationException("Vor dem Historienbeginn gibt es keinen Zustand.");
        var run = model.Run(shift);
        var (state, order) = model.StateAt(atUtc);
        var since = model.StateChangesBetween(atUtc - s_stateLookback, atUtc + TimeSpan.FromTicks(1)).LastOrDefault()?.TimestampUtc
            ?? scenario.HistoryStartUtc;
        var articleId = model.Plan.AssignmentAt(atUtc).ArticleId;
        var target = CycleProfile.TargetCycle(scenario, model.Plan, articleId);
        var elapsed = Clamp(atUtc - shift.StartUtc, shift.Duration);
        var planned = run.PlannedQuantity * elapsed.TotalSeconds / shift.Duration.TotalSeconds;
        var recent = model.IsPress ? RecentCycles(model, atUtc) : [];

        return new ProductionStationSnapshot(
            stationId,
            atUtc,
            state,
            since,
            order,
            articleId,
            model.IsPress ? model.Plan.BatchAt(atUtc) : null,
            shift,
            run.PlannedQuantity,
            run.CountBefore(atUtc),
            run.ScrapBefore(atUtc),
            planned,
            Mean(recent.Where(c => c.TimestampUtc >= atUtc - s_cycleWindow).Select(c => c.CycleTimeSeconds)),
            target,
            Mean(recent.Where(c => c.TimestampUtc >= atUtc - s_cycleWindow).Select(c => c.PressForceBuildUpSeconds)),
            Mean(recent.Select(c => c.FillWeightGrams)),
            model.IsPress ? scenario.Article(articleId).FillWeightTargetGrams : null,
            Materials(model, run.PlannedQuantity / shift.Duration.TotalMinutes, atUtc),
            Mould(model, atUtc));
    }

    public OvenSnapshot Oven(string ovenId, DateTimeOffset atUtc)
    {
        var oven = generator.OvenModel(ovenId);
        var shift = generator.Shifts.At(atUtc);
        var occupancy = shift is null ? 0 : Occupancy(oven, shift.StartUtc, atUtc);
        var next = oven.Charges.Where(c => c.UnloadedUtc > atUtc).Select(c => (DateTimeOffset?)c.UnloadedUtc).Min();
        return new OvenSnapshot(ovenId, atUtc, oven.StateAt(atUtc), oven.OccupiedAt(atUtc), oven.Spec.Places, occupancy, next,
            oven.Spec.AssignmentRule, oven.Spec.HoldTemperatureCelsius, [.. oven.ActiveAt(atUtc)]);
    }

    public BufferSnapshot Buffer(DateTimeOffset atUtc)
    {
        var buffer = generator.Buffer;
        return new BufferSnapshot(atUtc, buffer.PlatesAt(atUtc), buffer.MeanWaitAt(atUtc), buffer.OldestWaitAt(atUtc));
    }

    public ClimateSnapshot Climate(DateTimeOffset atUtc)
    {
        var (celsius, humidity) = generator.Climate.At(atUtc);
        var shiftStart = generator.Scenario.Calendar.ShiftPeriodOf(atUtc).StartUtc;
        var (startCelsius, _) = generator.Climate.At(shiftStart);
        return new ClimateSnapshot(atUtc, celsius, humidity, startCelsius, generator.Scenario.ClimateRange.Contains(celsius, humidity));
    }

    private List<CycleRecord> RecentCycles(ProductionStationModel model, DateTimeOffset atUtc) =>
        [.. generator.Cycles(model.StationId, atUtc - s_fillWeightWindow, atUtc)];

    private List<MaterialLevel> Materials(ProductionStationModel model, double planPiecesPerMinute, DateTimeOffset atUtc)
    {
        var materials = model.Plan.Consumables
            .Select(c => new MaterialLevel(c.Variable, model.ConsumableLevel(c, atUtc), c.PerPiece * planPiecesPerMinute, null))
            .ToList();
        if (model.IsPress)
        {
            var mixture = model.MixtureLevel(atUtc);
            var article = generator.Scenario.Article(model.Plan.AssignmentAt(atUtc).ArticleId);
            materials.Insert(0, new MaterialLevel(MinuteVariables.MixtureLevelKg, mixture.Kilograms,
                planPiecesPerMinute * article.FillWeightTargetGrams * KgPerGram, mixture.ProcessableUntilUtc));
        }

        return materials;
    }

    private MouldStatus? Mould(ProductionStationModel model, DateTimeOffset atUtc)
    {
        var mould = generator.Scenario.Moulds
            .Where(m => m.StationId == model.StationId && m.MountedUtc <= atUtc)
            .MaxBy(m => m.MountedUtc);
        if (mould is null) return null;
        var strokes = model.CountCycles(mould.MountedUtc, atUtc) / mould.Nests;
        var rate = model.CountCycles(atUtc - s_strokeRateWindow, atUtc) / s_strokeRateWindow.TotalMinutes / mould.Nests;
        return new MouldStatus(mould.Id, strokes, mould.StrokeLimit, rate);
    }

    private static double Occupancy(OvenModel oven, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var minutes = 0;
        var occupied = 0;
        for (var t = fromUtc; t < toUtc; t += TimeSpan.FromMinutes(1))
        {
            occupied += oven.OccupiedAt(t);
            minutes++;
        }

        return minutes == 0 ? 0 : (double)occupied / (oven.Spec.Places * minutes);
    }

    private static double? Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;
}
