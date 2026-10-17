using System.Globalization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulator;
using OPCAgentSample.Simulator.History;
using OPCAgentSample.Simulator.Live;
using OPCAgentSample.Simulator.Model;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>
/// Sichert, dass Optimierungen im Simulator (Zwischenspeicher der Taktwerte, Vorausberechnung des Live-Stroms,
/// inkrementeller Historienabschnitt) die erzeugten Werte nicht verändern. Die Prüfsummen wurden vor den
/// Optimierungen mit dem unveränderten Generator ermittelt (Seed der Demo).
/// </summary>
public sealed class LiveFeedDeterminismTests
{
    // Ortszeit Wien, Do 15.10.2026: kurz vor dem Schichtwechsel 14:00 (Palettenversand 14:00 liegt im Fenster).
    private static readonly DateTimeOffset s_start = new(2026, 10, 15, 11, 55, 0, TimeSpan.Zero);

    private const ulong ExpectedGeneratedChecksum = 10716837576292420060UL;
    private const long ExpectedGeneratedCount = 20456;
    private const ulong ExpectedCycleChecksum = 12635809672951974966UL;
    private const ulong ExpectedLiveChecksum = 3605011467205413573UL;

    [Fact]
    public void Generated_history_window_is_unchanged()
    {
        var generator = new HistoryGenerator(HallAScenario.Create());

        var records = HistoryChecksum.Of(generator.Generate(s_start - TimeSpan.FromHours(2), s_start + TimeSpan.FromHours(1)));
        var cycles = HistoryChecksum.Of(generator.Cycles("RT-04", s_start - TimeSpan.FromHours(6), s_start + TimeSpan.FromHours(1))
            .Concat(generator.Cycles("RT-01", s_start - TimeSpan.FromMinutes(20), s_start + TimeSpan.FromMinutes(5))));

        Assert.Equal(ExpectedGeneratedCount, records.Count);
        Assert.Equal(ExpectedGeneratedChecksum, records.Value);
        Assert.Equal(ExpectedCycleChecksum, cycles.Value);
    }

    [Fact]
    public void Live_values_and_alarm_changes_over_a_shift_change_are_unchanged()
    {
        var runtime = SimulatorRuntime.Create(new SimulatorOptions { StartUtc = s_start, Speed = 0 });
        var hash = new Fnv();
        var now = s_start;

        // Sekundentakt wie im Betrieb, danach größere Sprünge (Geschwindigkeit > 1) über den Schichtwechsel.
        for (var second = 1; second <= 90; second++)
        {
            now = s_start + TimeSpan.FromSeconds(second);
            runtime.Feed.Advance(now);
            if (second % 30 == 0) AddLiveValues(hash, runtime.Feed, now);
        }

        for (var step = 1; step <= 40; step++)
        {
            now += TimeSpan.FromSeconds(50 + (step % 7));
            runtime.Feed.Advance(now);
            if (step % 8 == 0) AddLiveValues(hash, runtime.Feed, now);
        }

        Assert.Equal(ExpectedLiveChecksum, hash.Value);
    }

    [Fact]
    public void Incrementally_grown_history_equals_a_fresh_read()
    {
        var generator = new HistoryGenerator(HallAScenario.Create());
        var clock = new SteppedClock { UtcNow = s_start };
        var incremental = new HistoryService(generator, clock);
        var minute = new MinuteHistory(MinuteVariables.EnergyKwh);

        // Der laufende Abschnitt wächst mit der Uhr (inkl. Schichtwechsel und Wechsel des Sechs-Stunden-Abschnitts).
        for (var minutes = 0; minutes <= 150; minutes += 7)
        {
            clock.UtcNow = s_start + TimeSpan.FromMinutes(minutes);
            _ = incremental.Read(minute, "RT-01", s_start - TimeSpan.FromHours(1), clock.UtcNow).Count();
        }

        var fresh = new HistoryService(generator, clock);
        foreach (var (binding, station) in new (HistoryBinding, string)[]
                 {
                     (minute, "RT-01"), (new MinuteHistory(MinuteVariables.OilTemperature), "RT-04"),
                     (new StateHistory(), "VP-01"), (new RecordHistory(typeof(PalletRecord)), "VP-01"),
                 })
        {
            var expected = fresh.Read(binding, station, s_start - TimeSpan.FromHours(8), clock.UtcNow).ToList();
            var actual = incremental.Read(binding, station, s_start - TimeSpan.FromHours(8), clock.UtcNow).ToList();
            Assert.Equal(expected, actual);
        }
    }

    private static void AddLiveValues(Fnv hash, LiveFeed feed, DateTimeOffset now)
    {
        foreach (var (key, live) in feed.Compute(now).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            hash.Add(key);
            hash.Add(Convert.ToString(live.Value, CultureInfo.InvariantCulture));
            hash.Add(live.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var change in feed.State.DrainAlarmChanges()) hash.Add(HistoryJson.Serialize(change));
    }

    private sealed class SteppedClock : ISimulationClock
    {
        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class Fnv
    {
        public ulong Value { get; private set; } = 14695981039346656037UL;

        public void Add(string? text)
        {
            foreach (var character in text ?? "\0") Value = (Value ^ character) * 1099511628211UL;
            Value = (Value ^ 0xFFFF) * 1099511628211UL;
        }
    }
}
