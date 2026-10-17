namespace OPCAgentSample.Server.Live;

/// <summary>Registrierung des Live-Stroms. Voraussetzung: <c>AddRedisCache</c>, <c>AddIngestion</c> und <c>AddApi</c>.</summary>
public static class LiveRegistration
{
    /// <summary>Optionen, Verteiler, Redis-Pub/Sub-Abonnement, Benachrichtigung des Live-Pfads und Stromdienst.</summary>
    public static IServiceCollection AddLiveStream(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<LiveOptions>(configuration, LiveOptions.SectionName);
        services.AddSingleton<LiveHub>();
        services.AddSingleton<ILiveNotifier, RedisLiveNotifier>();
        services.AddHostedService<LiveSubscriberService>();
        services.AddSingleton<LiveStreamService>();
        return services;
    }
}
