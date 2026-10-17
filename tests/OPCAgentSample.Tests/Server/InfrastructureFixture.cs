using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OPCAgentSample.Domain;
using OPCAgentSample.Server;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Tests.Server.Ingestion;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace OPCAgentSample.Tests.Server;

/// <summary>Die einmal aufgenommene Historie: Ergebnis der Aufnahme, Laufzeit und Dauer der Erzeugung der Datensätze.</summary>
public sealed record IngestedHistory(IngestResult Result, TimeSpan IngestTime, TimeSpan GenerationTime);

/// <summary>
/// Startet Redis 7 einmal für alle Tests der Collection <see cref="Name"/>, konfiguriert wie im Betrieb (kein Verdrängen, keine
/// Persistenz). Jeder Test arbeitet in Datenbank 0, die er leer oder als Kopie der Vorlage bekommt. Die vollständige Historie wird
/// höchstens einmal aufgenommen (<see cref="GetIngestedAsync"/>) und liegt als Vorlage in Datenbank 14: Tests, die Daten brauchen,
/// kopieren sie in Sekundenbruchteilen (<c>COPY</c> samt Ablaufzeit).
/// </summary>
public sealed class InfrastructureFixture : IAsyncLifetime
{
    public const string Name = "Infrastructure";

    private const int TemplateDatabase = 14;

    private readonly RedisContainer _redis = new RedisBuilder("redis:7")
        .WithCommand("--maxmemory-policy", "noeviction", "--save", string.Empty, "--appendonly", "no")
        .Build();

    private readonly Lazy<Task<IngestedHistory>> _ingested;
    private ConnectionMultiplexer? _admin;

    /// <summary>Redis ist absichtlich nicht erreichbar (für Tests der Ausfalltoleranz).</summary>
    public const string UnreachableRedis = "localhost:1,connectTimeout=500";

    public string RedisConnectionString => _redis.GetConnectionString();

    public InfrastructureFixture() => _ingested = new Lazy<Task<IngestedHistory>>(IngestAsync);

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        var options = ConfigurationOptions.Parse(RedisConnectionString);
        options.AllowAdmin = true;
        _admin = await ConnectionMultiplexer.ConnectAsync(options);
    }

    public async Task DisposeAsync()
    {
        _admin?.Dispose();
        await _redis.DisposeAsync();
    }

    /// <summary>Leert die Datenbank der Tests (Datenbank 0); die Vorlage bleibt.</summary>
    public async Task FlushRedisAsync()
    {
        foreach (var endpoint in _admin!.GetEndPoints()) await _admin.GetServer(endpoint).FlushDatabaseAsync(0);
    }

    /// <summary>Kopie der aufgenommenen Historie (Zähler, Statistik, Ereignisse, Stammdaten) in die Datenbank der Tests.</summary>
    public async Task RestoreIngestedRedisAsync()
    {
        await GetIngestedAsync();
        await FlushRedisAsync();
        var source = _admin!.GetDatabase(TemplateDatabase);
        var batch = source.CreateBatch();
        var tasks = new List<Task>();
        foreach (var endpoint in _admin.GetEndPoints())
        {
            await foreach (var key in _admin.GetServer(endpoint).KeysAsync(TemplateDatabase, pageSize: 2000))
            {
                tasks.Add(batch.KeyCopyAsync(key, key, destinationDatabase: 0, replace: true));
            }
        }

        batch.Execute();
        await Task.WhenAll(tasks);
    }

    /// <summary>Baut den Dienste-Container der Datenhaltung ohne Webserver; Hosted Services werden nicht gestartet. Redis ist leer.</summary>
    public async Task<TestServices> CreateServicesAsync(
        TimeProvider? timeProvider = null, string? redisConnectionString = null, IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configure = null)
    {
        await FlushRedisAsync();
        return TestServices.Create(redisConnectionString ?? RedisConnectionString, timeProvider, settings, configure);
    }

    /// <summary>Dienste auf einer Kopie der aufgenommenen Historie. Optional ist die Historienquelle registriert.</summary>
    public async Task<TestServices> CreateIngestedServicesAsync(bool withSource = false, Action<IServiceCollection>? configure = null)
    {
        await RestoreIngestedRedisAsync();
        return TestServices.Create(RedisConnectionString, timeProvider: null, settings: null, services =>
        {
            if (withSource) services.AddSingleton<IHistorySource>(new GeneratorHistorySource(SharedHistory.Instance));
            configure?.Invoke(services);
        });
    }

    /// <summary>
    /// Nimmt die komplette Historie bis zur Demo-Uhr in die Vorlage auf (einmal je Testlauf) und misst die Laufzeit.
    /// Die Erzeugung der Datensätze zählt nicht zur Aufnahmezeit.
    /// </summary>
    public Task<IngestedHistory> GetIngestedAsync() => _ingested.Value;

    private async Task<IngestedHistory> IngestAsync()
    {
        var history = SharedHistory.Instance;
        var source = new GeneratorHistorySource(history);
        foreach (var endpoint in _admin!.GetEndPoints()) await _admin.GetServer(endpoint).FlushDatabaseAsync(TemplateDatabase);

        await using var services = TestServices.Create(
            $"{RedisConnectionString},defaultDatabase={TemplateDatabase}", timeProvider: null, settings: null,
            configure: s => s.AddSingleton<IHistorySource>(source));
        var watch = Stopwatch.StartNew();
        var result = await services.Get<HistoryIngestor>().IngestAsync(source, history.Now);
        watch.Stop();
        return new IngestedHistory(result, watch.Elapsed, history.GenerationTime);
    }
}

[CollectionDefinition(InfrastructureFixture.Name)]
public sealed class InfrastructureCollectionDefinition : ICollectionFixture<InfrastructureFixture>;

public sealed class TestServices : IAsyncDisposable
{
    private TestServices(ServiceProvider provider) => Provider = provider;

    public ServiceProvider Provider { get; }

    public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

    public static TestServices Create(
        string redis, TimeProvider? timeProvider, IDictionary<string, string?>? settings, Action<IServiceCollection>? configure = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = redis,
            ["Redis:CommandTimeout"] = "00:00:01",
        };
        foreach (var pair in settings ?? new Dictionary<string, string?>()) values[pair.Key] = pair.Value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        if (timeProvider is not null) services.AddSingleton(timeProvider);
        services.AddRedisStore(configuration).AddChatHistory(configuration).AddIngestion(configuration);
        services.TryAddSingleton<IDemoClock>(new ManualDemoClock(SimulationClock.DemoStart));
        configure?.Invoke(services);
        return new TestServices(services.BuildServiceProvider());
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

/// <summary>Von Hand gesetzte Uhr für Tests der Aufbewahrungsfrist.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Von Hand gesetzte Simulationsuhr der Demo (Standard: Demo-Start Do 15.10.2026 09:42).</summary>
public sealed class ManualDemoClock(DateTimeOffset start) : IDemoClock
{
    private long _ticks = start.UtcTicks;

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, value.UtcTicks);
}
