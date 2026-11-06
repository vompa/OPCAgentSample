using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Verdrahtung von <c>AddClaudeAgent</c>: ohne Key bleibt die Referenz-Engine aktiv und nichts vom Modellaufruf wird gebaut.</summary>
public sealed class ClaudeRegistrationTests
{
    private static ServiceProvider BuildProvider(string? apiKey, Func<IChatClient> chatClient)
    {
        var settings = new Dictionary<string, string?>();
        if (apiKey is not null) settings["Claude:ApiKey"] = apiKey;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDemoClock>(new ManualDemoClock(new DateTimeOffset(2026, 10, 15, 7, 42, 0, TimeSpan.Zero)));
        services.AddSingleton(Options.Create(new DemoOptions()));
        services.AddSingleton<ReferenceAnswerEngine>();
        services.AddSingleton<IAnswerEngine>(provider => provider.GetRequiredService<ReferenceAnswerEngine>());
        services.AddSingleton(_ => chatClient()); // vor AddClaudeAgent: die Registrierung ersetzt ihn nicht (TryAdd)
        services.AddClaudeAgent(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Given_no_key_When_the_provider_resolves_the_engine_Then_the_reference_engine_is_active_and_no_chat_client_is_built()
    {
        var built = 0;
        using var provider = BuildProvider(apiKey: null, () =>
        {
            built++;
            throw new InvalidOperationException("Ohne Key darf kein Chat-Client entstehen.");
        });

        var engine = provider.GetRequiredService<IAnswerEngine>();

        Assert.IsType<ReferenceAnswerEngine>(engine);
        Assert.Equal(0, built);
    }

    [Fact]
    public void Given_a_key_When_the_provider_resolves_the_model_client_Then_it_is_the_ChatClientModelClient()
    {
        using var provider = BuildProvider("sk-ant-test-key", () => new NoCallChatClient());

        var client = provider.GetRequiredService<IAgentModelClient>();

        Assert.IsType<ChatClientModelClient>(client);
    }

    [Fact]
    public void Given_a_key_When_the_provider_resolves_the_anthropic_client_Then_the_container_builds_the_real_client()
    {
        using var provider = BuildProvider("sk-ant-test-key", () => new NoCallChatClient());

        var client = provider.GetRequiredService<IAnthropicClient>();

        Assert.IsType<AnthropicClient>(client);
    }

    /// <summary>Der Chat-Client wird beim Auflösen nur gebaut, nie aufgerufen.</summary>
    private sealed class NoCallChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
