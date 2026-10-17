namespace OPCAgentSample.Server.Observability;

/// <summary>Konfiguration der Telemetrie (Abschnitt <c>Observability</c>).</summary>
public sealed class ObservabilityOptions
{
    public const string SectionName = "Observability";

    /// <summary>Standardadresse des OTLP-Empfängers (gRPC), wenn weder Konfiguration noch <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> sie setzt.</summary>
    public const string DefaultEndpoint = "http://localhost:4317";

    /// <summary>Export per OTLP an/aus. Standard: aus (die Instrumentierung bleibt aktiv, es wird nur nichts gesendet).</summary>
    public bool OtlpEnabled { get; init; }
}
