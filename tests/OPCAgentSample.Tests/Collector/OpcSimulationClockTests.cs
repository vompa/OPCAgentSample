using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Collector;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;

namespace OPCAgentSample.Tests.Collector;

/// <summary>Die Simulationsuhr aus dem OPC-Knoten: fortgeschrieben mit der Geschwindigkeit, eingefroren ohne Verbindung, nie vor dem Server.</summary>
public sealed class OpcSimulationClockTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Before_the_first_reading_it_shows_the_demo_clock()
    {
        var clock = new OpcSimulationClock(new SteppingTimeProvider());

        Assert.False(clock.HasReading);
        Assert.Equal(SimulationClock.DemoStart, clock.UtcNow);
    }

    [Fact]
    public void Between_readings_it_advances_with_the_speed()
    {
        var time = new SteppingTimeProvider();
        var clock = new OpcSimulationClock(time);

        clock.Update(s_start, 60);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(clock.HasReading);
        Assert.Equal(s_start.AddMinutes(10), clock.UtcNow);
        Assert.Equal(60, clock.Speed);
    }

    [Fact]
    public void A_stopped_simulation_stays_where_it_is()
    {
        var time = new SteppingTimeProvider();
        var clock = new OpcSimulationClock(time);

        clock.Update(s_start, 0);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(s_start, clock.UtcNow);
    }

    [Fact]
    public void Without_a_connection_it_keeps_the_last_known_state()
    {
        var time = new SteppingTimeProvider();
        var clock = new OpcSimulationClock(time);
        clock.Update(s_start, 60);
        time.Advance(TimeSpan.FromSeconds(2));

        clock.Freeze();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(s_start.AddMinutes(2), clock.UtcNow);
    }

    [Fact]
    public void A_stale_reading_slightly_behind_the_extrapolated_time_is_ignored_so_the_clock_stays_monotonic()
    {
        var time = new SteppingTimeProvider();
        var clock = new OpcSimulationClock(time);
        clock.Update(s_start, 60);
        time.Advance(TimeSpan.FromSeconds(5));
        var before = clock.UtcNow;

        clock.Update(s_start.AddMinutes(4), 60); // der Knoten hinkt dem Takt der Aktualisierungsschleife hinterher

        Assert.Equal(before, clock.UtcNow);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before.AddMinutes(1), clock.UtcNow);
    }

    [Fact]
    public void A_reading_far_behind_means_the_simulation_was_reset_and_is_taken_over()
    {
        var time = new SteppingTimeProvider();
        var clock = new OpcSimulationClock(time);
        clock.Update(s_start, 1);
        time.Advance(TimeSpan.FromMinutes(30));

        clock.Update(s_start.AddHours(-3), 1);

        Assert.Equal(s_start.AddHours(-3), clock.UtcNow);
    }

    [Fact]
    public void After_the_connection_returned_the_first_reading_is_taken_over_even_if_it_is_earlier()
    {
        var time = new SteppingTimeProvider();
        var clock = new OpcSimulationClock(time);
        clock.Update(s_start, 60);
        time.Advance(TimeSpan.FromSeconds(30));
        clock.Freeze();

        clock.Update(s_start.AddMinutes(1), 60);

        Assert.Equal(s_start.AddMinutes(1), clock.UtcNow);
    }

    [Fact]
    public void It_replaces_the_demo_clock_in_the_service_container_and_a_disabled_collector_registers_nothing()
    {
        var enabled = new ServiceCollection().AddLogging().AddSingleton<IDemoClock>(new FixedClock()).AddOpcCollector(Configure("true"));
        var disabled = new ServiceCollection().AddSingleton<IDemoClock>(new FixedClock()).AddOpcCollector(Configure("false"));

        using var provider = enabled.BuildServiceProvider();
        Assert.IsType<OpcSimulationClock>(provider.GetRequiredService<IDemoClock>());
        Assert.IsType<OpcHistorySource>(provider.GetRequiredService<IHistorySource>());
        Assert.DoesNotContain(disabled, d => d.ServiceType == typeof(IHistorySource));
        Assert.IsType<FixedClock>(disabled.Single(d => d.ServiceType == typeof(IDemoClock)).ImplementationInstance);
    }

    private static IConfiguration Configure(string enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Opc:Enabled"] = enabled }).Build();

    private sealed class FixedClock : IDemoClock
    {
        public DateTimeOffset UtcNow => SimulationClock.DemoStart;
    }

    /// <summary>Zeitgeber, der nur von Hand vorrückt.</summary>
    private sealed class SteppingTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
