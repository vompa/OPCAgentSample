using System.Reflection;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace OPCAgentSample.Server.Observability;

/// <summary>Registrierung von OpenTelemetry: Traces, Metriken und Logs, Export per OTLP (optional, standardmäßig aus).</summary>
public static class ObservabilityExtensions
{
    public const string ServiceName = "opcagent-server";

    private const int MetricExportIntervalMs = 10_000;

    /// <summary>
    /// Die Instrumentierung ist immer aktiv; der Export per OTLP nur mit <c>Observability:OtlpEnabled=true</c>. Der Endpunkt
    /// kommt aus <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (Standard <c>http://localhost:4317</c>). Der Export läuft im
    /// Hintergrund über Batch-Prozessoren: Ist der Empfänger nicht erreichbar, verwirft der Exporter die Daten, ohne den Server
    /// zu verzögern oder Fehler zu werfen.
    /// </summary>
    public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var options = configuration.GetSection(ObservabilityOptions.SectionName).Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        var exportEnabled = options.OtlpEnabled;
        var endpoint = new Uri(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] is { Length: > 0 } configured ? configured : ObservabilityOptions.DefaultEndpoint);
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
            tracing
                .AddSource(AppTelemetry.Name)
                .AddAspNetCoreInstrumentation(aspNet => aspNet.Filter = context => !IsNoise(context.Request.Path))
                .AddHttpClientInstrumentation();
            if (exportEnabled) tracing.AddOtlpExporter(ConfigureExporter);
        });
        telemetry.WithMetrics(metrics =>
        {
            metrics
                .AddMeter(AppTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .SetExemplarFilter(ExemplarFilterType.TraceBased);
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

    /// <summary>Gesundheitsprüfung und der dauerhaft offene Live-Strom würden Traces und Latenzen verfälschen.</summary>
    private static bool IsNoise(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/api/live", StringComparison.OrdinalIgnoreCase);
}
