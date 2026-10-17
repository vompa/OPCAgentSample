using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulator;

/// <summary>Konfiguration des Simulators (Abschnitt "Simulator" in appsettings.json, Kommandozeile siehe README).</summary>
public sealed class SimulatorOptions
{
    public const string SectionName = "Simulator";

    /// <summary>Namespace-URI des Anlagenmodells. Er muss im Server den Index 2 haben.</summary>
    public const string NamespaceUri = "urn:vompa:opc-agent-sample:plant";

    public int Port { get; set; } = 4840;

    /// <summary>Simulationsgeschwindigkeit: 1 = Echtzeit, 60 = eine Minute Simulation je Sekunde, 0 = Uhr angehalten.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>
    /// Startzeit der Simulationsuhr als Text. Ohne Zeitzone gilt die Ortszeit Wien, sonst Offset bzw. "Z" für UTC.
    /// Leer = Demo-Uhr (Do 15.10.2026, 09:42 Ortszeit).
    /// </summary>
    public string? Start { get; set; }

    /// <summary>Startzeit als UTC-Zeitpunkt (für Tests); hat Vorrang vor <see cref="Start"/>.</summary>
    public DateTimeOffset? StartUtc { get; set; }

    /// <summary>Aktualisierungstakt der Live-Werte in Millisekunden (Realzeit).</summary>
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>Obergrenze der Werte je Knoten und HistoryRead-Antwort; darüber folgt ein ContinuationPoint.</summary>
    public int MaxValuesPerRead { get; set; } = 5000;

    /// <summary>Seed des Historien-Generators (Standard: Seed der Demo).</summary>
    public int Seed { get; set; } = HallAScenario.DefaultSeed;

    /// <summary>Zusätzlich zu SecurityPolicy None auch Basic256Sha256 (SignAndEncrypt) anbieten.</summary>
    public bool EnableSecurity { get; set; } = true;

    /// <summary>Ordner für Zertifikate (selbstsigniertes Anwendungszertifikat, Trust-Listen).</summary>
    public string PkiPath { get; set; } = "pki/simulator";

    /// <summary>
    /// Adresse, an die der Server bindet. Standard Loopback (nur dieser Rechner, keine Firewall-Abfrage); für Container
    /// "0.0.0.0" setzen. Mit dem Hostnamen "localhost" bindet das OPC-SDK an alle Netzwerkkarten.
    /// </summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public string EndpointUrl => $"opc.tcp://{BindAddress}:{Port}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BindAddress))
            throw new ArgumentException("BindAddress darf nicht leer sein.", nameof(BindAddress));
        if (Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port muss zwischen 1 und 65535 liegen.");
        if (!double.IsFinite(Speed) || Speed < 0)
            throw new ArgumentOutOfRangeException(nameof(Speed), Speed, "Die Geschwindigkeit muss endlich und >= 0 sein.");
        if (UpdateIntervalMs < 10)
            throw new ArgumentOutOfRangeException(nameof(UpdateIntervalMs), UpdateIntervalMs, "Takt muss mindestens 10 ms betragen.");
        if (MaxValuesPerRead < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxValuesPerRead), MaxValuesPerRead, "Mindestens ein Wert je Antwort.");
        if (string.IsNullOrWhiteSpace(PkiPath))
            throw new ArgumentException("PkiPath darf nicht leer sein.", nameof(PkiPath));
    }

    /// <summary>Startzeit der Simulationsuhr als UTC; nie vor Beginn der Historie.</summary>
    public DateTimeOffset ResolveStart(ShiftCalendar calendar, DateTimeOffset historyStartUtc)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var start = StartUtc ?? ParseStart(calendar) ?? SimulationClock.DemoStart;
        return start < historyStartUtc
            ? throw new InvalidOperationException($"Die Startzeit {start:u} liegt vor dem Beginn der Historie (Mo 05.10.2026 00:00 Ortszeit).")
            : start;
    }

    private DateTimeOffset? ParseStart(ShiftCalendar calendar)
    {
        if (string.IsNullOrWhiteSpace(Start)) return null;
        if (!DateTime.TryParse(Start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            throw new FormatException($"Startzeit '{Start}' ist ungültig (Beispiel: 2026-10-15T09:42).");

        // RoundtripKind: "Z" ergibt Utc, ein Offset Local (bereits umgerechnet), ohne Angabe Unspecified (= Ortszeit Wien).
        return parsed.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(parsed),
            DateTimeKind.Local => new DateTimeOffset(parsed.ToUniversalTime()),
            _ => calendar.ToUtc(parsed),
        };
    }
}
