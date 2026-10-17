using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Rauschfreier Messtakt einer Station innerhalb einer Schicht (Zeit in Sekunden ab Schichtbeginn): Soll-Takt des
/// laufenden Artikels mit Grundverlangsamung, Anlaufstreckung und Drift des Presskraftaufbaus.
/// </summary>
internal sealed class CycleProfile
{
    private readonly double[] _changeAt;
    private readonly double[] _baseCycle;
    private readonly double _startupStart;
    private readonly double _startupEnd;
    private readonly double _stretch;
    private readonly double _speed;
    private readonly double _driftStart = double.PositiveInfinity;
    private readonly double _driftSlope;
    private readonly double _driftCap;

    public CycleProfile(ScenarioDefinition scenario, StationPlan plan, ProductionPeriod shift, TimeWindow startup)
    {
        var changes = plan.Assignments
            .Where(a => a.StartUtc > shift.StartUtc && a.StartUtc < shift.EndUtc)
            .Select(a => (At: (a.StartUtc - shift.StartUtc).TotalSeconds, a.ArticleId))
            .Prepend((At: 0.0, plan.AssignmentAt(shift.StartUtc).ArticleId))
            .ToArray();
        _changeAt = [.. changes.Select(c => c.At)];
        _baseCycle = [.. changes.Select(c => TargetCycle(scenario, plan, c.ArticleId) * (1 + plan.Spec.BaseSlowdown))];
        _startupStart = (startup.StartUtc - shift.StartUtc).TotalSeconds;
        _startupEnd = (startup.EndUtc - shift.StartUtc).TotalSeconds;
        _stretch = scenario.Model.StartupCycleStretch;
        _speed = scenario.Model.StartupSpeedFactor;
        if (plan.Drift is { } drift)
        {
            _driftStart = (drift.StartUtc - shift.StartUtc).TotalSeconds;
            _driftSlope = (drift.ToSeconds - drift.FromSeconds) / drift.Ramp.TotalSeconds * drift.CycleSecondsPerBuildUpSecond;
            _driftCap = (drift.CapSeconds - drift.FromSeconds) * drift.CycleSecondsPerBuildUpSecond;
        }
    }

    public static double TargetCycle(ScenarioDefinition scenario, StationPlan plan, string articleId) =>
        plan.Spec.IsPress ? scenario.Article(articleId).TargetCycleSeconds : scenario.PackagingArticle(articleId).TargetCycleSeconds;

    public bool InStartup(double seconds) => seconds >= _startupStart && seconds < _startupEnd;

    /// <summary>Gemessener Takt ohne Rauschen.</summary>
    public double MeasuredCycle(double seconds)
    {
        var index = 0;
        while (index + 1 < _changeAt.Length && seconds >= _changeAt[index + 1]) index++;
        var cycle = _baseCycle[index] * (InStartup(seconds) ? _stretch : 1);
        if (seconds > _driftStart) cycle += Math.Min(_driftCap, _driftSlope * (seconds - _driftStart));
        return cycle;
    }

    /// <summary>Laufzeit bis zum nächsten Takt beim gegebenen Nutzungsgrad.</summary>
    public double Interval(double seconds, double utilization) =>
        MeasuredCycle(seconds) / (utilization * (InStartup(seconds) ? _speed : 1));

    /// <summary>Takte innerhalb der Laufzeitfenster; optional werden die Taktzeitpunkte gesammelt.</summary>
    public int Simulate((double Start, double End)[] windows, double utilization, List<double>? output)
    {
        if (windows.Length == 0) return 0;
        var count = 0;
        var index = 0;
        var time = windows[0].Start;
        while (true)
        {
            var remaining = Interval(time, utilization);
            while (remaining >= windows[index].End - time)
            {
                remaining -= windows[index].End - time;
                index++;
                if (index >= windows.Length) return count;
                time = windows[index].Start;
            }

            time += remaining;
            count++;
            output?.Add(time);
        }
    }
}
