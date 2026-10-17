using OPCAgentSample.Domain;

namespace OPCAgentSample.Kpi;

/// <summary>Grenzwerte der Stationsampel (Stammdaten „Grenzwerte“).</summary>
public sealed record TrafficLightThresholds(
    double PlanWarningBelow,
    double PlanProblemBelow,
    double OverproductionAbove,
    double CycleWarningAbove,
    double CycleProblemAbove,
    double FillWeightWarningGrams,
    double FillWeightProblemGrams,
    double MouldWarningAt,
    double MouldProblemAt,
    double MaterialWarningMinutes,
    double MaterialProblemMinutes,
    double OvenWarningAt)
{
    /// <summary>
    /// Standard: Ist/Soll &lt; 90 % Warnung, &lt; 75 % Problem; Überproduktion &gt; 105 % ohne Auftrag Warnung;
    /// Takt &gt; +5 % Warnung, &gt; +15 % Problem; Füllgewicht-Mittel ±1,0 g Warngrenze, ±2,0 g Eingriffsgrenze;
    /// Formstandzeit ab 90 % Warnung, ab 100 % Problem; Materialreichweite &lt; 60 min Warnung, &lt; 15 min Problem;
    /// Ofenbelegung ab 95 % Warnung.
    /// </summary>
    public static TrafficLightThresholds Default { get; } = new(
        PlanWarningBelow: 0.90,
        PlanProblemBelow: 0.75,
        OverproductionAbove: 1.05,
        CycleWarningAbove: 0.05,
        CycleProblemAbove: 0.15,
        FillWeightWarningGrams: 1.0,
        FillWeightProblemGrams: 2.0,
        MouldWarningAt: 0.90,
        MouldProblemAt: 1.0,
        MaterialWarningMinutes: 60,
        MaterialProblemMinutes: 15,
        OvenWarningAt: 0.95);
}

/// <summary>Messgrößen einer Station für die Ampel; nicht vorhandene Größen bleiben <c>null</c>.</summary>
public sealed record StationSignals(
    OperatingState State,
    double? PlanRatio = null,
    bool HasRunningOrder = true,
    double? CycleDeviation = null,
    double? FillWeightDeviationGrams = null,
    double? MouldWear = null,
    double? MaterialReachMinutes = null,
    double? OvenOccupancy = null);

public sealed record TrafficLightFinding(TrafficLight Light, string Text);

public sealed record TrafficLightAssessment(TrafficLight Light, IReadOnlyList<TrafficLightFinding> Findings);

/// <summary>
/// Ampel je Station aus Grenzwerten: jeder Messgröße wird eine Ampel zugeordnet, der schlechteste Wert gewinnt.
/// Wartet eine Station auf Vorstufe oder Material, ist ihr Ist/Soll ein Folgeproblem und wird höchstens als Warnung
/// bewertet. Überproduktion (Ist/Soll über Grenze ohne laufenden Auftrag) ist eine Warnung, auch wenn die OEE gut ist.
/// </summary>
public static class TrafficLightRules
{
    public static TrafficLightAssessment Evaluate(StationSignals signals, TrafficLightThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var t = thresholds ?? TrafficLightThresholds.Default;
        var findings = new List<TrafficLightFinding>();

        EvaluateState(signals, findings);
        EvaluatePlan(signals, t, findings);
        Add(findings, Above(signals.CycleDeviation, t.CycleWarningAbove, t.CycleProblemAbove), "Taktzeit über Soll");
        Add(findings, Above(Abs(signals.FillWeightDeviationGrams), t.FillWeightWarningGrams, t.FillWeightProblemGrams, inclusive: true), "Füllgewicht außerhalb Warngrenze");
        Add(findings, Above(signals.MouldWear, t.MouldWarningAt, t.MouldProblemAt, inclusive: true), "Formstandzeit");
        Add(findings, Below(signals.MaterialReachMinutes, t.MaterialWarningMinutes, t.MaterialProblemMinutes), "Materialreichweite");
        if (signals.OvenOccupancy >= t.OvenWarningAt) findings.Add(new(TrafficLight.Warning, "Ofen voll belegt"));

        return new TrafficLightAssessment(findings.Select(f => f.Light).Worst(), findings);
    }

    private static void EvaluateState(StationSignals signals, List<TrafficLightFinding> findings)
    {
        switch (signals.State)
        {
            case OperatingState.Fault:
                findings.Add(new(TrafficLight.Problem, "Störung"));
                break;
            case OperatingState.WaitingForUpstream:
                findings.Add(new(TrafficLight.Warning, "Wartet auf Vorstufe"));
                break;
            case OperatingState.WaitingForMaterial:
                findings.Add(new(TrafficLight.Warning, "Wartet auf Material"));
                break;
            default:
                break;
        }
    }

    private static void EvaluatePlan(StationSignals signals, TrafficLightThresholds t, List<TrafficLightFinding> findings)
    {
        if (signals.PlanRatio is not { } ratio) return;
        if (ratio > t.OverproductionAbove && !signals.HasRunningOrder)
        {
            findings.Add(new(TrafficLight.Warning, "Überproduktion ohne laufenden Auftrag"));
        }

        var light = ratio < t.PlanProblemBelow ? TrafficLight.Problem
            : ratio < t.PlanWarningBelow ? TrafficLight.Warning
            : TrafficLight.Running;
        var consequential = signals.State is OperatingState.WaitingForUpstream or OperatingState.WaitingForMaterial;
        if (consequential && light == TrafficLight.Problem) light = TrafficLight.Warning;
        if (light != TrafficLight.Running) findings.Add(new(light, "Ist/Soll unter Plan"));
    }

    private static void Add(List<TrafficLightFinding> findings, TrafficLight? light, string text)
    {
        if (light is { } value && value != TrafficLight.Running) findings.Add(new(value, text));
    }

    private static double? Abs(double? value) => value is { } v ? Math.Abs(v) : null;

    private static TrafficLight? Above(double? value, double warning, double problem, bool inclusive = false)
    {
        if (value is not { } v) return null;
        if (inclusive ? v >= problem : v > problem) return TrafficLight.Problem;
        return (inclusive ? v >= warning : v > warning) ? TrafficLight.Warning : TrafficLight.Running;
    }

    private static TrafficLight? Below(double? value, double warning, double problem) =>
        value is not { } v ? null
        : v < problem ? TrafficLight.Problem
        : v < warning ? TrafficLight.Warning
        : TrafficLight.Running;
}
