using Microsoft.Extensions.DependencyInjection.Extensions;

namespace OPCAgentSample.Server.Demo;

public static class DemoServiceCollectionExtensions
{
    /// <summary>
    /// Registriert die Referenz-/Vorschau-Implementierungen der Fachinhalte. Mit <c>TryAdd</c>: Wer vorher eine eigene
    /// Implementierung registriert (oder danach <c>Replace</c> aufruft), ersetzt sie gezielt.
    /// </summary>
    public static IServiceCollection AddReferenceDemo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DemoOptions>().Bind(configuration.GetSection(DemoOptions.SectionName));

        services.TryAddSingleton<IDemoClock, ReferenceDemoClock>();
        services.TryAddSingleton<IInboxSource, RuleInboxSource>();
        services.TryAddSingleton<IRoleProfileSource, ReferenceRoleProfileSource>();
        services.TryAddSingleton<IPlantStatusSource, ReferencePlantStatusSource>();
        services.TryAddSingleton<ReferenceAnswerEngine>();
        services.TryAddSingleton<IAnswerEngine>(provider => provider.GetRequiredService<ReferenceAnswerEngine>());
        services.TryAddSingleton<ISampleHistorySource>(provider => provider.GetRequiredService<ReferenceAnswerEngine>());
        return services;
    }
}
