using System.ComponentModel.DataAnnotations;

namespace OPCAgentSample.Server.Live;

/// <summary>Live-Strom der Weboberfläche (Abschnitt <c>Live</c>, Spezifikation Abschnitt 13 Punkt 7).</summary>
public sealed class LiveOptions
{
    public const string SectionName = "Live";

    /// <summary>Abstand der Heartbeat-Kommentare (<c>: heartbeat</c>), damit Proxys die Verbindung nicht schließen.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:05:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Abstand, in dem die Verbindung prüft, ob sich die Uhr (Minute) geändert hat.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan ClockCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Wartezeit, die der Browser vor dem automatischen Wiederverbinden einhält (SSE-Feld <c>retry</c>).</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Längste Dauer einer Verbindung. Danach sendet der Server das Ereignis <c>idle</c> und beendet den Strom; die Oberfläche
    /// verbindet nicht von selbst neu, sondern erst bei der nächsten Eingabe. So hält ein offener Tab die Anwendung nicht ewig
    /// wach (Scale-to-zero). <see langword="null"/> = unbegrenzt (lokaler Betrieb).
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "1.00:00:00")]
    public TimeSpan? MaxStreamDuration { get; set; }

    /// <summary>Gleichzeitige Live-Verbindungen je Besitzer und Server-Instanz; weitere werden mit 429 abgewiesen.</summary>
    [Range(1, 100)]
    public int MaxConnectionsPerOwner { get; set; } = 3;
}
