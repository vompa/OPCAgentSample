namespace OPCAgentSample.Server.Api;

/// <summary>Zeiträume des Maschinen-Reports (Parameter <c>range</c>).</summary>
public static class MachineRanges
{
    public const string Shift = "shift";
    public const string Yesterday = "yesterday";
    public const string Week = "week";
    public const string FourWeeks = "fourWeeks";

    public static IReadOnlyList<string> All { get; } = [Shift, Yesterday, Week, FourWeeks];

    /// <summary>Startzeitraum je Rolle: Schichtleiter die laufende Schicht, Hallenleiterin die Woche, Produktionsleiter vier Wochen.</summary>
    public static string DefaultOf(string roleId) => roleId switch
    {
        "hall_lead" => Week,
        "production_lead" => FourWeeks,
        _ => Shift,
    };
}

/// <summary>OEE einer Station oder der Halle in ganzen Prozent, zerlegt in die drei Faktoren.</summary>
public sealed record MachineOee(int OeePercent, int AvailabilityPercent, int PerformancePercent, int QualityPercent, string Rating);

/// <summary>Werte der Vorperiode zum Vergleich; Quoten, weil die laufende Periode noch nicht abgeschlossen sein kann.</summary>
public sealed record MachinePrevious(int? OeePercent, double? ScrapPercent, double? DowntimeSharePercent, int? OccupancyPercent);

/// <summary>Ein Grund im Stillstands-Pareto einer Station.</summary>
public sealed record MachinePareto(string Reason, double Minutes, int Count, int Euro);

/// <summary>Ausschuss nach Grund (Anzahl Takte mit Ausschuss).</summary>
public sealed record MachineScrapReason(string Reason, long Count, double Percent);

/// <summary>
/// Verlauf eines Prozesswerts mit Toleranzband. <see cref="OutsidePercent"/> ist der Anteil der Werte außerhalb von
/// [<see cref="Low"/>, <see cref="High"/>]; <see cref="Basis"/> nennt, worauf er sich bezieht (Takte, Minuten, Stunden).
/// </summary>
public sealed record MachineTrend(
    string Key,
    string Label,
    string Unit,
    int Decimals,
    double? Target,
    double? Low,
    double? High,
    double? OutsidePercent,
    string Basis,
    string Rating,
    IReadOnlyList<string> Labels,
    IReadOnlyList<double> Values);

/// <summary>MTTR und Reaktionszeit der Instandhaltung je Schichtart.</summary>
public sealed record MachineShiftReliability(string Shift, double? MttrMinutes, int Failures, double? ReactionMinutes, int ReactionCount);

public sealed record MachineReliability(
    double? MtbfHours, double? MttrMinutes, int Failures, int UnplannedSharePercent, IReadOnlyList<MachineShiftReliability> ByShift);

/// <summary>Formstandzeit einer Rundtischpresse: Verschleiß in Prozent, Hübe, Grenze und der Produktionstag der Grenze.</summary>
public sealed record MachineMould(string MouldId, int WearPercent, int Strokes, int StrokeLimit, int RemainingStrokes, string? LimitDay, string Rating);

/// <summary>Fällige oder überfällige Wartung einer Station.</summary>
public sealed record MachineMaintenanceItem(string Id, string Title, string DueText, bool Overdue, int StopMinutes);

public sealed record MachineMaintenance(MachineMould? Mould, IReadOnlyList<MachineMaintenanceItem> Items);

/// <summary>Eine Station im Raster und in ihrem Detailbereich.</summary>
public sealed record MachineStation(
    string Id,
    string Name,
    string Type,
    string Rating,
    MachineOee? Oee,
    int? OccupancyPercent,
    string? Unit,
    long Actual,
    long Planned,
    int? PlanPercent,
    double? ScrapPercent,
    double DowntimeMinutes,
    double DowntimeSharePercent,
    int DowntimeEuro,
    int ScrapEuro,
    MachinePrevious? Previous,
    IReadOnlyList<MachinePareto> Pareto,
    IReadOnlyList<MachineScrapReason> ScrapByReason,
    IReadOnlyList<MachineTrend> Trends,
    MachineReliability Reliability,
    MachineMaintenance Maintenance,
    IReadOnlyList<MachineShiftOee> ShiftOee);

/// <summary>OEE einer Station in einer Schichtart des Zeitraums.</summary>
public sealed record MachineShiftOee(string Shift, int? OeePercent);

/// <summary>Eine Schichtart im Schichtvergleich der Halle: OEE und Faktoren, Stillstand, Anlaufverluste nach dem Schichtwechsel.</summary>
public sealed record MachineShift(
    string Shift,
    MachineOee? Oee,
    double DowntimeMinutes,
    int StartupLossPieces,
    int StartupLossCount,
    double StartupLossMinutes,
    double? MttrMinutes,
    double? ReactionMinutes,
    int Failures);

public sealed record MachineShiftComparison(string Basis, IReadOnlyList<MachineShift> Shifts);

public sealed record MachineCostItem(string Station, string Reason, double Minutes, int Euro);

public sealed record MachineCosts(
    int DowntimeEuro,
    int ScrapEuro,
    int TotalEuro,
    int? PreviousTotalEuro,
    int EuroPerPlanHour,
    int? PreviousEuroPerPlanHour,
    IReadOnlyList<MachineCostItem> TopItems);

/// <summary>Kennzahlen der Halle A (Pressen und Verpackung) im Zeitraum und in der Vorperiode.</summary>
public sealed record MachineSummary(
    MachineOee? Oee, int? PlanPercent, double? ScrapPercent, double DowntimeMinutes, MachinePrevious? Previous);

/// <summary>Auffälligkeit, an der „Mit Claude analysieren“ eine passende Frage stellt.</summary>
public sealed record MachineFinding(string Id, string? Station, string Rating, string Title, string Text, string Question);

/// <summary>Antwort von <c>GET /api/reports/machines</c>.</summary>
public sealed record MachineReportResponse(
    string Role,
    string Range,
    string DefaultRange,
    bool Compare,
    string RangeLabel,
    string? PreviousLabel,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset GeneratedAtUtc,
    string DataAsOf,
    string Source,
    IReadOnlyList<string> Notes,
    MachineSummary Summary,
    IReadOnlyList<MachineStation> Stations,
    MachineShiftComparison ShiftComparison,
    MachineCosts Costs,
    IReadOnlyList<MachineFinding> Findings);
