using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Live;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Voraussichtlicher Verlust durch eine Taktverlängerung bis Schichtende (Stück) ab dem Beginn der Drift.</summary>
public sealed record SpeedLossForecast(DateTimeOffset OnsetUtc, DateTimeOffset ShiftEndUtc, double Pieces, double CycleSeconds, double TargetSeconds)
{
    public string Text =>
        $"Bei gleichbleibendem Takt (Ø {Views.Num(CycleSeconds)} s statt {Views.Num(TargetSeconds)} s) fehlen von {Fmt.Time(OnsetUtc)} "
        + $"bis Schichtende {Fmt.Time(ShiftEndUtc)} ca. {Math.Round(Pieces / 10) * 10:0} Stück.";

    /// <summary>Verlust = Laufzeit ab Driftbeginn bis Schichtende × (1/Soll-Takt − 1/Ist-Takt), gerechnet mit der Kennzahlen-Bibliothek.</summary>
    public static SpeedLossForecast? Compute(StationState state, DriftResult? drift, DateTimeOffset shiftEndUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (drift is not { Detected: true, OnsetUtc: { } onset } || state.CycleSeconds is not { } cycle || state.TargetCycleSeconds is not { } target || target <= 0) return null;

        var pieces = PlanFulfillment.SpeedLossPieces(shiftEndUtc - onset, cycle, target);
        return pieces > 0 ? new SpeedLossForecast(onset, shiftEndUtc, pieces, cycle, target) : null;
    }
}

/// <summary>
/// Ergebnis der Driftbewertung einer Zeitreihe: Ausgangswert (Mittel des ersten Drittels), letzter Wert, Änderung in Prozent und
/// der geschätzte Beginn der Drift. <see cref="Detected"/> ist wahr, wenn die letzten Abschnitte dauerhaft außerhalb der
/// Streuung des Ausgangswerts liegen.
/// </summary>
public sealed record DriftResult(
    bool Detected, double Baseline, double Latest, double ChangePercent, DateTimeOffset? OnsetUtc, string Direction);

/// <summary>
/// Deterministische Driftprüfung auf verdichteten Abschnitten: Ausgangswert und Streuung stammen aus dem ersten Drittel; die
/// Drift beginnt dort, wo die Abschnitte bis zum Ende dauerhaft mehr als das Vierfache der Streuung (mindestens 0,3 %) vom
/// Ausgangswert abweichen (Streuung robust über den Median der Abweichungen). Steigt die Abweichung stetig, wird der Beginn über die Ausgleichsgerade zurück zum Ausgangswert
/// geschätzt, sonst gilt der Beginn des ersten abweichenden Abschnitts.
/// </summary>
public static class DriftDetector
{
    private const int MinBuckets = 6;
    private const double SigmaFactor = 4;
    private const double MinRelativeThreshold = 0.003;

    public static DriftResult? Detect(IReadOnlyList<SeriesBucket> buckets)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var usable = buckets.Where(b => b.Count > 0).ToList();
        if (usable.Count < MinBuckets) return null;

        var baselineCount = Math.Max(3, usable.Count / 3);
        var head = usable.Take(baselineCount).Select(b => b.Mean).OrderBy(v => v).ToList();
        var baseline = head[head.Count / 2];

        // Robuste Streuung (Median der Abweichungen): Der Anlauf nach dem Schichtwechsel im ersten Abschnitt verfälscht sie nicht.
        var deviations = head.Select(v => Math.Abs(v - baseline)).OrderBy(v => v).ToList();
        var sigma = 1.4826 * deviations[deviations.Count / 2];
        var threshold = Math.Max(SigmaFactor * sigma, MinRelativeThreshold * Math.Abs(baseline));

        var latest = usable[^1].Mean;
        var sign = Math.Sign(latest - baseline);
        double Deviation(SeriesBucket bucket) => sign * (bucket.Mean - baseline);

        var runStart = usable.Count;
        for (var i = usable.Count - 1; i >= baselineCount; i--)
        {
            if (Deviation(usable[i]) <= threshold) break;
            runStart = i;
        }

        var change = baseline != 0 ? (latest - baseline) / Math.Abs(baseline) * 100 : 0;
        var direction = sign > 0 ? "steigend" : sign < 0 ? "fallend" : "unverändert";
        if (sign == 0 || usable.Count - runStart < 2) return new DriftResult(false, baseline, latest, change, null, "unverändert");

        var onset = usable[runStart].StartUtc;
        var run = usable.Skip(runStart).ToList();
        // Eine Rampe beginnt nahe am Ausgangswert und wächst; ein Sprung liegt schon im ersten Abschnitt nahe am Endwert.
        var isRamp = Deviation(run[0]) < 0.5 * Deviation(run[^1]);
        if (run.Count >= 3 && isRamp && Deviation(run[^1]) - Deviation(run[0]) > 2 * threshold)
        {
            var estimate = Extrapolate(run, baseline);
            var previousStart = runStart > 0 ? usable[runStart - 1].StartUtc : onset;
            if (estimate is { } t && t < onset) onset = t > previousStart ? t : previousStart;
        }

        return new DriftResult(true, baseline, latest, change, onset, direction);
    }

    /// <summary>Zeitpunkt, zu dem die Ausgleichsgerade durch die Abschnitte den Ausgangswert schneidet.</summary>
    private static DateTimeOffset? Extrapolate(List<SeriesBucket> run, double baseline)
    {
        var origin = run[0].StartUtc;
        var xs = run.Select(b => (b.StartUtc + ((b.EndUtc - b.StartUtc) / 2) - origin).TotalMinutes).ToList();
        var ys = run.Select(b => b.Mean).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        if (denominator <= 0) return null;

        var slope = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum() / denominator;
        if (Math.Abs(slope) < double.Epsilon) return null;

        var crossing = meanX + ((baseline - meanY) / slope);
        return origin + TimeSpan.FromMinutes(crossing);
    }
}
