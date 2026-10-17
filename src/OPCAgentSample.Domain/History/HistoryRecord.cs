namespace OPCAgentSample.Domain.History;

/// <summary>
/// Basis aller Historien-Datensätze (Vertrag zwischen Simulator, Collector und Verdichtung).
/// <see cref="TimestampUtc"/> ist der Zeitpunkt, zu dem der Datensatz feststeht; ein Datenstrom ist danach sortiert.
/// Minutenwerte tragen den Beginn ihrer Minute.
/// </summary>
public abstract record HistoryRecord(string StationId, DateTimeOffset TimestampUtc);

/// <summary>Ausschussgrund am Rundtisch (OPC-Knoten Ausschussgründe).</summary>
public enum ScrapReason
{
    Weight,
    Thickness,
    Fabric,
    Label,
    Breakage,
}

/// <summary>
/// Ein Takt eines Rundtischs mit seinen Prozesswerten. <see cref="TargetCycleSeconds"/> ist der Soll-Takt des Artikels
/// (Grundlage der OEE-Leistung); <see cref="Scrap"/> ist <c>null</c> für ein Gutteil.
/// </summary>
public sealed record CycleRecord(
    string StationId,
    DateTimeOffset TimestampUtc,
    string? OrderId,
    string ArticleId,
    string MixtureBatchId,
    double CycleTimeSeconds,
    double TargetCycleSeconds,
    double PressForceKn,
    double PressForceBuildUpSeconds,
    double FillWeightGrams,
    double ThicknessMm,
    ScrapReason? Scrap) : HistoryRecord(StationId, TimestampUtc)
{
    public bool IsGood => Scrap is null;
}

/// <summary>
/// Stückzahl einer Minute an einer Station ohne Einzeltakt-Historie (Verpackung: Kartons).
/// <see cref="TargetCycleSeconds"/> ist die Soll-Zeit je Einheit.
/// </summary>
public sealed record UnitCountRecord(
    string StationId,
    DateTimeOffset TimestampUtc,
    string ArticleId,
    int Good,
    int Scrap,
    double TargetCycleSeconds) : HistoryRecord(StationId, TimestampUtc);

/// <summary>Minutenwert einer Variablen (Zähler, Ofen, Puffer, Verpackung, Klima); Namen in <see cref="MinuteVariables"/>.</summary>
public sealed record MinuteValue(string StationId, DateTimeOffset TimestampUtc, string Variable, double Value)
    : HistoryRecord(StationId, TimestampUtc);

/// <summary>Wechsel des Betriebszustands einer Station.</summary>
public sealed record StateChange(string StationId, DateTimeOffset TimestampUtc, OperatingState State, string? OrderId)
    : HistoryRecord(StationId, TimestampUtc);

/// <summary>
/// Planmenge einer Station für eine Schicht (Soll). Der Datensatz steht zu Schichtbeginn fest; das Soll bis jetzt
/// ergibt sich zeitanteilig. Eine Station mit Planmenge ist in dieser Schicht eingeplant (Schichtzeit).
/// </summary>
public sealed record ShiftPlanRecord(
    string StationId,
    DateTimeOffset TimestampUtc,
    string ShiftKey,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int PlannedQuantity) : HistoryRecord(StationId, TimestampUtc);
