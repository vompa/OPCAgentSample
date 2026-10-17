using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Server.Collector;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Tests.Simulator;
using Xunit.Abstractions;

namespace OPCAgentSample.Tests.Collector;

/// <summary>
/// Der Collector liest per HistoryRead exakt die Datensätze, aus denen der Simulator seine Knoten speist (Generator mit
/// festem Seed): dieselbe Menge, gleiche Felder, nach Zeit sortiert – auch seitenweise über ContinuationPoints.
/// </summary>
[Collection(SimulatorFixtureGroup.Name)]
public sealed class OpcHistorySourceTests(SimulatorFixture simulator, ITestOutputHelper output)
{
    private static readonly ShiftCalendar s_calendar = new();

    [Fact]
    public async Task A_full_shift_read_over_OPC_equals_the_generator_records()
    {
        var shift = s_calendar.ShiftPeriod(new DateOnly(2026, 10, 14), Shift.Late);

        var actual = await ReadAsync(shift.StartUtc, shift.EndUtc);
        var expected = simulator.Generator.Generate(shift.StartUtc, shift.EndUtc).ToList();

        output.WriteLine($"Spätschicht Mi 14.10.: {actual.Count:N0} Datensätze");
        AssertSameRecords(expected, actual);
        Assert.Contains(actual, r => r is LossEvent);
        Assert.Contains(actual, r => r is CycleRecord { Scrap: not null });
    }

    [Fact]
    public async Task The_running_shift_up_to_the_clock_with_open_events_equals_the_generator_records_and_paging_changes_nothing()
    {
        var from = s_calendar.ShiftPeriodOf(SimulatorFixture.Now).StartUtc;

        var actual = await ReadAsync(from, SimulatorFixture.Now, pageSize: 500);
        var expected = simulator.Generator.Generate(from, SimulatorFixture.Now).ToList();

        output.WriteLine($"Laufende Frühschicht bis 09:42, Seiten à 500 Werte: {actual.Count:N0} Datensätze");
        AssertSameRecords(expected, actual);
        Assert.Contains(actual, r => r is LossEvent { EndUtc: null });
        Assert.Contains(actual, r => r is CycleRecord { StationId: "RT-04" });
        Assert.Contains(actual, r => r is OvenCharge);
        Assert.Contains(actual, r => r is MinuteValue { StationId: MinuteVariables.HallAClimateSource });
    }

    [Fact]
    public async Task The_history_starts_at_the_beginning_of_week_41_and_an_empty_window_is_empty()
    {
        var (source, run) = await StartAsync();
        await using var started = run;

        Assert.Equal(simulator.Scenario.HistoryStartUtc, source.HistoryStartUtc);
        Assert.Empty(await Collect(source, SimulatorFixture.Now, SimulatorFixture.Now));
        Assert.Empty(await Collect(source, SimulatorFixture.Now.AddHours(1), SimulatorFixture.Now.AddHours(2))); // Zukunft: noch nichts
    }

    private async Task<List<HistoryRecord>> ReadAsync(DateTimeOffset from, DateTimeOffset until, int pageSize = 5000)
    {
        var (source, run) = await StartAsync(pageSize);
        await using var started = run;
        var records = await Collect(source, from, until);
        Assert.Equal(records.OrderBy(r => r.TimestampUtc).Select(r => r.TimestampUtc), records.Select(r => r.TimestampUtc));
        return records;
    }

    private static async Task<List<HistoryRecord>> Collect(OpcHistorySource source, DateTimeOffset from, DateTimeOffset until)
    {
        var records = new List<HistoryRecord>();
        await foreach (var record in source.ReadAsync(from, until)) records.Add(record);
        return records;
    }

    private async Task<(OpcHistorySource Source, IAsyncDisposable Run)> StartAsync(int pageSize = 5000)
    {
        var pki = Path.Combine(Path.GetTempPath(), $"opc-agent-source-{Guid.NewGuid():N}");
        var configuration = CollectorSettings.Configuration(
            simulator.EndpointUrl, pki, new Dictionary<string, string?> { ["Opc:HistoryPageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var services = new ServiceCollection().AddLogging().AddOpcCollector(configuration);
        var provider = services.BuildServiceProvider();
        var sessions = provider.GetRequiredService<OpcSessionProvider>();
        await ((IHostedService)sessions).StartAsync(CancellationToken.None);
        return (provider.GetRequiredService<OpcHistorySource>(), new Cleanup(sessions, provider, pki));
    }

    /// <summary>Vergleicht Mengen von Datensätzen über die Prüfsumme je Datensatz (gleicher Typ, gleiche Felder, gleiche Anzahl).</summary>
    private static void AssertSameRecords(IReadOnlyList<HistoryRecord> expected, IReadOnlyList<HistoryRecord> actual)
    {
        Assert.Equal(expected.GroupBy(r => r.GetType().Name).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"),
            actual.GroupBy(r => r.GetType().Name).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}"));
        Assert.Equal(Fingerprints(expected), Fingerprints(actual));
    }

    private static List<ulong> Fingerprints(IEnumerable<HistoryRecord> records) =>
        [.. records.Select(r => HistoryChecksum.Of([r]).Value).Order()];

    private sealed class Cleanup(OpcSessionProvider sessions, ServiceProvider provider, string pki) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await ((IHostedService)sessions).StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
            SimulatorFixture.TryDeleteDirectory(pki);
        }
    }
}
