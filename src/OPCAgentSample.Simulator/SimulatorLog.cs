using Microsoft.Extensions.Logging;

namespace OPCAgentSample.Simulator;

/// <summary>Protokollmeldungen des Simulators (quellgenerierte LoggerMessage-Methoden).</summary>
internal static partial class SimulatorLog
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "OPC-UA-Simulator läuft auf {Endpoint} (Simulationszeit {Time:u}, Geschwindigkeit {Speed}x, Security {Security})")]
    public static partial void Started(this ILogger logger, string endpoint, DateTimeOffset time, double speed, string security);

    [LoggerMessage(Level = LogLevel.Error, Message = "Aktualisierung der Live-Werte abgebrochen")]
    public static partial void UpdateLoopFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Das eigene Zertifikat passte nicht mehr zur Konfiguration; es liegt jetzt unter {Path}, ein neues wurde erzeugt")]
    public static partial void CertificateReplaced(this ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "OPC-UA-Simulator beendet")]
    public static partial void Stopped(this ILogger logger);
}
