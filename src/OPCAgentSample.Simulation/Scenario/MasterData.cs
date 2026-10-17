namespace OPCAgentSample.Simulation.Scenario;

/// <summary>
/// Trennscheiben-Artikel eines Rundtischs. <see cref="TargetCycleSeconds"/> ist der Soll-Takt (Grundlage der
/// OEE-Leistung), Füllgewicht mit Soll und Toleranz je Scheibe, Stückkosten für die Ausschussbewertung.
/// </summary>
public sealed record Article(
    string Id,
    string Description,
    int DiameterMm,
    string Grit,
    double ThicknessMm,
    double TargetCycleSeconds,
    double FillWeightTargetGrams,
    double FillWeightToleranceGrams,
    double PressForceKn,
    decimal ScrapCostEuro);

/// <summary>Verpackungsartikel (Kartontyp) einer Verpackstation; Soll-Zeit je Karton und Kosten je Fehlkarton.</summary>
public sealed record PackagingArticle(
    string Id,
    string Description,
    int DiameterMm,
    int PiecesPerCarton,
    double TargetCycleSeconds,
    decimal ScrapCostEuro);

/// <summary>Mischungscharge aus dem Mischraum (Rezept nach Körnung) mit Schüttdichte.</summary>
public sealed record MixtureBatch(string Id, string Recipe, double BulkDensityKgPerLitre, DateTimeOffset MixedUtc, string Note);

/// <summary>
/// Fertigungsauftrag. <see cref="NeedUtc"/> ist der früheste Bedarfstermin (Produktion davor = Überproduktion
/// „vor Bedarf“); <see cref="ProducedAtDemo"/> ist nur für Aufträge anderer Hallen gesetzt (dort keine Rohdaten).
/// </summary>
public sealed record ProductionOrder(
    string Id,
    string HallId,
    string? StationId,
    string ArticleId,
    int Quantity,
    DateTimeOffset PlannedStartUtc,
    DateTimeOffset PlannedEndUtc,
    DateTimeOffset NeedUtc,
    DateTimeOffset DueUtc,
    int? ProducedAtDemo = null);

/// <summary>
/// Pressform mit Standzeitgrenze in Hüben, Einbauzeitpunkt und Prognose der Hübe je Tag. Ein Rundtisch trägt
/// <see cref="Nests"/> Formnester; jede Form macht daher einen Hub je <see cref="Nests"/> Takte des Tisches.
/// </summary>
public sealed record Mould(
    string Id, string StationId, int DiameterMm, int Nests, int StrokeLimit, DateTimeOffset MountedUtc, int ForecastStrokesPerDay);

/// <summary>
/// Produktionsstation (Rundtisch oder Verpackung) mit Planmengen je Schicht, Kostensatz und Modellparametern.
/// <see cref="BaseSlowdown"/> verlängert den gemessenen Takt gegenüber dem Soll; <see cref="DefaultUtilization"/> ist
/// der Nutzungsgrad (Anteil der Laufzeit ohne Mikrostopps) für Schichten ohne Drehbuch-Vorgabe.
/// </summary>
public sealed record ProductionStationSpec(
    string StationId,
    bool IsPress,
    int PlanEarly,
    int PlanLate,
    int PlanNight,
    decimal DowntimeEuroPerMinute,
    double BaseSlowdown,
    double DefaultUtilization);

/// <summary>Ofen mit Chargenplätzen, Profil (Aufheizen/Halten/Abkühlen) und Zuordnungsregel.</summary>
public sealed record OvenSpec(
    string StationId,
    int Places,
    int PlatesPerPlace,
    TimeSpan HeatUp,
    TimeSpan Hold,
    TimeSpan CoolDown,
    TimeSpan LoadInterval,
    DateTimeOffset LoadAnchorUtc,
    int DiameterMm,
    string AssignmentRule,
    double HoldTemperatureCelsius,
    decimal DowntimeEuroPerMinute)
{
    public TimeSpan CureTime => HeatUp + Hold + CoolDown;
}

/// <summary>Verbrauchsmaterial einer Station mit Kapazität, Verbrauch je Stück und periodischem Nachfüllen.</summary>
public sealed record ConsumableSpec(
    string StationId, string Variable, double Capacity, double PerPiece, TimeSpan RefillInterval, TimeSpan RefillOffset);

/// <summary>Sollbereich des Hallenklimas für Mischungen (Stammdatum).</summary>
public sealed record ClimateRange(double MinCelsius, double MaxCelsius, double MinHumidity, double MaxHumidity)
{
    public bool Contains(double celsius, double humidity) =>
        celsius >= MinCelsius && celsius <= MaxCelsius && humidity >= MinHumidity && humidity <= MaxHumidity;
}

/// <summary>
/// Zielwerte der Tageskennzahlen-Historie (Historie B) je Station über die letzten 90 Tage: MTBF, MTTR und Anteil der
/// Störungen an der Gewebezuführung. Die synthetischen Tage vor dem Rohdatenfenster werden darauf kalibriert.
/// </summary>
public sealed record ReliabilityTarget(string StationId, double MtbfHours, double MttrMinutes, double FabricFeedShare);

/// <summary>Anteil ungeplanter Stillstände (Störung / (Störung + geplante Wartung)) je Monat, Halle A.</summary>
public sealed record UnplannedShareTarget(int Month, double Share);
