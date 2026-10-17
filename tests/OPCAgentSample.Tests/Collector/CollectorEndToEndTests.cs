using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Collector;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Tests.Rollup;
using OPCAgentSample.Tests.Server;
using OPCAgentSample.Tests.Server.Ingestion;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace OPCAgentSample.Tests.Collector;

/// <summary>
/// Ende-zu-Ende: Simulator im Prozess, Collector mit Sitzung, HistoryRead und Subscriptions, Redis in einem Testcontainer.
/// Geprüft wird, was sichtbar ankommt (Zähler und Ereignisse in Redis, Wasserstand, Zustand der Stationen, Uhr) – nicht die Zwischenschritte und keine Laufzeiten.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class CollectorEndToEndTests(InfrastructureFixture infrastructure, ITestOutputHelper output)
{
    /// <summary>Datenbank der Vorlage mit der Aufnahme der Generator-Quelle (siehe <see cref="InfrastructureFixture"/>).</summary>
    private const int TemplateDatabase = 14;

    private static readonly string[] s_stations = [.. PlantLayout.HallAStations.Select(s => s.Id)];

    private static readonly ShiftCalendar s_calendar = new();

    [Fact]
    public async Task Full_ingestion_over_OPC_to_the_demo_clock_gives_the_same_counters_as_the_generator_source()
    {
        await infrastructure.GetIngestedAsync();
        await using var simulator = await SimulatorInstance.StartAsync(speed: 0);
        await using var services = await infrastructure.CreateServicesAsync(configure: Register(simulator));
        await using var collector = await RunningCollector.StartAsync(services, live: false);
        var source = services.Get<OpcHistorySource>();

        var watch = Stopwatch.StartNew();
        var result = await services.Get<HistoryIngestor>().IngestAsync(source, services.Get<IDemoClock>().UtcNow);
        watch.Stop();
        output.WriteLine($"Vollaufnahme über OPC: {result.Records:N0} Datensätze in {result.Windows} Fenstern: {watch.Elapsed.TotalSeconds:F1} s");

        // Die Vorlage der Generator-Quelle liegt in Datenbank 14; die Aufnahme über OPC in Datenbank 0.
        using var template = await ConnectToTemplateAsync();
        await AssertSameCountersAsync(new RollupReader(template), services.Get<RollupReader>());
        Assert.Equal(SimulationClock.DemoStart, await services.Get<RollupReader>().ReadWatermarkAsync(OpcHistorySource.Id));
        Assert.Equal(SharedHistory.Instance.Records.Count, result.Records);
    }

    [Fact]
    public async Task Live_at_60x_new_cycles_and_states_arrive_and_the_redis_state_advances()
    {
        await using var simulator = await SimulatorInstance.StartAsync(speed: 60);
        await using var services = await TakeOverIngestedHistoryAsync(simulator);
        var reader = services.Get<RollupReader>();
        var redis = services.Get<IConnectionMultiplexer>().GetDatabase();
        var statesBefore = await StateChangeCountAsync(redis);
        var untilBefore = await RedisSourceUntilAsync(redis);
        var tenMinutes = SimulationClock.DemoStart.AddMinutes(10);

        await using var collector = await RunningCollector.StartAsync(services);

        await CollectorSettings.WaitForAsync(
            async () => (await reader.ReadRecentCyclesAsync("RT-04", SimulationClock.DemoStart.AddDays(1), 1)) is [var last] && last.TimestampUtc > tenMinutes,
            "neue Takte von RT-04 in Redis");
        await CollectorSettings.WaitForAsync(
            async () => await StateChangeCountAsync(redis) > statesBefore, "neue Zustandswechsel in Redis");
        await CollectorSettings.WaitForAsync(
            async () => await RedisSourceUntilAsync(redis) > untilBefore.AddMinutes(10), "fortgeschriebener Zustand st:RT-04 in Redis");

        var clock = services.Get<IDemoClock>();
        Assert.IsType<OpcSimulationClock>(clock);
        Assert.True(clock.UtcNow > SimulationClock.DemoStart.AddMinutes(10), $"Uhr {clock.UtcNow:O}");
        var watermark = await reader.ReadWatermarkAsync(OpcHistorySource.Id);
        Assert.True(watermark > SimulationClock.DemoStart.AddMinutes(10), $"Wasserstand {watermark:O}");
        Assert.True(watermark <= simulator.Clock.UtcNow, "Der Wasserstand darf der Uhr des OPC-Servers nicht vorauseilen.");
    }

    [Fact]
    public async Task After_an_outage_the_restarted_simulator_ends_the_gap_and_the_watermark_follows()
    {
        await using var simulator = await SimulatorInstance.StartAsync(speed: 0);
        await using var services = await TakeOverIngestedHistoryAsync(simulator);
        var sources = services.Get<RollupReader>();
        await using var collector = await RunningCollector.StartAsync(services);
        await CollectorSettings.WaitForAsync(
            async () => services.Get<OpcSessionProvider>().Current is not null, "erste OPC-Verbindung");

        // Ausfall: Der Simulator verschwindet.
        await simulator.StopHostAsync();
        await CollectorSettings.WaitForAsync(async () => services.Get<OpcSessionProvider>().Current is null, "erkannter Ausfall");
        Assert.Equal(SimulationClock.DemoStart, await sources.ReadWatermarkAsync(OpcHistorySource.Id));

        // Neustart eine Stunde später: Die Lücke [Demo-Uhr, +1 h) wird nachgeladen.
        var until = SimulationClock.DemoStart.AddHours(1);
        await simulator.StartHostAsync(speed: 0, start: "2026-10-15T10:42:00");
        await CollectorSettings.WaitForAsync(
            async () => await sources.ReadWatermarkAsync(OpcHistorySource.Id) == until, "nachgeladene Lücke (Wasserstand)");

        // Die Zähler der Woche entsprechen der Historie bis zur Demo-Uhr plus den Datensätzen der nachgeladenen Stunde.
        var gap = new HistoryGenerator(HallAScenario.Create()).Generate(SimulationClock.DemoStart, until).ToList();
        var history = SharedHistory.Instance;
        var builder = new KpiBlockBuilder(history.Calendar.WeekPeriodOf(until), history.Costs);
        builder.AddRange(history.Records);
        builder.AddRange(gap);
        var stored = await sources.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42");
        RollupAssert.Equivalent(builder.Snapshot(until), stored.Block, "Woche mit nachgeladener Lücke");
        Assert.Equal(until, services.Get<IDemoClock>().UtcNow);
    }

    [Fact]
    public async Task The_clock_follows_the_simulator_and_never_runs_ahead_of_it()
    {
        var log = new CapturingLoggerProvider();
        await using var simulator = await SimulatorInstance.StartAsync(speed: 60, start: "2026-10-15T10:05:00");
        await using var services = await infrastructure.CreateServicesAsync(configure: s =>
        {
            Register(simulator)(s);
            s.AddLogging(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(log));
        });
        await using var collector = await RunningCollector.StartAsync(services, live: false);
        var clock = services.Get<IDemoClock>();
        var start = s_calendar.ToUtc(new DateTime(2026, 10, 15, 10, 5, 0));
        Assert.IsType<OpcSimulationClock>(clock);

        await CollectorSettings.WaitForAsync(async () => services.Get<OpcSessionProvider>().Current is not null, "OPC-Verbindung");
        var first = clock.UtcNow;
        await CollectorSettings.WaitForAsync(
            async () => clock.UtcNow >= first.AddMinutes(2),
            () => $"Fortschreiten der Uhr um zwei Simulationsminuten (Start {first:O}, jetzt {clock.UtcNow:O}, Geschwindigkeit {(clock as OpcSimulationClock)?.Speed}); Protokoll: {string.Join(" | ", log.Messages)}",
            seconds: 60);
        var second = clock.UtcNow;
        var simulated = simulator.Clock.UtcNow; // danach gelesen: die Uhr des Servers geht nie nach

        Assert.True(first >= start && first < start.AddMinutes(60), $"Startzeit {first:O}");
        Assert.True(second <= simulated, $"Uhr {second:O} vor dem Simulator {simulated:O}");
    }

    private static Action<IServiceCollection> Register(SimulatorInstance simulator, IDictionary<string, string?>? overrides = null) =>
        services => services.AddOpcCollector(CollectorSettings.Configuration(simulator.EndpointUrl, simulator.ClientPki, overrides));

    /// <summary>
    /// Redis mit der Historie bis zur Demo-Uhr, deren Wasserstand dem OPC-Collector gehört: so, als hätte er sie zuvor
    /// selbst aufgenommen (das ist der Inhalt des Tests <c>Full_ingestion_…</c>), und nun fehlt nur das, was danach kommt.
    /// </summary>
    private async Task<TestServices> TakeOverIngestedHistoryAsync(SimulatorInstance simulator)
    {
        var services = await infrastructure.CreateIngestedServicesAsync(configure: Register(simulator));
        var database = services.Get<IConnectionMultiplexer>().GetDatabase();
        var watermark = await database.HashGetAsync(RollupKeys.Watermarks, GeneratorHistorySource.Id);
        Assert.True(watermark.HasValue);
        await database.HashDeleteAsync(RollupKeys.Watermarks, GeneratorHistorySource.Id);
        await database.HashSetAsync(RollupKeys.Watermarks, OpcHistorySource.Id, watermark);
        return services;
    }

    private async Task<ConnectionMultiplexer> ConnectToTemplateAsync() =>
        await ConnectionMultiplexer.ConnectAsync($"{infrastructure.RedisConnectionString},defaultDatabase={TemplateDatabase}");

    /// <summary>Alle Schichten und die Woche (Zähler), die Ereignisse der Halle A und die Auftragsereignisse sind in beiden Redis gleich.</summary>
    private static async Task AssertSameCountersAsync(RollupReader expected, RollupReader actual)
    {
        var history = SharedHistory.Instance;
        var shifts = history.Calendar.ProductionShiftsBetween(history.Scenario.HistoryStartUtc, history.Now).ToList();
        Assert.NotEmpty(shifts);
        foreach (var shift in shifts)
        {
            RollupAssert.Equivalent(
                (await expected.ReadBlockAsync(s_stations, RollupLevel.Shift, shift.Key)).Block,
                (await actual.ReadBlockAsync(s_stations, RollupLevel.Shift, shift.Key)).Block, shift.Key);
        }

        RollupAssert.Equivalent(
            (await expected.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42")).Block,
            (await actual.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42")).Block, "2026-W42");

        var start = history.Scenario.HistoryStartUtc;
        Assert.Equal(
            (await expected.ReadLossesAsync(start, history.Now)).Select(l => l.Id),
            (await actual.ReadLossesAsync(start, history.Now)).Select(l => l.Id));
        Assert.Equal(
            (await expected.ReadAlarmsAsync(start, history.Now)).Select(a => a.Id),
            (await actual.ReadAlarmsAsync(start, history.Now)).Select(a => a.Id));
        Assert.Equal(
            (await expected.ReadOrderEventsAsync(start, history.Now.AddMinutes(1))).Count,
            (await actual.ReadOrderEventsAsync(start, history.Now.AddMinutes(1))).Count);
    }

    /// <summary>Gesamtzahl der Zustandswechsel in den Streams aller Stationen.</summary>
    private static async Task<long> StateChangeCountAsync(IDatabase redis)
    {
        var lengths = await Task.WhenAll(s_stations.Select(station => redis.StreamLengthAsync(RollupKeys.StateStream(station))));
        return lengths.Sum();
    }

    private static async Task<DateTimeOffset> RedisSourceUntilAsync(IDatabase redis)
    {
        var value = await redis.HashGetAsync("st:RT-04", "source_until");
        return value.IsNullOrEmpty
            ? DateTimeOffset.MinValue
            : DateTimeOffset.Parse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
