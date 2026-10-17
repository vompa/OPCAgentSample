using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulation.Script;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Rechnet eine Schicht einer Produktionsstation: Stillstände aus dem Drehbuch, Anlaufverlust nach Schichtwechsel,
/// Kurzstopps des Grundrauschens (Lage nach Seed), Taktzeitpunkte und Ausschussmarken.
/// </summary>
/// <remarks>
/// Taktmodell: Ein Takt dauert <c>Messtakt / (Nutzungsgrad × Anlauffaktor)</c> Laufzeit; Stillstände unterbrechen.
/// Messtakt = Soll-Takt × (1 + Grundverlangsamung) (im Anlauf gestreckt) + Driftanteil. Der Nutzungsgrad steht für
/// Mikrostopps unter einer Minute, die nicht als Ereignis erfasst werden. Für Fenster mit Stückzahl-Vorgabe wird er
/// per Bisektion so bestimmt, dass die Vorgabe exakt getroffen wird. Ausschuss entsteht deterministisch über eine
/// akkumulierte Quote, der Grund (außer in Drehbuch-Fenstern) zufällig.
/// </remarks>
internal sealed class ShiftRunBuilder
{
    private const double MinUtilization = 0.05;
    private const double MaxUtilization = 3.0;
    private const int BisectionSteps = 60;
    private static readonly TimeSpan s_stopMargin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_endOfShiftMargin = TimeSpan.FromMinutes(5);

    private static readonly (string Reason, string Assembly)[] s_pressMinorStops =
    [
        ("Kurzstopp Abstreifer", Assemblies.Stripper),
        ("Kurzstopp Ausstoß", Assemblies.Ejector),
        ("Kurzstopp Waage", Assemblies.Dosing),
        ("Kurzstopp Greifer 1", Assemblies.FabricFeed1),
        ("Kurzstopp Sensor Presse", Assemblies.Press),
    ];

    private static readonly (string Reason, string Assembly)[] s_packagingMinorStops =
    [
        ("Kurzstopp Kartonaufrichter", Assemblies.CartonErector),
        ("Kurzstopp Kontrollwaage", Assemblies.Checkweigher),
        ("Kurzstopp Umreifer", Assemblies.Strapper),
    ];

    private readonly ScenarioDefinition _scenario;
    private readonly StationPlan _plan;
    private readonly int _seed;

    public ShiftRunBuilder(ScenarioDefinition scenario, StationPlan plan, int seed)
    {
        _scenario = scenario;
        _plan = plan;
        _seed = seed;
    }

    public ShiftRun Build(ProductionPeriod shift)
    {
        var random = DeterministicRandom.For(_seed, _plan.StationId, shift.StartUtc.UtcTicks);
        var blocks = _plan.Blocks
            .Where(b => b.StartUtc < shift.EndUtc && b.EndUtc > shift.StartUtc)
            .Select(b => new TimeWindow(b.StartUtc, b.EndUtc).Intersect(shift.StartUtc, shift.EndUtc))
            .ToList();
        var startup = StartupWindow(shift, blocks);
        var noiseStops = NoiseStops(shift, blocks, startup, random);
        var running = Subtract(new TimeWindow(shift.StartUtc, shift.EndUtc), [.. blocks, .. noiseStops.Select(s => new TimeWindow(s.StartUtc, s.EndUtc))]);
        var profile = new CycleProfile(_scenario, _plan, shift, startup);

        var segments = new List<RateSegment>();
        var cycles = new List<double>();
        double? carried = null;
        foreach (var (start, end, target) in SegmentBounds(shift))
        {
            var windows = Clip(running, start, end, shift.StartUtc);
            var utilization = target?.Pieces is { } pieces
                ? SolveUtilization(profile, windows, pieces)
                : carried ?? _plan.Spec.DefaultUtilization;
            carried = target is { ContinueAfter: true } ? utilization : null;
            segments.Add(new RateSegment(start, end, utilization, target?.Pieces));
            profile.Simulate(windows, utilization, cycles);
        }

        var ticks = cycles.Select(seconds => shift.StartUtc.UtcTicks + (long)Math.Round(seconds * TimeSpan.TicksPerSecond)).ToArray();
        var (scrap, reasons) = ScrapMarks(ticks, segments, startup);
        return new ShiftRun(shift, PlannedQuantity(shift), startup, noiseStops, segments, profile, ticks, scrap, reasons);
    }

    public int PlannedQuantity(ProductionPeriod shift)
    {
        var overridePlan = _scenario.PlanOverrides.FirstOrDefault(o => o.StationId == _plan.StationId && o.ShiftKey == shift.Key);
        if (overridePlan is not null) return overridePlan.PlannedQuantity;
        return _scenario.Calendar.ShiftOf(shift.StartUtc) switch
        {
            Shift.Early => _plan.Spec.PlanEarly,
            Shift.Late => _plan.Spec.PlanLate,
            _ => _plan.Spec.PlanNight,
        };
    }

    private TimeWindow StartupWindow(ProductionPeriod shift, IReadOnlyList<TimeWindow> blocks)
    {
        var start = shift.StartUtc;
        var moved = true;
        while (moved)
        {
            moved = false;
            foreach (var block in blocks.Where(b => b.Contains(start)))
            {
                start = block.EndUtc;
                moved = true;
            }
        }

        if (start >= shift.EndUtc) return new TimeWindow(shift.EndUtc, shift.EndUtc);
        var model = _scenario.Model;
        var spread = (int)(model.MaxStartupLoss - model.MinStartupLoss).TotalMinutes + 1;
        var dayNumber = shift.StartUtc.UtcDateTime.DayOfYear;
        var variant = ((_plan.Index * 7) + (dayNumber * 3) + (int)_scenario.Calendar.ShiftOf(shift.StartUtc)) % spread;
        var end = start + model.MinStartupLoss + TimeSpan.FromMinutes(variant);
        return new TimeWindow(start, end < shift.EndUtc ? end : shift.EndUtc);
    }

    private List<PlannedLoss> NoiseStops(ProductionPeriod shift, IReadOnlyList<TimeWindow> blocks, TimeWindow startup, Random random)
    {
        var model = _scenario.Model;
        var count = model.MinorStopsPerShift;
        var weights = Enumerable.Range(0, count).Select(_ => 0.5 + random.NextDouble()).ToArray();
        var positions = Enumerable.Range(0, count).Select(_ => random.NextDouble()).ToArray();
        var reasonPicks = Enumerable.Range(0, count).Select(_ => random.NextDouble()).ToArray();

        var excluded = blocks.Select(b => new TimeWindow(b.StartUtc - s_stopMargin, b.EndUtc + s_stopMargin))
            .Append(startup)
            .Append(new TimeWindow(shift.EndUtc - s_endOfShiftMargin, shift.EndUtc))
            .Concat(_scenario.QuietWindows.Select(q => new TimeWindow(q.StartUtc, q.EndUtc)))
            .ToList();
        var catalog = _plan.Spec.IsPress ? s_pressMinorStops : s_packagingMinorStops;
        var stops = new List<PlannedLoss>();
        for (var i = 0; i < count; i++)
        {
            var duration = TimeSpan.FromSeconds(Math.Round(model.MinorStopTotalPerShift.TotalSeconds * weights[i] / weights.Sum()));
            var eligible = Subtract(new TimeWindow(shift.StartUtc, shift.EndUtc), excluded)
                .Select(w => new TimeWindow(w.StartUtc, w.EndUtc - duration))
                .Where(w => !w.IsEmpty)
                .ToList();
            var total = TimeWindow.TotalDuration(eligible);
            if (total <= TimeSpan.Zero) continue;

            var start = PickInstant(eligible, total * positions[i]);
            start = new DateTimeOffset(start.UtcTicks - (start.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
            var (reason, assembly) = catalog[(int)(reasonPicks[i] * catalog.Length)];
            var stop = new PlannedLoss($"K-{_plan.StationId}-{shift.Key}-{i + 1}", _plan.StationId, LossCategory.MinorStop,
                reason, assembly, reason, start, start + duration, 0, true, null);
            stops.Add(stop);
            excluded.Add(new TimeWindow(stop.StartUtc - s_stopMargin, stop.EndUtc + s_stopMargin));
        }

        return [.. stops.OrderBy(s => s.StartUtc)];
    }

    private IEnumerable<(DateTimeOffset Start, DateTimeOffset End, OutputTarget? Target)> SegmentBounds(ProductionPeriod shift)
    {
        var targets = _plan.Targets.Where(t => t.StartUtc < shift.EndUtc && t.EndUtc > shift.StartUtc).ToList();
        var cursor = shift.StartUtc;
        foreach (var target in targets)
        {
            var start = target.StartUtc > shift.StartUtc ? target.StartUtc : shift.StartUtc;
            var end = target.EndUtc < shift.EndUtc ? target.EndUtc : shift.EndUtc;
            if (start > cursor) yield return (cursor, start, null);
            yield return (start, end, target);
            cursor = end;
        }

        if (cursor < shift.EndUtc) yield return (cursor, shift.EndUtc, null);
    }

    private static double SolveUtilization(CycleProfile profile, (double Start, double End)[] windows, int pieces)
    {
        var atLeast = Smallest(profile, windows, pieces);
        var atLeastNext = Smallest(profile, windows, pieces + 1);
        return (atLeast + atLeastNext) / 2;
    }

    /// <summary>Kleinster Nutzungsgrad, bei dem mindestens <paramref name="pieces"/> Takte entstehen.</summary>
    private static double Smallest(CycleProfile profile, (double Start, double End)[] windows, int pieces)
    {
        var low = MinUtilization;
        var high = MaxUtilization;
        for (var step = 0; step < BisectionSteps; step++)
        {
            var middle = (low + high) / 2;
            if (profile.Simulate(windows, middle, null) >= pieces) high = middle;
            else low = middle;
        }

        return high;
    }

    private (bool[] Scrap, ScrapReason?[] Reasons) ScrapMarks(long[] ticks, List<RateSegment> segments, TimeWindow startup)
    {
        var model = _scenario.Model;
        var scrap = new bool[ticks.Length];
        var reasons = new ScrapReason?[ticks.Length];
        var segmentIndex = -1;
        var accumulated = 0.0;
        for (var i = 0; i < ticks.Length; i++)
        {
            var instant = new DateTimeOffset(ticks[i], TimeSpan.Zero);
            var current = segmentIndex;
            while (segmentIndex + 1 < segments.Count && instant >= segments[segmentIndex + 1].StartUtc) segmentIndex++;
            if (segmentIndex != current) accumulated = 0;

            var window = _plan.ScrapWindowAt(instant);
            var rate = window?.Rate ?? BaseRate(startup.Contains(instant), model);
            var before = Math.Floor(accumulated + 0.5);
            accumulated += rate;
            scrap[i] = Math.Floor(accumulated + 0.5) > before;
            reasons[i] = window?.Reason;
        }

        return (scrap, reasons);
    }

    private double BaseRate(bool inStartup, ModelParameters model)
    {
        var rate = inStartup ? model.StartupScrapRate : model.BaseScrapRate;
        return _plan.Spec.IsPress ? rate : model.PackagingScrapRate;
    }

    private static DateTimeOffset PickInstant(IReadOnlyList<TimeWindow> windows, TimeSpan offset)
    {
        foreach (var window in windows)
        {
            if (offset < window.Duration) return window.StartUtc + offset;
            offset -= window.Duration;
        }

        return windows[^1].StartUtc;
    }

    internal static List<TimeWindow> Subtract(TimeWindow whole, IEnumerable<TimeWindow> holes)
    {
        var result = new List<TimeWindow> { whole };
        foreach (var hole in holes.Where(h => !h.IsEmpty))
        {
            var next = new List<TimeWindow>();
            foreach (var part in result)
            {
                if (hole.EndUtc <= part.StartUtc || hole.StartUtc >= part.EndUtc)
                {
                    next.Add(part);
                    continue;
                }

                if (hole.StartUtc > part.StartUtc) next.Add(new TimeWindow(part.StartUtc, hole.StartUtc));
                if (hole.EndUtc < part.EndUtc) next.Add(new TimeWindow(hole.EndUtc, part.EndUtc));
            }

            result = next;
        }

        return [.. result.OrderBy(w => w.StartUtc)];
    }

    private static (double Start, double End)[] Clip(IReadOnlyList<TimeWindow> running, DateTimeOffset from, DateTimeOffset to, DateTimeOffset origin) =>
        [.. running.Select(w => w.Intersect(from, to))
            .Where(w => !w.IsEmpty)
            .Select(w => ((w.StartUtc - origin).TotalSeconds, (w.EndUtc - origin).TotalSeconds))];
}
