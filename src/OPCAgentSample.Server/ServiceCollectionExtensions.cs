using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Features.ChatHistory;
using OPCAgentSample.Server.Infrastructure.Redis;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Security;
using StackExchange.Redis;

namespace OPCAgentSample.Server;

/// <summary>Registrierung der Datenhaltung; <c>Program.cs</c> bleibt dadurch schlank.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Redis als einziger Speicher: Verbindung, Stammdaten (beim Start aus dem Seed), Verdichtung (<see cref="RollupEngine"/>,
    /// <see cref="RollupReader"/>) und Audit-Log.
    /// </summary>
    public static IServiceCollection AddRedisStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ConnectionStringsOptions>(configuration, ConnectionStringsOptions.SectionName);
        services.AddValidatedOptions<RedisOptions>(configuration, RedisOptions.SectionName);
        services.TryAddSingletonTimeProvider();

        // AbortOnConnectFail=false: Der Start gelingt auch, solange Redis noch hochfährt; der Initialisierer wartet darauf.
        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var connectionString = provider.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value.Redis;
            var redisOptions = provider.GetRequiredService<IOptions<RedisOptions>>().Value;
            var configurationOptions = ConfigurationOptions.Parse(connectionString);
            configurationOptions.AbortOnConnectFail = false;
            configurationOptions.ConnectTimeout = (int)redisOptions.CommandTimeout.TotalMilliseconds;
            configurationOptions.SyncTimeout = (int)redisOptions.CommandTimeout.TotalMilliseconds;
            configurationOptions.AsyncTimeout = (int)redisOptions.CommandTimeout.TotalMilliseconds;
            return ConnectionMultiplexer.Connect(configurationOptions);
        });
        services.AddSingleton<RedisAccess>();
        services.AddSingleton<MasterDataStore>();
        services.AddSingleton<RollupReader>();
        services.AddHostedService<RedisInitializer>();
        services.AddSingleton<AuditLog>();
        return services;
    }

    /// <summary>Verlauf der Fragen (Redis) samt Aufräum-Lauf.</summary>
    public static IServiceCollection AddChatHistory(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddValidatedOptions<ChatHistoryOptions>(configuration, ChatHistoryOptions.SectionName);
        services.AddSingleton<ChatHistoryService>();
        services.AddSingleton<UsageStore>();
        services.AddHostedService<ChatHistoryCleanupService>();
        return services;
    }

    internal static void AddValidatedOptions<TOptions>(this IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(section))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }

    internal static void TryAddSingletonTimeProvider(this IServiceCollection services) =>
        services.TryAddSingleton(TimeProvider.System);
}
