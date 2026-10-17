using OPCAgentSample.Domain;

namespace OPCAgentSample.Server.Live;

/// <summary>Ein Befund der Stationsampel (Ampelfarbe und Text der Kennzahlen-Bibliothek).</summary>
public sealed record StationFinding(TrafficLight Light, string Text);

/// <summary>
/// Zustand einer Station zum Datenstand (Redis-Hash <c>st:{id}</c>): Betriebszustand, Auftrag, Prozesswerte, Ampel und
/// Materialreichweite. Entsteht aus den verdichteten Daten in Redis und dem laufenden Schichtblock; die Ampel rechnet die Kennzahlen-Bibliothek.
/// </summary>
public sealed record StationState(
    string StationId,
    Area Area,
    OperatingState State,
    DateTimeOffset SinceUtc,
    string? OrderId,
    string? ArticleId,
    string ArticleLabel,
    TrafficLight Light,
    IReadOnlyList<StationFinding> Findings,
    string KpiValue,
    string KpiUnit,
    string Message,
    long PiecesActual,
    double PiecesPlanned,
    double? PlanRatio,
    double? CycleSeconds,
    double? TargetCycleSeconds,
    double? BuildUpSeconds,
    double? FillWeightMeanGrams,
    double? FillWeightTargetGrams,
    string? MouldId,
    double? MouldWear,
    DateTimeOffset? MouldLimitUtc,
    double? ReachMinutes,
    string? ReachMaterial,
    double? Occupancy,
    int? OccupiedPlaces,
    int? Places,
    double? ZoneTemperature,
    int? Plates,
    double? MeanWaitMinutes,
    DateTimeOffset SourceUntilUtc);

/// <summary>Hallenklima (Redis-Hash <c>st:A-KLIMA</c>): Werte zum Datenstand und zu Schichtbeginn.</summary>
public sealed record ClimateState(
    double Celsius, double Humidity, double CelsiusAtShiftStart, bool InRange, DateTimeOffset SourceUntilUtc);

/// <summary>Alle Stationszustände der Halle mit Klima und Datenstand.</summary>
public sealed record StationSnapshot(IReadOnlyList<StationState> Stations, ClimateState? Climate, DateTimeOffset SourceUntilUtc)
{
    public static StationSnapshot Empty { get; } = new([], null, DateTimeOffset.MinValue);

    public StationState? Find(string stationId) => Stations.FirstOrDefault(s => s.StationId == stationId);
}
