using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Simulation.Script;

/// <summary>Festes Verlust- oder Stillstandsereignis des Drehbuchs (Spezifikation Abschnitt 6).</summary>
public sealed record ScriptedLoss(
    string Id,
    string StationId,
    LossCategory Category,
    string Reason,
    string Assembly,
    string Cause,
    DateTimeOffset StartUtc,
    TimeSpan Duration,
    int AffectedPieces = 0,
    string? Note = null,
    bool Acknowledged = true)
{
    public DateTimeOffset EndUtc => StartUtc + Duration;
}

/// <summary>
/// Rüstvorgang: der Soll-Anteil ist ein geplanter Stopp, der Überhang wird als „Rüsten über Soll“ (ungeplant) erfasst.
/// </summary>
public sealed record ScriptedSetup(string Id, string StationId, DateTimeOffset StartUtc, TimeSpan Planned, TimeSpan Overrun);

/// <summary>Welcher Auftrag (oder kein Auftrag) mit welchem Artikel ab wann an einer Station läuft.</summary>
public sealed record OrderAssignment(string StationId, DateTimeOffset StartUtc, string? OrderId, string ArticleId);

/// <summary>Welche Mischungscharge ab wann an einem Rundtisch verarbeitet wird.</summary>
public sealed record BatchAssignment(string StationId, DateTimeOffset StartUtc, string BatchId);

/// <summary>
/// Kalibrierte Stückzahl einer Station in einem Fenster (der Nutzungsgrad wird daraus bestimmt). Mit
/// <see cref="ContinueAfter"/> läuft die Station bis Schichtende mit diesem Nutzungsgrad weiter (Live-Fortsetzung).
/// </summary>
public sealed record OutputTarget(string StationId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Pieces, bool ContinueAfter = false);

/// <summary>Ausschussquote einer Station in einem Fenster mit Grund (überschreibt den Grundausschuss).</summary>
public sealed record ScrapWindow(string StationId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Rate, ScrapReason Reason);

/// <summary>Verschiebung des Füllgewichts ab einem Zeitpunkt, linear über <see cref="Ramp"/> eingeblendet.</summary>
public sealed record FillWeightShift(string StationId, DateTimeOffset StartUtc, TimeSpan Ramp, double OffsetGrams);

/// <summary>
/// Drift des Presskraftaufbaus: linear von <see cref="FromSeconds"/> bis <see cref="ToSeconds"/> über
/// <see cref="Ramp"/>, danach mit gleicher Steigung weiter bis <see cref="CapSeconds"/>. Der Takt verlängert sich um
/// <see cref="CycleSecondsPerBuildUpSecond"/> je Sekunde Mehraufbauzeit.
/// </summary>
public sealed record BuildUpDrift(
    string StationId,
    DateTimeOffset StartUtc,
    TimeSpan Ramp,
    double FromSeconds,
    double ToSeconds,
    double CapSeconds,
    double CycleSecondsPerBuildUpSecond)
{
    public double BuildUpAt(DateTimeOffset instant)
    {
        if (instant <= StartUtc) return FromSeconds;
        var slope = (ToSeconds - FromSeconds) / Ramp.TotalSeconds;
        return Math.Min(CapSeconds, FromSeconds + (slope * (instant - StartUtc).TotalSeconds));
    }
}

/// <summary>Mischungsgebinde, das an einen Rundtisch geliefert wird; verarbeitbar bis Lieferung + Topfzeit.</summary>
public sealed record MixtureDelivery(string StationId, DateTimeOffset DeliveredUtc, double Kilograms, TimeSpan PotLife)
{
    public DateTimeOffset ProcessableUntilUtc => DeliveredUtc + PotLife;
}

/// <summary>Alarm oder Meldung des Drehbuchs.</summary>
public sealed record ScriptedAlarm(
    string Id,
    string StationId,
    AlarmKind Kind,
    AlarmSeverity Severity,
    string Assembly,
    string Text,
    DateTimeOffset ActiveUtc,
    DateTimeOffset? ClearedUtc);

/// <summary>Stützpunkt eines Verlaufs (Puffer).</summary>
public sealed record ProfilePoint(DateTimeOffset AtUtc, double Value);

/// <summary>Verzögerung der laufenden Ofencharge (z. B. Temperaturabweichung).</summary>
public sealed record OvenDelay(string StationId, DateTimeOffset AtUtc, TimeSpan Delay);

/// <summary>Abweichende Planmenge einer Station in einer Schicht (Schlüssel z. B. 2026-10-14N).</summary>
public sealed record ShiftPlanOverride(string StationId, string ShiftKey, int PlannedQuantity);

/// <summary>Fenster ohne Grundrauschen-Kurzstopps (der Zustand ist dort vollständig durch das Drehbuch bestimmt).</summary>
public sealed record QuietWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
