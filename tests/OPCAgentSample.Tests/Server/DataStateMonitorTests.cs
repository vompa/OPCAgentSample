using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Domain;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server;

/// <summary>Kaltstart-Hinweis und Erkennung „Simulationsuhr liegt hinter dem Wasserstand“ gegen den Wasserstand in Redis (Hash <c>wm</c>).</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class DataStateMonitorTests(InfrastructureFixture infrastructure)
{
    private static readonly DateTimeOffset s_realNow = new(2026, 10, 15, 7, 42, 0, TimeSpan.Zero);

    private async Task<(TestServices Services, DataStateMonitor Monitor, ManualDemoClock Clock, ManualTimeProvider Time)> CreateAsync()
    {
        var time = new ManualTimeProvider(s_realNow);
        var clock = new ManualDemoClock(SimulationClock.DemoStart);
        var services = await infrastructure.CreateServicesAsync(time, configure: s => s.AddSingleton<IDemoClock>(clock));
        return (services, services.Get<DataStateMonitor>(), clock, time);
    }

    private static Task<bool> SetWatermarkAsync(TestServices services, string source, DateTimeOffset watermark) =>
        services.Get<IConnectionMultiplexer>().GetDatabase().HashSetAsync(RollupKeys.Watermarks, source, watermark.UtcTicks);

    [Fact]
    public async Task Without_any_ingestion_there_is_no_warning_and_no_loading_flag()
    {
        var (services, monitor, _, _) = await CreateAsync();
        await using var _ = services;

        var status = await monitor.GetAsync();

        Assert.False(status.HistoryLoading);
        Assert.Null(status.Warning);
    }

    [Fact]
    public async Task The_loading_flag_follows_begin_and_end()
    {
        var (services, monitor, _, _) = await CreateAsync();
        await using var _ = services;

        monitor.BeginLoading();
        Assert.True((await monitor.GetAsync()).HistoryLoading);
        monitor.EndLoading();
        Assert.False((await monitor.GetAsync()).HistoryLoading);
    }

    [Fact]
    public async Task A_clock_before_the_watermark_is_reported_and_nothing_is_deleted()
    {
        var (services, monitor, clock, _) = await CreateAsync();
        await using var _ = services;
        await SetWatermarkAsync(services, "opc", SimulationClock.DemoStart.AddHours(5));
        clock.Set(SimulationClock.DemoStart);

        await monitor.CheckAsync();
        var status = await monitor.GetAsync();

        Assert.NotNull(status.Warning);
        Assert.Contains("Simulationsuhr", status.Warning, StringComparison.Ordinal);
        Assert.Equal(SimulationClock.DemoStart.AddHours(5), await services.Get<RollupReader>().ReadOverallWatermarkAsync());
    }

    [Fact]
    public async Task The_warning_goes_away_when_the_clock_catches_up_and_uses_the_smallest_watermark()
    {
        var (services, monitor, clock, time) = await CreateAsync();
        await using var _ = services;
        await SetWatermarkAsync(services, "a", SimulationClock.DemoStart.AddHours(5));
        await SetWatermarkAsync(services, "b", SimulationClock.DemoStart.AddHours(1));
        clock.Set(SimulationClock.DemoStart.AddHours(2));

        // Der kleinste Wasserstand (b) liegt vor der Uhr: kein Hinweis.
        Assert.Null((await monitor.GetAsync()).Warning);

        await SetWatermarkAsync(services, "b", SimulationClock.DemoStart.AddHours(6));
        clock.Set(SimulationClock.DemoStart.AddHours(3));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull((await monitor.GetAsync()).Warning);

        clock.Set(SimulationClock.DemoStart.AddHours(7));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null((await monitor.GetAsync()).Warning);
    }

    [Fact]
    public async Task The_check_is_cached_for_a_short_time()
    {
        var (services, monitor, clock, time) = await CreateAsync();
        await using var _ = services;
        await SetWatermarkAsync(services, "opc", SimulationClock.DemoStart.AddHours(5));
        Assert.NotNull((await monitor.GetAsync()).Warning);

        clock.Set(SimulationClock.DemoStart.AddHours(8));
        Assert.NotNull((await monitor.GetAsync()).Warning);
        time.Advance(TimeSpan.FromSeconds(16));
        Assert.Null((await monitor.GetAsync()).Warning);
    }

    [Fact]
    public async Task A_cold_start_shows_no_warning_while_the_ingestion_follows_the_clock()
    {
        var (services, monitor, clock, time) = await CreateAsync();
        await using var _ = services;

        // Frisches Redis: erst ohne Wasserstand, dann holt die Aufnahme bis zur Uhr auf und folgt ihr im Takt von Sekunden.
        Assert.Null((await monitor.GetAsync()).Warning);
        foreach (var seconds in new[] { 0, 10, 20, 600 })
        {
            clock.Set(SimulationClock.DemoStart.AddSeconds(seconds));
            await SetWatermarkAsync(services, "opc", clock.UtcNow.AddSeconds(-5));
            time.Advance(TimeSpan.FromSeconds(16));
            Assert.Null((await monitor.GetAsync()).Warning);
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    public async Task A_simulator_restarted_with_an_earlier_start_is_reported_with_both_times(int minutesAhead)
    {
        var (services, monitor, clock, _) = await CreateAsync();
        await using var _ = services;
        await SetWatermarkAsync(services, "opc", SimulationClock.DemoStart.AddMinutes(minutesAhead));
        clock.Set(SimulationClock.DemoStart);

        await monitor.CheckAsync();
        var warning = (await monitor.GetAsync()).Warning;

        Assert.NotNull(warning);
        Assert.Contains("(09:42)", warning, StringComparison.Ordinal);
        Assert.Contains($"({DemoTime.TimeText(SimulationClock.DemoStart.AddMinutes(minutesAhead))})", warning, StringComparison.Ordinal);
    }
}
