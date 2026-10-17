using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Agent;

/// <summary>Registrierung der Claude-Anbindung. Ohne <c>Claude:ApiKey</c> bleibt die Referenz-Engine (gekennzeichnet) aktiv.</summary>
public static class ClaudeRegistration
{
    /// <summary>Nach <c>AddReferenceDemo</c> und <c>AddMcpEndpoint</c> aufrufen.</summary>
    public static IServiceCollection AddClaudeAgent(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ClaudeOptions>().Bind(configuration.GetSection(ClaudeOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClaudeOptions>, ClaudeOptionsValidator>();
        services.TryAddSingletonTimeProvider();

        services.TryAddSingleton<IClaudeModelClient, AnthropicModelClient>();
        services.TryAddSingleton<IMcpLoopback, HttpMcpLoopback>();
        services.TryAddSingleton<InternalAgentKeys>();
        services.TryAddSingleton<IAgentToolbox, McpAgentToolbox>();
        services.TryAddSingleton<IQuotaCounters, RedisQuotaCounters>();
        services.TryAddSingleton<ClaudeQuota>();
        services.TryAddSingleton<ClaudeAnswerEngine>();
        services.AddSingleton<IConfigureOptions<McpSecurityOptions>, AgentKeyConfigurator>();
        services.AddHostedService<ClaudeModeLogger>();

        // Der Modus steht erst fest, wenn die Konfiguration vollständig ist (Key aus User-Secrets bzw. Umgebung): Entscheidung beim Auflösen.
        services.Replace(ServiceDescriptor.Singleton<IAnswerEngine>(provider =>
            provider.GetRequiredService<IOptions<ClaudeOptions>>().Value.Enabled
                ? new RepeatingAnswerEngine(
                    provider.GetRequiredService<ClaudeAnswerEngine>(),
                    provider.GetRequiredService<IOptions<ClaudeOptions>>(),
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILogger<RepeatingAnswerEngine>>())
                : provider.GetRequiredService<ReferenceAnswerEngine>()));
        return services;
    }
}

/// <summary>Schreibt beim Start, welche Antwort-Engine aktiv ist. Der Key kommt nie in den Text.</summary>
public sealed partial class ClaudeModeLogger(IOptions<ClaudeOptions> options, ILogger<ClaudeModeLogger> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Enabled)
        {
            LogClaude(settings.Model, settings.Effort, settings.MaxRounds, settings.MaxToolCalls, settings.DailyQuestionsPerOwner);
        }
        else
        {
            LogReference();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Antwort-Engine: Claude (Modell {Model}, Effort {Effort}, höchstens {Rounds} Runden und {ToolCalls} Tool-Aufrufe je Frage, {Daily} Fragen je Besitzer und Tag).")]
    private partial void LogClaude(string model, string effort, int rounds, int toolCalls, int daily);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Antwort-Engine: Referenz (vorbereitete Antworten). Claude ist nicht angebunden; mit Claude:ApiKey (User-Secrets oder Umgebungsvariable Claude__ApiKey) wird er aktiviert.")]
    private partial void LogReference();
}
