using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Kpi;

namespace OPCAgentSample.Server.Live;

/// <summary>
/// Kurztext je Station für die Anlagenübersicht („Mischung A60 noch 38 min“, „Form 94 % · Füllgewicht ↑“). Er fasst die
/// wichtigsten Befunde der Ampel zusammen: schwerste zuerst, höchstens zwei Teile. Grenzen kommen aus
/// <see cref="TrafficLightThresholds"/>, damit Text und Ampel nie auseinanderlaufen.
/// </summary>
internal static class StationMessages
{
    private const int MaxParts = 2;
    private static readonly TrafficLightThresholds s_limits = TrafficLightThresholds.Default;

    public static string Compose(StationState station, Station layout, bool breakdownInShift, string shiftStartText)
    {
        var parts = new List<(TrafficLight Light, string Text)>();
        AddStatePart(station, layout, parts);
        AddMaterialPart(station, parts);
        AddMouldPart(station, parts);
        AddFillWeightPart(station, parts);
        AddCyclePart(station, parts);
        if (station.Occupancy >= s_limits.OvenWarningAt) parts.Add((TrafficLight.Warning, "Voll"));
        if (station.Findings.Any(f => f.Text.StartsWith("Überproduktion", StringComparison.Ordinal)))
        {
            parts.Add((TrafficLight.Warning, "Überproduktion"));
        }

        var plan = PlanPart(station, breakdownInShift);
        if (plan is { Light: TrafficLight.Problem }) parts.Insert(0, plan.Value);
        else if (plan is { } warning && parts.Count == 0) parts.Add(warning); // Rückstand ist nur dann der Hauptgrund, wenn nichts Konkreteres vorliegt

        var texts = parts.OrderByDescending(p => p.Light).Select(p => p.Text).Take(MaxParts).ToList();
        return texts.Count > 0 ? string.Join(" · ", texts) : DefaultText(station, layout, shiftStartText);
    }

    private static void AddStatePart(StationState station, Station layout, List<(TrafficLight, string)> parts)
    {
        switch (station.State)
        {
            case OperatingState.Fault:
                parts.Add((TrafficLight.Problem, "Störung"));
                break;
            case OperatingState.WaitingForUpstream:
                parts.Add((TrafficLight.Warning, layout.Type == StationType.Packaging ? "Wartet auf Ofencharge" : "Wartet auf Vorstufe"));
                break;
            case OperatingState.WaitingForMaterial:
                parts.Add((TrafficLight.Warning, "Wartet auf Material"));
                break;
            default:
                break;
        }
    }

    private static void AddMaterialPart(StationState station, List<(TrafficLight, string)> parts)
    {
        if (station.ReachMinutes is not { } reach || reach >= s_limits.MaterialWarningMinutes) return;

        var light = reach < s_limits.MaterialProblemMinutes ? TrafficLight.Problem : TrafficLight.Warning;
        parts.Add((light, string.Create(CultureInfo.InvariantCulture, $"{station.ReachMaterial} noch {(int)Math.Round(reach)} min")));
    }

    private static void AddMouldPart(StationState station, List<(TrafficLight, string)> parts)
    {
        if (station.MouldWear is not { } wear || wear < s_limits.MouldWarningAt) return;

        var light = wear >= s_limits.MouldProblemAt ? TrafficLight.Problem : TrafficLight.Warning;
        parts.Add((light, string.Create(CultureInfo.InvariantCulture, $"Form {OeeResult.Percent(wear)} %")));
    }

    private static void AddFillWeightPart(StationState station, List<(TrafficLight, string)> parts)
    {
        if (station.FillWeightMeanGrams is not { } mean || station.FillWeightTargetGrams is not { } target) return;

        var deviation = mean - target;
        if (Math.Abs(deviation) < s_limits.FillWeightWarningGrams) return;

        var light = Math.Abs(deviation) >= s_limits.FillWeightProblemGrams ? TrafficLight.Problem : TrafficLight.Warning;
        parts.Add((light, deviation > 0 ? "Füllgewicht ↑" : "Füllgewicht ↓"));
    }

    private static void AddCyclePart(StationState station, List<(TrafficLight, string)> parts)
    {
        if (station.CycleSeconds is not { } cycle || station.TargetCycleSeconds is not { } target || target <= 0) return;

        var deviation = (cycle / target) - 1;
        if (deviation <= s_limits.CycleWarningAbove) return;

        var light = deviation > s_limits.CycleProblemAbove ? TrafficLight.Problem : TrafficLight.Warning;
        parts.Add((light, string.Create(CultureInfo.InvariantCulture, $"Taktzeit +{OeeResult.Percent(deviation)} %")));
    }

    private static (TrafficLight Light, string Text)? PlanPart(StationState station, bool breakdownInShift)
    {
        var waiting = station.State is OperatingState.WaitingForUpstream or OperatingState.WaitingForMaterial;
        if (waiting || station.PlanRatio is not { } ratio || ratio >= s_limits.PlanWarningBelow) return null;

        var light = ratio < s_limits.PlanProblemBelow ? TrafficLight.Problem : TrafficLight.Warning;
        return (light, breakdownInShift ? "Rückstand nach Störung" : "Rückstand");
    }

    private static string DefaultText(StationState station, Station layout, string shiftStartText) => layout.Type switch
    {
        StationType.Oven => "Temperaturprofil im Soll",
        StationType.Packaging => $"{Thousands(station.PiecesActual)} Kartons seit {shiftStartText}",
        _ => "Läuft im Plan",
    };

    /// <summary>Tausendertrennzeichen wie in der Oberfläche (1.240), unabhängig von der Kultur des Rechners.</summary>
    private static string Thousands(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
}
