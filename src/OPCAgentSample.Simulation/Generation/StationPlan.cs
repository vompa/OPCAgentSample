using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulation.Script;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>Verlustereignis mit festem Beginn und Ende (aus Drehbuch, Rüsten oder Grundrauschen).</summary>
internal sealed record PlannedLoss(
    string Id,
    string StationId,
    LossCategory Category,
    string Reason,
    string Assembly,
    string Cause,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int AffectedPieces,
    bool Acknowledged,
    string? Note)
{
    public bool IsStandstill => Category is not (LossCategory.SpeedLoss or LossCategory.Overproduction);

    public LossEvent ToEvent(DateTimeOffset timestampUtc, bool closed) =>
        new(StationId, timestampUtc, Id, Assembly, Category, Reason, Cause, StartUtc, closed ? EndUtc : null,
            AffectedPieces, Acknowledged, Note);

    public OperatingState State => Category switch
    {
        LossCategory.Breakdown or LossCategory.MinorStop => OperatingState.Fault,
        LossCategory.MaterialWait => OperatingState.WaitingForMaterial,
        LossCategory.UpstreamWait => OperatingState.WaitingForUpstream,
        LossCategory.Setup or LossCategory.SetupOverrun => OperatingState.Setup,
        LossCategory.PlannedMaintenance => OperatingState.PlannedMaintenance,
        _ => OperatingState.Idle,
    };

    public static PlannedLoss From(ScriptedLoss loss) =>
        new(loss.Id, loss.StationId, loss.Category, loss.Reason, loss.Assembly, loss.Cause, loss.StartUtc, loss.EndUtc,
            loss.AffectedPieces, loss.Acknowledged, loss.Note);

    public static IEnumerable<PlannedLoss> From(ScriptedSetup setup)
    {
        var plannedEnd = setup.StartUtc + setup.Planned;
        yield return new(setup.Id, setup.StationId, LossCategory.Setup, LossReasons.Setup, Assemblies.Mould,
            "Rüsten (Soll)", setup.StartUtc, plannedEnd, 0, true, null);
        if (setup.Overrun > TimeSpan.Zero)
        {
            yield return new($"{setup.Id}-U", setup.StationId, LossCategory.SetupOverrun, LossReasons.SetupOverrun,
                Assemblies.Mould, $"Rüsten {setup.Overrun.TotalMinutes:0} min über Soll", plannedEnd, plannedEnd + setup.Overrun,
                0, true, null);
        }
    }
}

/// <summary>Zeitlich sortierte Drehbuchdaten einer Produktionsstation (Rundtisch oder Verpackung).</summary>
internal sealed class StationPlan
{
    public StationPlan(ScenarioDefinition scenario, ProductionStationSpec spec, int index)
    {
        Spec = spec;
        Index = index;
        var id = spec.StationId;
        Losses = [.. scenario.Losses.Where(l => l.StationId == id).Select(PlannedLoss.From)
            .Concat(scenario.Setups.Where(s => s.StationId == id).SelectMany(PlannedLoss.From))
            .OrderBy(l => l.StartUtc)];
        Blocks = [.. Losses.Where(l => l.IsStandstill)];
        Assignments = [.. scenario.OrderAssignments.Where(a => a.StationId == id).OrderBy(a => a.StartUtc)];
        Batches = [.. scenario.BatchAssignments.Where(b => b.StationId == id).OrderBy(b => b.StartUtc)];
        Targets = [.. scenario.OutputTargets.Where(t => t.StationId == id).OrderBy(t => t.StartUtc)];
        ScrapWindows = [.. scenario.ScrapWindows.Where(w => w.StationId == id)];
        FillShifts = [.. scenario.FillWeightShifts.Where(f => f.StationId == id)];
        Drift = scenario.BuildUpDrifts.FirstOrDefault(d => d.StationId == id);
        MixtureDeliveries = [.. scenario.MixtureDeliveries.Where(d => d.StationId == id).OrderBy(d => d.DeliveredUtc)];
        Consumables = [.. scenario.Consumables.Where(c => c.StationId == id)];
    }

    public ProductionStationSpec Spec { get; }

    public string StationId => Spec.StationId;

    public int Index { get; }

    public IReadOnlyList<PlannedLoss> Losses { get; }

    public IReadOnlyList<PlannedLoss> Blocks { get; }

    public IReadOnlyList<OrderAssignment> Assignments { get; }

    public IReadOnlyList<BatchAssignment> Batches { get; }

    public IReadOnlyList<OutputTarget> Targets { get; }

    public IReadOnlyList<ScrapWindow> ScrapWindows { get; }

    public IReadOnlyList<FillWeightShift> FillShifts { get; }

    public BuildUpDrift? Drift { get; }

    public IReadOnlyList<MixtureDelivery> MixtureDeliveries { get; }

    public IReadOnlyList<ConsumableSpec> Consumables { get; }

    public OrderAssignment AssignmentAt(DateTimeOffset instant) =>
        Assignments.LastOrDefault(a => a.StartUtc <= instant) ?? Assignments[0];

    public string BatchAt(DateTimeOffset instant) =>
        (Batches.LastOrDefault(b => b.StartUtc <= instant) ?? (Batches.Count > 0 ? Batches[0] : null))?.BatchId ?? string.Empty;

    public double FillWeightOffsetAt(DateTimeOffset instant)
    {
        var offset = 0.0;
        foreach (var shift in FillShifts)
        {
            if (instant < shift.StartUtc) continue;
            var progress = shift.Ramp <= TimeSpan.Zero ? 1.0 : Math.Min(1.0, (instant - shift.StartUtc) / shift.Ramp);
            offset += shift.OffsetGrams * progress;
        }

        return offset;
    }

    public double BuildUpAt(DateTimeOffset instant, double nominal) => Drift?.BuildUpAt(instant) ?? nominal;

    /// <summary>Taktverlängerung durch die Drift des Presskraftaufbaus (Sekunden).</summary>
    public double CycleDeltaAt(DateTimeOffset instant) =>
        Drift is null ? 0 : (Drift.BuildUpAt(instant) - Drift.FromSeconds) * Drift.CycleSecondsPerBuildUpSecond;

    public ScrapWindow? ScrapWindowAt(DateTimeOffset instant) =>
        ScrapWindows.LastOrDefault(w => instant >= w.StartUtc && instant < w.EndUtc);

    public PlannedLoss? BlockAt(DateTimeOffset instant) =>
        Blocks.FirstOrDefault(b => instant >= b.StartUtc && instant < b.EndUtc);
}
