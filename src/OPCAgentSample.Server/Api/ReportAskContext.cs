using System.Globalization;
using System.Text;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Mcp;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// Verweis auf die Auffälligkeit eines Reports, aus der eine Frage stammt („Mit Claude analysieren“). Er enthält nur Kennungen;
/// Zeitraum und Werte liest der Server selbst aus dem Report, nie aus der Anfrage.
/// </summary>
public sealed record ReportRef(string? Kind, string? Range, string? FindingId)
{
    public const string MachinesKind = "machines";
    public const int MaxFindingIdLength = 80;

    /// <summary>Fehlertext für die Antwort 400 oder <see langword="null"/>, wenn der Verweis gültig ist.</summary>
    public string? Validate()
    {
        if (Kind != MachinesKind) return $"Der Report '{Kind}' ist unbekannt. Gültig: {MachinesKind}.";
        if (Range is null || !MachineRanges.All.Contains(Range, StringComparer.Ordinal))
            return $"Der Zeitraum '{Range}' ist unbekannt. Gültig: {string.Join(", ", MachineRanges.All)}.";
        if (string.IsNullOrWhiteSpace(FindingId) || FindingId.Length > MaxFindingIdLength) return "Die Auffälligkeit fehlt oder ist zu lang.";
        return null;
    }
}

/// <summary>
/// Löst den <see cref="ReportRef"/> einer Frage in den Kontext für Claude auf. Der Report kommt aus demselben Zwischenspeicher wie
/// die Report-Seite, damit Frage und Anzeige dieselben Zahlen haben.
/// </summary>
public sealed class ReportAskContextResolver(MachineReportCache cache, MachineReportService service)
{
    public async Task<ReportContext> ResolveAsync(ReportRef reference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var report = await cache.GetAsync(reference.Range!, compare: true, service, cancellationToken);
        return new ReportContext(
            $"{reference.Kind}|{reference.Range}|{reference.FindingId}", MachineAskContext.Describe(report, reference.FindingId!));
    }
}

/// <summary>
/// Beschreibt für Claude, was ein Maschinen-Report an einer Auffälligkeit zeigt: Zeitraum mit Von/Bis und der passenden Angabe für die
/// Tools, Kennzahlen der Station, Verlust- und Ausschussgründe, Toleranzbänder, Formverschleiß, Wartung und Schichtvergleich.
/// Alle Werte hat der Server berechnet; der Text ist ein Datenblock, keine Anweisung.
/// </summary>
public static class MachineAskContext
{
    private const int MaxParetoRows = 4;
    private const int MaxScrapRows = 3;

    private static readonly CultureInfo s_de = CultureInfo.GetCultureInfo("de-AT");

    public static string Describe(MachineReportResponse report, string findingId)
    {
        ArgumentNullException.ThrowIfNull(report);
        var text = new StringBuilder();
        AppendRange(text, report);

        var finding = report.Findings.FirstOrDefault(f => f.Id == findingId);
        if (finding is null)
        {
            text.AppendLine("Die Auffälligkeit ist im aktuellen Report nicht mehr enthalten; es gilt nur der Zeitraum.");
            return text.ToString().TrimEnd();
        }

        text.AppendLine(CultureInfo.InvariantCulture, $"Auffälligkeit im Report: {finding.Title}. {finding.Text}");
        if (report.Stations.FirstOrDefault(s => s.Id == finding.Station) is { } station) AppendStation(text, station, finding.Id);
        else AppendShiftComparison(text, report.ShiftComparison);
        return text.ToString().TrimEnd();
    }

    private static void AppendRange(StringBuilder text, MachineReportResponse report)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"Quelle: Maschinen-Report der Halle A, Zeitraum {report.RangeLabel}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Von {Fmt.Stamp(report.StartUtc)} bis {Fmt.Stamp(report.EndUtc)} (Ortszeit; ein noch laufender Zeitraum reicht nur bis zum Datenstand), Datenstand {report.DataAsOf}.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Zeitraum-Angabe für die Tools: \"{ToolRangeOf(report)}\" (deckt genau diesen Zeitraum ab; Angaben wie \"4 Wochen\" oder \"28 Tage\" weichen ab).");
    }

    /// <summary>
    /// Angabe für den Parameter <c>zeitraum</c> der MCP-Tools, die genau den Zeitraum des Reports abdeckt. Die vier Wochen sind
    /// ganze Kalenderwochen (<c>KW 39-42</c>), kein gleitendes Fenster wie „28 Tage“.
    /// </summary>
    internal static string ToolRangeOf(MachineReportResponse report) => report.Range switch
    {
        MachineRanges.Shift => "aktuelle Schicht",
        MachineRanges.Yesterday => "gestern",
        MachineRanges.Week => "diese Woche",
        _ => $"KW {WeekOf(report.StartUtc)}-{WeekOf(report.EndUtc.AddDays(-1))}",
    };

    private static int WeekOf(DateTimeOffset utc) => ISOWeek.GetWeekOfYear(DemoTime.ToLocal(utc).DateTime);

    private static void AppendStation(StringBuilder text, MachineStation station, string findingId)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"Werte von {station.Id} ({station.Name}) im Report:");
        if (station.Oee is { } oee)
        {
            var previous = station.Previous?.OeePercent is { } p ? $", Vorperiode {p} %" : string.Empty;
            text.AppendLine(CultureInfo.InvariantCulture, $"- OEE {oee.OeePercent} % ({RatingText.Word(oee.Rating)}): Verfügbarkeit {oee.AvailabilityPercent} %, Leistung {oee.PerformancePercent} %, Qualität {oee.QualityPercent} %{previous}.");
        }

        if (station.OccupancyPercent is { } occupancy) text.AppendLine(CultureInfo.InvariantCulture, $"- Belegung {occupancy} %.");
        if (station.PlanPercent is { } plan) text.AppendLine(CultureInfo.InvariantCulture, $"- Plan erfüllt zu {plan} % ({Fmt.Thousands(station.Actual)} von {Fmt.Thousands(station.Planned)} {station.Unit}).");
        if (station.ScrapPercent is { } scrap) text.AppendLine(CultureInfo.InvariantCulture, $"- Ausschuss {Number(scrap, 1)} % ({Fmt.Thousands(station.ScrapEuro)} €).");
        if (station.DowntimeMinutes > 0)
            text.AppendLine(CultureInfo.InvariantCulture, $"- Stillstand {Fmt.Duration(TimeSpan.FromMinutes(station.DowntimeMinutes))} = {Number(station.DowntimeSharePercent, 1)} % der Zeit, {Fmt.Thousands(station.DowntimeEuro)} €.");

        AppendPareto(text, station);
        AppendScrapReasons(text, station);
        AppendTrends(text, station, findingId);
        AppendMaintenance(text, station);
        AppendShiftOee(text, station);
    }

    private static void AppendPareto(StringBuilder text, MachineStation station)
    {
        if (station.Pareto.Count == 0) return;

        var rows = station.Pareto.Take(MaxParetoRows).Select(p =>
            $"{p.Reason} {Fmt.Duration(TimeSpan.FromMinutes(p.Minutes))} in {p.Count} Ereignissen, {Fmt.Thousands(p.Euro)} €");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Größte Stillstandsgründe: {string.Join("; ", rows)}.");
    }

    private static void AppendScrapReasons(StringBuilder text, MachineStation station)
    {
        if (station.ScrapByReason.Count == 0) return;

        var rows = station.ScrapByReason.Take(MaxScrapRows).Select(r => $"{r.Reason} {Number(r.Percent, 0)} % ({Fmt.Thousands(r.Count)} Takte)");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Ausschussgründe: {string.Join("; ", rows)}.");
    }

    /// <summary>Prozesswerte mit Toleranzband: die Werte außerhalb der Ampel und der Wert, um den es in der Auffälligkeit geht.</summary>
    private static void AppendTrends(StringBuilder text, MachineStation station, string findingId)
    {
        foreach (var trend in station.Trends.Where(t => t.Rating != Rating.Ok || findingId.EndsWith($"-{t.Key}", StringComparison.Ordinal)))
        {
            var band = trend.Low is { } low && trend.High is { } high ? $"Toleranzband {Number(low, trend.Decimals)} bis {Number(high, trend.Decimals)} {trend.Unit}" : "ohne Toleranzband";
            var target = trend.Target is { } t ? $", Soll {Number(t, trend.Decimals)} {trend.Unit}" : string.Empty;
            var outside = trend.OutsidePercent is { } o ? $", {Number(o, 1)} % der Werte außerhalb (Basis: {trend.Basis})" : string.Empty;
            text.AppendLine(CultureInfo.InvariantCulture, $"- {trend.Label}: {band}{target}{outside} ({RatingText.Word(trend.Rating)}).");
        }
    }

    private static void AppendMaintenance(StringBuilder text, MachineStation station)
    {
        if (station.Maintenance.Mould is { } mould)
        {
            var limit = mould.LimitDay is null ? string.Empty : $", Grenze voraussichtlich {mould.LimitDay}";
            text.AppendLine(CultureInfo.InvariantCulture, $"- Form {mould.MouldId}: {mould.WearPercent} % der Standzeit, {Fmt.Thousands(mould.Strokes)} von {Fmt.Thousands(mould.StrokeLimit)} Hüben, noch {Fmt.Thousands(mould.RemainingStrokes)}{limit}.");
        }

        foreach (var item in station.Maintenance.Items.Where(i => i.Overdue))
            text.AppendLine(CultureInfo.InvariantCulture, $"- Wartung überfällig: {item.Title} ({item.DueText}, {item.StopMinutes} min Stillstand).");
    }

    private static void AppendShiftOee(StringBuilder text, MachineStation station)
    {
        var rows = station.ShiftOee.Where(s => s.OeePercent is not null).Select(s => $"{s.Shift} {s.OeePercent} %").ToList();
        if (rows.Count > 0) text.AppendLine(CultureInfo.InvariantCulture, $"- OEE nach Schicht: {string.Join(", ", rows)}.");
    }

    private static void AppendShiftComparison(StringBuilder text, MachineShiftComparison comparison)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"Schichtvergleich der Halle A im Report ({comparison.Basis}):");
        foreach (var shift in comparison.Shifts)
        {
            var oee = shift.Oee is { } o ? $"OEE {o.OeePercent} % (Verfügbarkeit {o.AvailabilityPercent} %, Leistung {o.PerformancePercent} %, Qualität {o.QualityPercent} %)" : "keine OEE";
            var mttr = shift.MttrMinutes is { } m ? $", MTTR {Number(m, 1)} min" : string.Empty;
            var reaction = shift.ReactionMinutes is { } r ? $", Reaktionszeit Bereitschaft {Number(r, 1)} min" : string.Empty;
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"- {shift.Shift}schicht: {oee}; Stillstand {Fmt.Duration(TimeSpan.FromMinutes(shift.DowntimeMinutes))}; "
                + $"Anlaufverlust nach Wechseln {Fmt.Thousands(shift.StartupLossPieces)} Stück in {shift.StartupLossCount} Fällen ({Number(shift.StartupLossMinutes, 0)} min); {shift.Failures} Störungen{mttr}{reaction}.");
        }
    }

    private static string Number(double value, int decimals) => value.ToString($"N{decimals}", s_de);
}
