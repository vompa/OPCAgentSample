using System.Globalization;
using System.Text.RegularExpressions;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Live;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Gemeinsame Darstellung von Zuständen, Bereichen und Zeiträumen in den Tool-Ergebnissen (deutsche Begriffe).</summary>
public static partial class Views
{
    public static string StateWord(OperatingState state) => state switch
    {
        OperatingState.Running => "Läuft",
        OperatingState.Idle => "Leerlauf",
        OperatingState.Setup => "Rüsten",
        OperatingState.Fault => "Störung",
        OperatingState.PlannedMaintenance => "Wartung geplant",
        OperatingState.WaitingForMaterial => "Wartet auf Material",
        OperatingState.WaitingForUpstream => "Wartet auf Vorstufe",
        OperatingState.RunningWithoutOrder => "Läuft ohne Auftrag",
        _ => state.ToString(),
    };

    public static string AreaWord(Area area) => area switch
    {
        Area.Pressing => "Pressen",
        Area.Buffer => "Puffer",
        Area.Curing => "Öfen",
        Area.Packaging => "Verpackung",
        _ => area.ToString(),
    };

    public static string TypeWord(StationType type) => type switch
    {
        StationType.RotaryTable => "Rundtisch",
        StationType.Oven => "Ofen",
        StationType.Packaging => "Verpackung",
        _ => "Puffer",
    };

    public static string AlarmKindWord(AlarmKind kind) => kind switch
    {
        AlarmKind.Fault => "Störung",
        AlarmKind.ConditionWarning => "Zustandswarnung",
        AlarmKind.ProcessWarning => "Prozesswarnung",
        _ => "Hinweis",
    };

    public static string SeverityWord(AlarmSeverity severity) => severity switch
    {
        AlarmSeverity.High => "hoch",
        AlarmSeverity.Medium => "mittel",
        AlarmSeverity.Low => "niedrig",
        _ => "info",
    };

    /// <summary>Rang einer Meldung für den Meldungsfilter der Rolle: Störung kritisch, Warnungen Warnung, Hinweis Info.</summary>
    public static int AlarmRank(AlarmKind kind) => kind switch
    {
        AlarmKind.Fault => Rating.Rank(Rating.Crit),
        AlarmKind.ConditionWarning or AlarmKind.ProcessWarning => Rating.Rank(Rating.Warn),
        _ => 0,
    };

    public static string AlarmRating(AlarmKind kind) => kind switch
    {
        AlarmKind.Fault => Rating.Crit,
        AlarmKind.ConditionWarning or AlarmKind.ProcessWarning => Rating.Warn,
        _ => Rating.Info,
    };

    public static string LightWord(TrafficLight light) => light.Label();

    /// <summary>Zeitraum als Objekt für die Ergebnisse: Beschriftung, Beginn und Ende in Ortszeit.</summary>
    public static object RangeInfo(ResolvedRange range, DateTimeOffset now) => new
    {
        Bezeichnung = range.Label,
        Von = Fmt.Stamp(range.StartUtc),
        Bis = Fmt.Stamp(range.EffectiveEnd(now)),
        Laufend = range.IsOpen(now),
    };

    /// <summary>Station als Zeile der Hallenübersicht.</summary>
    public static object StationRow(StationState state, Station station, DateTimeOffset now) => new
    {
        Id = state.StationId,
        station.Name,
        Bereich = AreaWord(station.Area),
        Typ = TypeWord(station.Type),
        Ampel = LightWord(state.Light),
        Bewertung = RatingText.From(state.Light),
        Zustand = StateWord(state.State),
        ZustandSeit = Fmt.Stamp(state.SinceUtc),
        Dauer = Fmt.Duration(now - state.SinceUtc),
        Auftrag = state.OrderId,
        Artikel = state.ArticleLabel.Length > 0 ? state.ArticleLabel : null,
        Kennzahl = new { Bezeichnung = state.KpiUnit, Wert = state.KpiValue },
        IstSollProzent = state.PlanRatio is { } ratio ? Fmt.Percent(ratio) : (int?)null,
        Meldung = state.Message,
        Befunde = state.Findings.Select(f => new { Ampel = LightWord(f.Light), f.Text }),
    };

    /// <summary>
    /// Ursachenketten der Halle: Wartet eine Verpackung auf die Vorstufe, ein Ofen ist voll belegt und eine Presse mit demselben
    /// Durchmesser hat eine Warnung, hängen sie zusammen (Presse → Ofen → Verpackung). Eigenständig ist nur ein Problem ohne Kette.
    /// </summary>
    public static IReadOnlyList<object> Chains(IReadOnlyList<StationState> states)
    {
        var chains = new List<object>();
        foreach (var waiting in states.Where(s => s.Area == Area.Packaging && s.State == OperatingState.WaitingForUpstream))
        {
            var diameter = Diameter(waiting.ArticleLabel);
            foreach (var oven in states.Where(s => s.Area == Area.Curing && s.Light != TrafficLight.Running && s.Occupancy >= TrafficLightThresholds.Default.OvenWarningAt))
            {
                foreach (var press in states.Where(s => s.Area == Area.Pressing && s.Light != TrafficLight.Running && Diameter(s.ArticleLabel) == diameter && diameter is not null))
                {
                    chains.Add(new
                    {
                        Kette = new[] { press.StationId, oven.StationId, waiting.StationId },
                        Text = $"{press.StationId} → {oven.StationId} → {waiting.StationId}: {press.StationId} liefert Ø {diameter}, "
                            + $"{oven.StationId} ist voll belegt ({Fmt.Percent(oven.Occupancy ?? 0)} %), {waiting.StationId} wartet auf die Ofencharge. "
                            + $"Eigenständig ist nur das Problem an der Presse, {waiting.StationId} hat kein eigenes Problem.",
                    });
                }
            }
        }

        return chains;
    }

    /// <summary>Durchmesser aus einer Artikelbezeichnung wie „Ø 180 · A30“.</summary>
    public static string? Diameter(string articleLabel)
    {
        var match = DiameterPattern().Match(articleLabel);
        return match.Success ? match.Groups["d"].Value : null;
    }

    /// <summary>Zahl für Fließtext mit Dezimalkomma (in den JSON-Feldern stehen Zahlen mit Punkt).</summary>
    public static string Num(double value, int decimals = 1) =>
        value.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), CultureInfo.InvariantCulture).Replace('.', ',');

    [GeneratedRegex(@"Ø\s*(?<d>\d{2,3})")]
    private static partial Regex DiameterPattern();
}
