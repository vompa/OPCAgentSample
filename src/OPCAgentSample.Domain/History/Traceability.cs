namespace OPCAgentSample.Domain.History;

/// <summary>
/// Pressenlos: gefertigte Scheiben eines Rundtischs aus einer Mischungscharge innerhalb einer Schicht
/// (erste Stufe der Rückverfolgung Mischcharge → Rundtisch/Schicht → Ofencharge → Karton/Palette).
/// </summary>
public sealed record PressLot(
    string StationId,
    DateTimeOffset TimestampUtc,
    string LotId,
    string MixtureBatchId,
    string ArticleId,
    string ShiftKey,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int GoodPieces) : HistoryRecord(StationId, TimestampUtc);

/// <summary>Ofencharge mit den enthaltenen Pressenlosen und dem Ergebnis der Rotations- und Bruchprüfung.</summary>
public sealed record OvenCharge(
    string StationId,
    DateTimeOffset TimestampUtc,
    string ChargeId,
    int Place,
    int DiameterMm,
    int Plates,
    DateTimeOffset LoadedUtc,
    DateTimeOffset UnloadedUtc,
    IReadOnlyList<string> PressLotIds,
    bool RotationTestPassed,
    bool BreakTestPassed) : HistoryRecord(StationId, TimestampUtc);

/// <summary>Palette einer Verpackstation mit Kartons aus einer oder mehreren Ofenchargen.</summary>
public sealed record PalletRecord(
    string StationId,
    DateTimeOffset TimestampUtc,
    string PalletId,
    string ArticleId,
    int Cartons,
    int PiecesPerCarton,
    IReadOnlyList<string> OvenChargeIds,
    DateTimeOffset? ShippedUtc) : HistoryRecord(StationId, TimestampUtc);
