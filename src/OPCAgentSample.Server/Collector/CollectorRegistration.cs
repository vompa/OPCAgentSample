using Microsoft.Extensions.DependencyInjection.Extensions;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;

namespace OPCAgentSample.Server.Collector;

/// <summary>Registrierung des OPC-Collectors. Voraussetzung: <c>AddIngestion</c>.</summary>
public static class CollectorRegistration
{
    /// <summary>
    /// Registriert Sitzung, Historienquelle, Live-Pfad und die Simulationsuhr aus dem OPC-Knoten <c>Werk.Simulation.Zeit</c>
    /// (ersetzt die Demo-Uhr). Mit <c>Opc:Enabled = false</c> geschieht nichts: Die Referenzuhr bleibt, es gibt keine Quelle.
    /// </summary>
    public static IServiceCollection AddOpcCollector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<OpcOptions>(configuration, OpcOptions.SectionName);
        if (!configuration.GetSection(OpcOptions.SectionName).GetValue(nameof(OpcOptions.Enabled), true)) return services;

        services.TryAddSingletonTimeProvider();
        services.AddSingleton<OpcSimulationClock>();
        services.Replace(ServiceDescriptor.Singleton<IDemoClock>(provider => provider.GetRequiredService<OpcSimulationClock>()));

        services.AddSingleton<OpcSessionProvider>();
        services.AddHostedService(provider => provider.GetRequiredService<OpcSessionProvider>());
        services.AddSingleton<OpcHistorySource>();
        services.AddSingleton<IHistorySource>(provider => provider.GetRequiredService<OpcHistorySource>());
        services.AddHostedService<OpcLiveService>();
        return services;
    }
}
