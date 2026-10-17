using OPCAgentSample.Domain;

namespace OPCAgentSample.Server.MasterData;

/// <summary>Artikel (Trennscheibe) mit Durchmesser, Körnung und Dicke.</summary>
public sealed class Article
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public int DiameterMm { get; set; }
    public required string Grit { get; set; }
    public decimal ThicknessMm { get; set; }
    public required string ArticleGroup { get; set; }
    public int PiecesPerCarton { get; set; }

    /// <summary>Soll-Füllgewicht je Scheibe in Gramm (0 bei Kartons); Grundlage der Ampel „Füllgewicht“.</summary>
    public decimal FillWeightTargetGrams { get; set; }

    /// <summary>Toleranz des Füllgewichts in Gramm (0 bei Kartons).</summary>
    public decimal FillWeightToleranceGrams { get; set; }
}

/// <summary>Mischungsrezept je Körnung (z. B. A60).</summary>
public sealed class Mixture
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Grit { get; set; }
}

/// <summary>Gemischte Charge mit Verarbeitbarkeit (Standzeit der Mischung).</summary>
public sealed class MixtureBatch
{
    public required string Id { get; set; }
    public required string MixtureId { get; set; }
    public int BulkDensityGramsPerLiter { get; set; }
    public DateTimeOffset ProducedAtUtc { get; set; }
    public DateTimeOffset? ProcessableUntilUtc { get; set; }
    public string? Remark { get; set; }
}

/// <summary>Pressform einer Station mit Hubzähler und Standzeitgrenze.</summary>
public sealed class Mould
{
    public required string Id { get; set; }
    public required string StationId { get; set; }
    public int DiameterMm { get; set; }
    public int StrokesSinceChange { get; set; }
    public int StrokeLimit { get; set; }
    public int AverageStrokesPerDay { get; set; }

    /// <summary>Formnester je Rundtisch; ein Hub entspricht so vielen Takten des Tisches.</summary>
    public int Nests { get; set; }

    /// <summary>Einbauzeitpunkt; die Hübe seit dem Wechsel werden aus den Takten ab hier gezählt.</summary>
    public DateTimeOffset MountedAtUtc { get; set; }
}

public enum OrderState
{
    Planned,
    Running,
    Completed,
}

/// <summary>Fertigungsauftrag; Aufträge der Halle B haben keine Station.</summary>
public sealed class ProductionOrder
{
    public required string Id { get; set; }
    public required string ArticleId { get; set; }
    public required string HallId { get; set; }
    public string? StationId { get; set; }
    public int TargetQuantity { get; set; }
    public int ProducedQuantity { get; set; }
    public OrderState State { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public DateTimeOffset DueUtc { get; set; }
    public string? Remark { get; set; }
}

public enum CostRateKind
{
    DowntimeMinute,
    ScrapPiece,
}

/// <summary>Kostensatz in Euro: je Stillstandsminute einer Station oder je Ausschussstück eines Artikels.</summary>
public sealed class CostRate
{
    public required string Id { get; set; }
    public CostRateKind Kind { get; set; }
    public string? StationId { get; set; }
    public string? ArticleId { get; set; }
    public decimal Euro { get; set; }

    /// <summary>Falsch, solange der Wert nur ein Platzhalter ist und mit dem Szenario kalibriert wird.</summary>
    public bool IsCalibrated { get; set; }

    public string? Note { get; set; }
}

/// <summary>Gesetzlicher Feiertag im Datenhorizont.</summary>
public sealed class Holiday
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
}

/// <summary>Ein Produktionstag des Schichtkalenders; berücksichtigt Wochenende, Feiertage und Betriebsurlaub.</summary>
public sealed class CalendarDay
{
    public DateOnly ProductionDay { get; set; }
    public bool IsProductionDay { get; set; }
    public string? Remark { get; set; }
}

/// <summary>Rolle mit Sicht (Scope), Rechten und Meldungsfilter. Bestimmt, welche Daten der MCP-Server liefert.</summary>
public sealed class AppRole
{
    public required string Id { get; set; }
    public required string Title { get; set; }

    /// <summary>Halle, auf die die Sicht beschränkt ist; leer = ganzes Werk.</summary>
    public string? ScopeHallId { get; set; }

    public required string ScopeDescription { get; set; }
    public required string Devices { get; set; }
    public List<string> Rights { get; set; } = [];
    public required string MessageFilter { get; set; }
}

/// <summary>Fiktiver Demo-Benutzer. Keine personenbezogene Leistungsauswertung, keine Namen an Maschinendaten.</summary>
public sealed class AppUser
{
    public required string Id { get; set; }
    public required string DisplayName { get; set; }
    public required string RoleId { get; set; }
}

/// <summary>Favorit eines Benutzers (max. 10 je Benutzer, Kurztitel höchstens 22 Zeichen); gilt auf PC und Handy.</summary>
public sealed class Favorite
{
    public const int MaxPerUser = 10;
    public const int MaxShortTitleLength = 22;

    public required string ShortTitle { get; set; }
    public required string Question { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>Regel, die einen Fall im Postfach einer Rolle auslöst (deterministisch, keine KI).</summary>
public sealed class EscalationRule
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Subject { get; set; }
    public bool SafetyRelevantOnly { get; set; }
    public int ThresholdMinutes { get; set; }
    public required string TargetRoleId { get; set; }
}

public enum MaintenanceKind
{
    SafetyInspection,
    StrokeCounter,
    ConditionBased,
    MeasuringEquipment,
    Scheduled,
    TimeBased,
    Forecast,
    Inspection,
    Revision,
}

/// <summary>Geplanter oder prognostizierter Wartungstermin einer Station (Wartungs-DB).</summary>
public sealed class MaintenancePlan
{
    public required string Id { get; set; }
    public required string StationId { get; set; }
    public required string Title { get; set; }
    public MaintenanceKind Kind { get; set; }
    public DateTimeOffset DueUtc { get; set; }

    /// <summary>Ende eines Zeitfensters (exklusiv), z. B. beim Betriebsurlaub; sonst leer.</summary>
    public DateTimeOffset? WindowEndUtc { get; set; }

    public int? DurationMinutes { get; set; }
    public bool IsSafetyRelevant { get; set; }

    /// <summary>Prognose aus Hüben oder Betriebsstunden statt fester Termin.</summary>
    public bool IsForecast { get; set; }

    public string? WorkOrder { get; set; }
    public string? MouldId { get; set; }
    public string? Hint { get; set; }
}

/// <summary>Erledigte Wartung (Historie, bleibt dauerhaft).</summary>
public sealed class MaintenanceHistory
{
    public required string Id { get; set; }
    public required string StationId { get; set; }
    public required string Title { get; set; }
    public MaintenanceKind Kind { get; set; }
    public DateTimeOffset DoneAtUtc { get; set; }
    public string? Result { get; set; }
}
