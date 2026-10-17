using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Server.Live;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server.Ingestion;

/// <summary>Der Hintergrunddienst: ohne Quelle ruht er, mit Quelle holt er bis zur Uhr auf und setzt periodisch fort.</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class IngestionServiceTests(InfrastructureFixture infrastructure)
{
    private static readonly SharedHistory s_history = SharedHistory.Instance;

    private static async Task WaitForAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }

        Assert.Fail($"Zeitüberschreitung: {what}");
    }

    [Fact]
    public async Task Without_a_registered_source_the_service_does_nothing_and_says_so()
    {
        var log = new CapturingLoggerProvider();
        await using var services = await infrastructure.CreateServicesAsync(
            configure: s => s.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(log)));
        var service = services.Provider.GetServices<IHostedService>().OfType<IngestionService>().Single();

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        Assert.Null(await services.Get<RollupReader>().ReadOverallWatermarkAsync());
        Assert.Contains(log.Messages, m => m.Contains("Keine Historienquelle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_a_source_it_catches_up_to_the_clock_publishes_the_state_and_continues_periodically()
    {
        var start = s_history.Calendar.ToUtc(new DateTime(2026, 10, 5, 0, 0, 0));
        var first = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualDemoClock(first);
        var source = new GeneratorHistorySource(s_history, start);
        await using var services = await infrastructure.CreateServicesAsync(
            settings: new Dictionary<string, string?> { ["Ingestion:PollInterval"] = "00:00:01" },
            configure: s => s.AddSingleton<IDemoClock>(clock).AddSingleton<IHistorySource>(source));
        var watermarks = services.Get<RollupReader>();
        var service = services.Provider.GetServices<IHostedService>().OfType<IngestionService>().Single();

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () => await watermarks.ReadWatermarkAsync(source.SourceId) == first, "Aufholen bis zur Uhr");
            await WaitForAsync(
                async () => await services.Get<IConnectionMultiplexer>().GetDatabase().KeyExistsAsync("st:RT-01"), "Zustand der Stationen in Redis");

            clock.Set(first.AddHours(2));
            await WaitForAsync(async () => await watermarks.ReadWatermarkAsync(source.SourceId) == first.AddHours(2), "periodische Fortsetzung");

            // Der Zustand der Stationen wird nach dem Wasserstand veröffentlicht: auf das sichtbare Ergebnis warten,
            // sonst bricht StopAsync unter Last die Veröffentlichung ab
            await WaitForAsync(
                async () => (await services.Get<StationStateStore>().GetAsync(CancellationToken.None)).Snapshot.SourceUntilUtc == first.AddHours(2),
                "Veröffentlichung des fortgesetzten Stands in Redis");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        var state = await services.Get<StationStateStore>().GetAsync(CancellationToken.None);
        Assert.Equal("Redis", state.SourceLabel);
        Assert.Equal(first.AddHours(2), state.Snapshot.SourceUntilUtc);
    }
}

/// <summary>Sammelt Logmeldungen für Tests.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages) return [.. _messages];
        }
    }

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages) owner._messages.Add(formatter(state, exception));
        }
    }
}
