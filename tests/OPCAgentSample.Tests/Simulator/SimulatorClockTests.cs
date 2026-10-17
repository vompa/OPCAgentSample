using OPCAgentSample.Domain;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulator;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>Die Simulationsuhr läuft mit der konfigurierten Geschwindigkeit; Alarme entstehen und enden zur Simulationszeit.</summary>
public sealed class SimulatorClockTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(40);

    [Fact]
    public async Task Sixty_times_speed_advances_the_simulation_time_by_about_a_minute_per_second()
    {
        await using var simulator = await RunningSimulator.StartAsync(speed: 60, start: "2026-10-15T10:05:00");
        await using var client = await simulator.ConnectAsync();
        var start = HallAScenario.Create().Calendar.ToUtc(new DateTime(2026, 10, 15, 10, 5, 0)).UtcDateTime;

        var first = await client.ReadValueAsync<DateTime>("Werk.Simulation.Zeit");
        var realTime = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(3));
        var second = await client.ReadValueAsync<DateTime>("Werk.Simulation.Zeit");
        realTime.Stop();

        Assert.Equal(60.0, await client.ReadValueAsync<double>("Werk.Simulation.Geschwindigkeit"));
        Assert.True(first >= start && first < start.AddMinutes(2), $"Startzeit {first:O}");
        // Verhältnis statt fester Grenzen: Unter Last der Gesamtsuite dauern die Lesezugriffe länger. Die Uhr springt im
        // Takt von 1 s Realzeit (= 60 s Simulation), deshalb je Seite ein Takt Spielraum.
        var expected = realTime.Elapsed.TotalSeconds * 60;
        Assert.InRange((second - first).TotalSeconds, expected * 0.5 - 60, expected * 1.5 + 60);
    }

    [Fact]
    public async Task An_alarm_that_ends_in_simulation_time_is_reported_as_inactive_event()
    {
        // 10:11 Ortszeit; Alarm A-0920 (OF-02) endet laut Drehbuch um 10:15, also nach ca. 4 s Realzeit.
        await using var simulator = await RunningSimulator.StartAsync(speed: 60, start: "2026-10-15T10:11:00");
        await using var client = await simulator.ConnectAsync();
        var events = await client.SubscribeAlarmsAsync();
        await events.RefreshAsync();

        await WaitUntilAsync(() => events.Events.Any(e => e.ConditionName == "A-0920" && e.Active)); // zuerst aktiv per ConditionRefresh ...
        await WaitUntilAsync(() => events.Events.Any(e => e.ConditionName == "A-0920" && !e.Active)); // ... dann als Ereignis beendet

        var cleared = events.Events.First(e => e.ConditionName == "A-0920" && !e.Active);
        Assert.Equal("OF-02 / Station", cleared.SourceName);
        Assert.False(cleared.Retain);
        Assert.Equal(0, await client.ReadValueAsync<int>("HalleA.Oefen.OF-02.Alarme.AktiveMeldungen"));
    }

    [Fact]
    public async Task A_new_alarm_in_simulation_time_creates_a_condition_and_an_active_event()
    {
        // 09:37 Ortszeit; A-0938 (RT-01) und A-0941 (RT-04) werden laut Drehbuch in den nächsten Minuten aktiv.
        await using var simulator = await RunningSimulator.StartAsync(speed: 60, start: "2026-10-15T09:37:00");
        await using var client = await simulator.ConnectAsync();
        var events = await client.SubscribeAlarmsAsync();
        await events.RefreshAsync();
        Assert.DoesNotContain(events.Events, e => e.ConditionName == "A-0941");

        await WaitUntilAsync(() => events.Events.Any(e => e.ConditionName == "A-0941" && e.Active));

        var alarm = events.Events.First(e => e.ConditionName == "A-0941");
        Assert.Equal("RT-04 / Hydraulik", alarm.SourceName);
        Assert.Equal(500, alarm.Severity);
        Assert.True(await client.ReadValueAsync<bool>("HalleA.Pressen.RT-04.Alarme.A-0941.ActiveState.Id"));
        Assert.Equal(1, await client.ReadValueAsync<int>("HalleA.Pressen.RT-04.Alarme.AktiveAlarme"));
    }

    [Fact]
    public void Start_time_without_zone_is_vienna_local_time_and_with_zone_is_taken_as_given()
    {
        var calendar = new ShiftCalendar();
        var historyStart = HallAScenario.Create().HistoryStartUtc;

        var local = new SimulatorOptions { Start = "2026-10-15T09:42" }.ResolveStart(calendar, historyStart);
        var utc = new SimulatorOptions { Start = "2026-10-15T07:42:00Z" }.ResolveStart(calendar, historyStart);
        var offset = new SimulatorOptions { Start = "2026-10-15T09:42:00+02:00" }.ResolveStart(calendar, historyStart);
        var demo = new SimulatorOptions().ResolveStart(calendar, historyStart);

        Assert.Equal(SimulationClock.DemoStart, local);
        Assert.Equal(SimulationClock.DemoStart, utc);
        Assert.Equal(SimulationClock.DemoStart, offset);
        Assert.Equal(SimulationClock.DemoStart, demo);
        Assert.Throws<InvalidOperationException>(() => new SimulatorOptions { Start = "2026-10-01T00:00" }.ResolveStart(calendar, historyStart));
        Assert.Throws<FormatException>(() => new SimulatorOptions { Start = "gestern" }.ResolveStart(calendar, historyStart));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(s_timeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(50, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Zeitüberschreitung beim Warten auf ein Alarm-Ereignis der Simulation.");
            }
        }
    }

    /// <summary>Ein eigener Simulator mit Temp-PKI für einen Test; räumt beim Beenden auf.</summary>
    private sealed class RunningSimulator : IAsyncDisposable
    {
        private readonly string _pkiRoot = Path.Combine(Path.GetTempPath(), $"opc-agent-clock-{Guid.NewGuid():N}");
        private SimulatorHost? _host;

        public static async Task<RunningSimulator> StartAsync(double speed, string start)
        {
            var simulator = new RunningSimulator();
            simulator._host = await SimulatorHost.StartAsync(
                SimulatorFixture.CreateOptions(Path.Combine(simulator._pkiRoot, "server"), speed, start));
            return simulator;
        }

        public Task<OpcTestClient> ConnectAsync() =>
            OpcTestClient.ConnectAsync(_host!.EndpointUrl, Path.Combine(_pkiRoot, "client"));

        public async ValueTask DisposeAsync()
        {
            if (_host is not null) await _host.DisposeAsync();
            SimulatorFixture.TryDeleteDirectory(_pkiRoot);
        }
    }
}
