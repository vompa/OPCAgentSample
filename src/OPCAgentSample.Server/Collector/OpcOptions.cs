using System.ComponentModel.DataAnnotations;

namespace OPCAgentSample.Server.Collector;

/// <summary>Verbindung des Collectors zum OPC-UA-Server der Halle (Abschnitt Opc).</summary>
public sealed class OpcOptions
{
    public const string SectionName = "Opc";

    /// <summary>Collector einschalten. Aus: keine Verbindung, keine Historienquelle, die Demo-Uhr bleibt die Referenzuhr.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Endpunkt des OPC-UA-Servers.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Endpoint { get; set; } = "opc.tcp://localhost:4840";

    /// <summary>Signieren und verschlüsseln (SecurityPolicy Basic256Sha256), sonst SecurityPolicy None.</summary>
    public bool UseSecurity { get; set; }

    /// <summary>
    /// Nicht vertrauenswürdige Serverzertifikate ohne Rückfrage annehmen. Nur für lokale Entwicklung und Tests; sonst muss
    /// das Zertifikat des Servers in <c>&lt;PkiPath&gt;/trusted</c> liegen (abgelehnte landen in <c>&lt;PkiPath&gt;/rejected</c>).
    /// </summary>
    public bool AutoAcceptUntrustedCertificates { get; set; }

    /// <summary>Ordner für das eigene Zertifikat und die Trust-Listen (relativ zum Arbeitsverzeichnis, nicht im Repository).</summary>
    [Required(AllowEmptyStrings = false)]
    public string PkiPath { get; set; } = "pki/collector";

    /// <summary>Anwendungsname des Collectors (Subject des Zertifikats).</summary>
    [Required(AllowEmptyStrings = false)]
    public string ApplicationName { get; set; } = "OPCAgentSample.Collector";

    /// <summary>Zeitlimit je OPC-Dienstaufruf (HistoryRead, Read).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Abstand der KeepAlive-Prüfung; ein Fehlschlag löst den Wiederaufbau der Sitzung aus.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:01:00")]
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Erste Wartezeit nach einem Verbindungsfehler; sie verdoppelt sich bis <see cref="ReconnectMaxDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    [Range(typeof(TimeSpan), "00:00:00.100", "01:00:00")]
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wie lange ein Zugriff (z. B. der erste Abruf der Historie) auf die erste Verbindung wartet.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan ConnectWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Stationen, die ein Fenster gleichzeitig per HistoryRead laden (begrenzt die Last auf dem OPC-Server).</summary>
    [Range(1, 16)]
    public int MaxParallelStations { get; set; } = 4;

    /// <summary>Werte je Knoten und HistoryRead-Antwort; der Server begrenzt zusätzlich selbst (Simulator: 5000), darüber folgt ein ContinuationPoint.</summary>
    [Range(100, 100_000)]
    public int HistoryPageSize { get; set; } = 5_000;

    /// <summary>Abtastrate der Live-Subscription (Millisekunden).</summary>
    [Range(100, 60_000)]
    public int SamplingIntervalMs { get; set; } = 1_000;

    /// <summary>Mindestabstand zweier Nachladevorgänge, die eine Änderung in der Subscription auslöst.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:10:00")]
    public TimeSpan LiveRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);
}
