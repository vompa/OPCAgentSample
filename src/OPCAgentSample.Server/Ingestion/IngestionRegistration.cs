using Microsoft.Extensions.DependencyInjection.Extensions;
using OPCAgentSample.Domain;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Kpis;
using OPCAgentSample.Server.Live;
using OPCAgentSample.Server.Mcp;

namespace OPCAgentSample.Server.Ingestion;

/// <summary>Registrierung von Datenaufnahme, Kennzahlenblöcken und Live-Pfad. Voraussetzung: <c>AddRedisStore</c>.</summary>
public static class IngestionRegistration
{
    /// <summary>
    /// Registriert alles für die Aufnahme und die echten Quellen der Weboberfläche (Übersicht, Meldungen). Historienquellen
    /// (<see cref="IHistorySource"/>) registriert der Aufrufer; ohne Quelle ruht der Hintergrunddienst.
    /// </summary>
    public static IServiceCollection AddIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<IngestionOptions>(configuration, IngestionOptions.SectionName);
        services.AddValidatedOptions<PlantOptions>(configuration, PlantOptions.SectionName);
        services.TryAddSingletonTimeProvider();
        services.TryAddSingleton(_ => new ShiftCalendar());

        // Aufnahme und Verdichtung
        services.AddSingleton<IRollupContextSource, MasterDataRollupContext>();
        services.AddSingleton<RollupEngine>();
        services.AddSingleton<DataStateMonitor>();
        services.AddSingleton<HistoryIngestor>();
        services.AddSingleton<KpiBlockService>();

        // Live-Pfad und Quellen der Weboberfläche
        services.AddSingleton<PlantQueries>();
        services.AddSingleton<StationStateBuilder>();
        services.AddSingleton<StationStateStore>();
        services.AddSingleton<EventFeed>();
        services.AddSingleton<LivePath>();
        services.AddSingleton<IHallOverviewSource, LiveHallOverviewSource>();
        services.AddSingleton<IFeedSource, LiveFeedSource>();

        services.AddHostedService<IngestionService>();
        return services;
    }
}
