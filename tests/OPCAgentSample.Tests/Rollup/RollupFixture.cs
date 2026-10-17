using System.Diagnostics;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Tests.Server.Ingestion;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace OPCAgentSample.Tests.Rollup;

/// <summary>Das Szenario als <see cref="RollupContext"/>: Kalender, Kostensätze, Füllgewichte der Artikel und eingebaute Formen.</summary>
public sealed class ScenarioContextSource(SharedHistory history) : IRollupContextSource
{
    public Task<RollupContext> GetAsync(CancellationToken cancellationToken)
    {
        var scenario = history.Scenario;
        var fill = scenario.Articles.ToDictionary(a => a.Id, a => new FillSpec(a.FillWeightTargetGrams, a.FillWeightToleranceGrams));
        var moulds = scenario.Moulds.Select(m => new MouldMount(m.Id, m.StationId, m.Nests, m.MountedUtc)).ToList();
        return Task.FromResult(new RollupContext(history.Calendar, history.Costs, fill, moulds));
    }
}

/// <summary>Redis 7 so konfiguriert wie im Betrieb (keine Persistenz, kein Verdrängen) – eine Instanz je Test der Verdichtung.</summary>
public sealed class RedisInstance : IAsyncDisposable
{
    private readonly RedisContainer _container = new RedisBuilder("redis:7")
        .WithCommand("--maxmemory-policy", "noeviction", "--save", string.Empty, "--appendonly", "no")
        .Build();

    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task StartAsync()
    {
        await _container.StartAsync();
        var options = ConfigurationOptions.Parse(ConnectionString);
        options.AllowAdmin = true;
        Connection = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task FlushAsync()
    {
        foreach (var endpoint in Connection.GetEndPoints()) await Connection.GetServer(endpoint).FlushDatabaseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Connection?.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>Nimmt die Historie des Szenarios Fenster für Fenster in den <see cref="RollupEngine"/> auf (wie die Aufnahme des Servers).</summary>
public static class RollupIngest
{
    public static RollupEngine NewEngine(IConnectionMultiplexer connection) =>
        new(connection, new ScenarioContextSource(SharedHistory.Instance));

    /// <summary>Alle Datensätze von <paramref name="from"/> bis <paramref name="until"/>, in Schicht-Fenstern.</summary>
    public static async Task<int> RunAsync(
        RollupEngine engine, DateTimeOffset from, DateTimeOffset until, string sourceId = "test")
    {
        var history = SharedHistory.Instance;
        var source = new GeneratorHistorySource(history, from);
        var windows = 0;
        for (var cursor = from; cursor < until;)
        {
            var end = history.Calendar.ShiftPeriodOf(cursor).EndUtc;
            if (end > until) end = until;
            var window = new TimeWindow(cursor, end);
            var batch = new List<HistoryRecord>();
            await foreach (var record in source.ReadAsync(cursor, end)) batch.Add(record);
            await engine.ApplyAsync(sourceId, window, batch, until);
            cursor = end;
            windows++;
        }

        return windows;
    }
}

/// <summary>Die vollständige Historie einmal in einer Redis-Instanz verdichtet; alle lesenden Tests teilen sie.</summary>
public sealed class IngestedRollup(RedisInstance redis, TimeSpan duration, int windows) : IAsyncDisposable
{
    public RedisInstance Redis { get; } = redis;

    public TimeSpan Duration { get; } = duration;

    public int Windows { get; } = windows;

    public RollupReader Reader { get; } = new(redis.Connection);

    public ValueTask DisposeAsync() => Redis.DisposeAsync();
}

public sealed class RollupFixture : IAsyncLifetime
{
    public const string Name = "Rollup";

    private readonly Lazy<Task<IngestedRollup>> _ingested = new(IngestAsync);

    public Task<IngestedRollup> GetIngestedAsync() => _ingested.Value;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_ingested.IsValueCreated) await (await _ingested.Value).DisposeAsync();
    }

    public static async Task<RedisInstance> NewRedisAsync()
    {
        var instance = new RedisInstance();
        await instance.StartAsync();
        return instance;
    }

    private static async Task<IngestedRollup> IngestAsync()
    {
        var history = SharedHistory.Instance;
        var redis = await NewRedisAsync();
        using var engine = RollupIngest.NewEngine(redis.Connection);
        var start = history.Calendar.DayPeriod(history.Scenario.DailyHistoryStart).StartUtc;
        var watch = Stopwatch.StartNew();
        var windows = await RollupIngest.RunAsync(engine, start, history.Now);
        return new IngestedRollup(redis, watch.Elapsed, windows);
    }
}

[CollectionDefinition(RollupFixture.Name)]
public sealed class RollupCollectionDefinition : ICollectionFixture<RollupFixture>;
