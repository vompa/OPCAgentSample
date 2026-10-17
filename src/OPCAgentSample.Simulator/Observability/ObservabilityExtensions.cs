using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OPCAgentSample.Simulator.Observability;

/// <summary>Registrierung von OpenTelemetry im Simulator: Traces, Metriken und Logs, Export per OTLP nur auf Wunsch (wie im Server).</summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "opcagent-simulator";
    public const string DefaultEndpoint = "http://localhost:4317";

    private const int MetricExportIntervalMs = 10_000;

    /// <summary>
    /// Endpunkt aus <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (Standard <c>http://localhost:4317</c>). Der Export ist nur mit
    /// <c>Observability:OtlpEnabled=true</c> an. Ohne erreichbaren Empfänger verwirft der Exporter die Daten, ohne zu blockieren oder Fehler zu werfen.
    /// </summary>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var exportEnabled = configuration.GetValue("Observability:OtlpEnabled", false);
        var endpoint = new Uri(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 } configured ? configured : DefaultEndpoint);
        var version = typeof(ObservabilityExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

        void ConfigureExporter(OtlpExporterOptions exporter)
        {
            exporter.Endpoint = endpoint;
            exporter.TimeoutMilliseconds = 5_000;
        }

        void ConfigureResource(ResourceBuilder resource) =>
            resource.AddService(ServiceName, serviceVersion: version, serviceInstanceId: Environment.MachineName);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            var resource = ResourceBuilder.CreateDefault();
            ConfigureResource(resource);
            logging.SetResourceBuilder(resource);
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = false;
            if (exportEnabled) logging.AddOtlpExporter(ConfigureExporter);
        });

        var telemetry = builder.Services.AddOpenTelemetry();
        telemetry.ConfigureResource(ConfigureResource);
        telemetry.WithTracing(tracing =>
        {
            tracing.AddSource(SimulatorTelemetry.Name);
            if (exportEnabled) tracing.AddOtlpExporter(ConfigureExporter);
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter(SimulatorTelemetry.Name).AddRuntimeInstrumentation();
            if (exportEnabled)
            {
                metrics.AddOtlpExporter((exporter, reader) =>
                {
                    ConfigureExporter(exporter);
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = MetricExportIntervalMs;
                });
            }
        });
        return builder;
    }
}
