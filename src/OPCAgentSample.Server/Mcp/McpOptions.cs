using System.ComponentModel.DataAnnotations;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Einstellungen der MCP-Schnittstelle (Abschnitt Mcp).</summary>
public sealed class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>Höchstzahl der Zeilen je Liste in einem Tool-Ergebnis; mehr wird gekürzt und im Ergebnis vermerkt.</summary>
    [Range(5, 500)]
    public int MaxRows { get; set; } = 40;

    /// <summary>Wartungsplanung: Maschinenstillstand und Rüstzeiten (Kalibrierung, siehe <c>docs/calibration.md</c>).</summary>
    public MaintenanceOptions Maintenance { get; } = new();

    /// <summary>Grenzwerte der Prozesswerte, die nicht in der Datenbank stehen.</summary>
    public ProcessLimitOptions ProcessLimits { get; } = new();

    /// <summary>Kostensatz je Stillstandsminute der verdichteten Hallen B und C (simuliert, wie die Stationssätze).</summary>
    public Dictionary<string, decimal> CounterHallDowntimeEuroPerMinute { get; } =
        new(StringComparer.Ordinal) { ["HALLE-B"] = 1.00m, ["HALLE-C"] = 0.835m };
}

public sealed class MaintenanceOptions
{
    /// <summary>
    /// Zusätzlicher Maschinenstillstand einer Wartung in Minuten, wenn sie einzeln erfolgt (Schlüssel: Wartungs-Id).
    /// Ohne Eintrag gilt die Arbeitsdauer der Wartung. Beispiel: Die Formwartung dauert 40 min, die Maschine steht dafür 25 min.
    /// </summary>
    public Dictionary<string, int> MachineStopMinutes { get; } = new(StringComparer.Ordinal);

    /// <summary>Dauer eines Rüstvorgangs (Auftragswechsel) in Minuten; Fenster, in dem Wartungen gebündelt werden können.</summary>
    [Range(5, 240)]
    public int SetupMinutes { get; set; } = 40;

    /// <summary>Dauer eines Mischungswechsels in Minuten.</summary>
    [Range(5, 240)]
    public int MixtureChangeMinutes { get; set; } = 20;
}

public sealed class ProcessLimitOptions
{
    /// <summary>Soll-Presskraftaufbau eines Rundtischs in Sekunden.</summary>
    public double BuildUpTargetSeconds { get; set; } = 0.8;

    /// <summary>Ab diesem Vielfachen des Solls gilt der Presskraftaufbau als Warnung.</summary>
    public double BuildUpWarningRatio { get; set; } = 1.25;

    /// <summary>Vorlauf im Mischraum in Minuten: So früh vor dem Ende der Verarbeitbarkeit muss neue Mischung angefordert werden.</summary>
    [Range(0, 240)]
    public int MixtureLeadMinutes { get; set; } = 30;
}
