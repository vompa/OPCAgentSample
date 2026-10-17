using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Tests.Rollup;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace OPCAgentSample.Tests.Server.Ingestion;

/// <summary>Aufnahme der Historie in Redis (Testcontainers): Umfang, Laufzeit, Wasserstand, Idempotenz, Wiederaufnahme und Lückenschluss.</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class IngestionTests(InfrastructureFixture infrastructure, ITestOutputHelper output)
{
    private static readonly SharedHistory s_history = SharedHistory.Instance;

    private static readonly string[] s_stations = [.. PlantLayout.HallAStations.Select(s => s.Id)];

    private static DateTimeOffset At(int day, int hour, int minute = 0) => s_history.Calendar.ToUtc(new DateTime(2026, 10, day, hour, minute, 0));

    /// <summary>Dienste mit leerem Redis und einer Quelle, die erst ab <paramref name="startUtc"/> liefert (kurze Aufnahme für schnelle Tests).</summary>
    private Task<TestServices> CreateShortAsync(DateTimeOffset startUtc, out GeneratorHistorySource source)
    {
        var created = new GeneratorHistorySource(s_history, startUtc);
        source = created;
        return infrastructure.CreateServicesAsync(configure: s => s.AddSingleton<IHistorySource>(created));
    }

    private static async Task<Dictionary<string, KpiBlock>> ReadBlocksAsync(RollupReader reader) =>
        new()
        {
            ["shift"] = (await reader.ReadBlockAsync(s_stations, RollupLevel.Shift, s_history.Calendar.ShiftPeriodOf(At(15, 9, 0)).Key)).Block,
            ["day"] = (await reader.ReadBlockAsync(s_stations, RollupLevel.Day, "2026-10-15")).Block,
            ["week"] = (await reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42")).Block,
        };

    [Fact]
    public async Task Full_ingestion_to_the_demo_clock_loads_every_record()
    {
        var ingested = await infrastructure.GetIngestedAsync();
        output.WriteLine($"Erzeugung der Datensätze: {ingested.GenerationTime.TotalSeconds:F1} s (nicht Teil der Aufnahme)");
        output.WriteLine($"Aufnahme: {ingested.Result.Records:N0} Datensätze in {ingested.Result.Windows} Fenstern: {ingested.IngestTime.TotalSeconds:F1} s");
        // Die Laufzeit wird nur ausgegeben, nicht geprüft: sie hängt von der Last ab.
        Assert.Equal(s_history.All.Count, ingested.Result.Records);

        await using var services = await infrastructure.CreateIngestedServicesAsync();
        var reader = services.Get<RollupReader>();
        var records = s_history.Records;
        var start = s_history.Scenario.HistoryStartUtc;

        // Jede Schicht stimmt mit der Rechnung der Kennzahlen-Bibliothek über die Rohdatensätze überein.
        var shifts = s_history.Calendar.ProductionShiftsBetween(start, s_history.Now).ToList();
        Assert.NotEmpty(shifts);
        foreach (var shift in shifts)
        {
            var stored = await reader.ReadBlockAsync(s_stations, RollupLevel.Shift, shift.Key);
            RollupAssert.Equivalent(s_history.Block(shift), stored.Block, shift.Key);
        }

        var week = await reader.ReadBlockAsync(s_stations, RollupLevel.Week, "2026-W42");
        RollupAssert.Equivalent(s_history.Week42(), week.Block, "2026-W42");

        // Die Ereignisse stehen vollständig in den Streams.
        var losses = await reader.ReadLossesAsync(start, s_history.Now);
        Assert.Equal(records.OfType<LossEvent>().Select(r => r.Id).Distinct().Count(), losses.Select(l => l.Id).Distinct().Count());
        var alarms = await reader.ReadAlarmsAsync(start, s_history.Now);
        Assert.Equal(records.OfType<AlarmRecord>().Select(r => r.Id).Distinct().Count(), alarms.Select(a => a.Id).Distinct().Count());
        var orders = await reader.ReadOrderEventsAsync(start, s_history.Now.AddMinutes(1));
        Assert.Equal(records.OfType<OrderEvent>().Select(r => (r.StationId, r.TimestampUtc, r.OrderId, r.Kind)).Distinct().Count(), orders.Count);

        // Die Tageswerte der Hallen B und C (Historie B) liegen als Tage vor.
        Assert.NotNull(await reader.ReadTotalsAsync("HALLE-B", RollupLevel.Day, "2026-10-13"));
    }

    [Fact]
    public async Task Watermark_is_the_demo_clock_and_a_second_ingestion_adds_nothing()
    {
        await using var services = await infrastructure.CreateIngestedServicesAsync(withSource: true);
        var reader = services.Get<RollupReader>();
        var source = services.Get<IHistorySource>();
        var before = await ReadBlocksAsync(reader);

        var watermark = await reader.ReadWatermarkAsync(source.SourceId);
        var again = await services.Get<HistoryIngestor>().IngestAsync(source, s_history.Now);

        Assert.Equal(s_history.Now, watermark);
        Assert.Equal(s_history.Now, await reader.ReadOverallWatermarkAsync());
        Assert.Equal(0, again.Records);
        Assert.Equal(0, again.Windows);
        var after = await ReadBlocksAsync(reader);
        foreach (var key in before.Keys) KpiBlockAssert.Equal(before[key], after[key]);
    }

    [Fact]
    public async Task Ingesting_the_same_range_twice_gives_identical_counters()
    {
        var until = At(14, 13, 30);
        await using var services = await CreateShortAsync(At(13, 6), out var source);
        var ingestor = services.Get<HistoryIngestor>();
        var reader = services.Get<RollupReader>();
        await ingestor.IngestAsync(source, until);
        var first = await ReadAllShiftsAsync(reader);

        // Dieselbe Aufnahme noch einmal in ein leeres Redis: Der Dienst baut seinen Stand neu auf, die Stammdaten kommen aus dem Seed.
        await infrastructure.FlushRedisAsync();
        await services.Get<RollupEngine>().ResetAsync();
        var second = await ingestor.IngestAsync(source, until);
        var secondShifts = await ReadAllShiftsAsync(reader);

        Assert.True(second.Records > 0);
        Assert.Equal(until, await reader.ReadWatermarkAsync(source.SourceId));
        Assert.Equal(first.Keys.Order(), secondShifts.Keys.Order());
        foreach (var (key, block) in first) KpiBlockAssert.Equal(block, secondShifts[key]);

        static async Task<Dictionary<string, KpiBlock>> ReadAllShiftsAsync(RollupReader reader)
        {
            var result = new Dictionary<string, KpiBlock>();
            foreach (var shift in s_history.Calendar.ProductionShiftsBetween(At(13, 6), At(14, 13, 30)))
            {
                result[shift.Key] = (await reader.ReadBlockAsync(s_stations, RollupLevel.Shift, shift.Key)).Block;
            }

            return result;
        }
    }

    [Fact]
    public async Task An_interrupted_run_continues_at_the_watermark_and_a_gap_is_loaded_later()
    {
        var cut = At(14, 10, 20);
        var end = At(15, 9, 0);
        await using var services = await CreateShortAsync(At(13, 6), out var source);
        var ingestor = services.Get<HistoryIngestor>();
        var reader = services.Get<RollupReader>();

        // Erster Lauf bricht mitten in der Schicht ab, später wird bis zum Ende nachgeholt (Lücke).
        var first = await ingestor.IngestAsync(source, cut);
        Assert.Equal(cut, await reader.ReadWatermarkAsync(source.SourceId));
        var second = await ingestor.IngestAsync(source, end);

        Assert.Equal(cut, second.FromUtc);
        Assert.Equal(end, await reader.ReadWatermarkAsync(source.SourceId));
        Assert.Equal(s_history.All.Count(r => r.TimestampUtc >= At(13, 6) && r.TimestampUtc < end), first.Records + second.Records);

        // Das Ergebnis ist dasselbe wie bei einer Aufnahme in einem Zug (dazu wird Redis neu befüllt).
        var actual = await ReadBlocksAsync(reader);
        await using var oneGo = await CreateShortAsync(At(13, 6), out var otherSource);
        await oneGo.Get<HistoryIngestor>().IngestAsync(otherSource, end);
        var expected = await ReadBlocksAsync(oneGo.Get<RollupReader>());
        foreach (var key in expected.Keys) KpiBlockAssert.Equal(expected[key], actual[key]);
    }

    [Fact]
    public async Task A_later_reported_event_with_an_earlier_start_recomputes_the_earlier_shift()
    {
        var shift = s_history.Shift(14, Shift.Night);
        var loss = s_history.Records.OfType<LossEvent>()
            .Where(l => l.EndUtc is not null && l.Category == LossCategory.Breakdown && shift.Contains(l.StartUtc) && l.StationId == "RT-03")
            .OrderByDescending(l => l.EndUtc - l.StartUtc)
            .First();
        var later = At(15, 6);
        var corrected = loss with { Reason = LossReasons.Unspecified, Cause = "nachgetragen", TimestampUtc = later.AddMinutes(1) };
        await using var services = await CreateShortAsync(At(13, 6), out var source);
        await services.Get<HistoryIngestor>().IngestAsync(source, later.AddMinutes(10));
        var reader = services.Get<RollupReader>();
        var before = await reader.ReadTotalsAsync("RT-03", RollupLevel.Shift, shift.Key);

        await services.Get<RollupEngine>().ApplyAsync(
            source.SourceId, new TimeWindow(later.AddMinutes(10), later.AddMinutes(20)), [corrected]);
        var after = await reader.ReadTotalsAsync("RT-03", RollupLevel.Shift, shift.Key);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(before.Reasons.ContainsKey(loss.Reason));
        Assert.True(after.Reasons.ContainsKey(LossReasons.Unspecified));
        Assert.Equal(before.Figures.GoodPieces, after.Figures.GoodPieces);
        Assert.Equal(before.Figures.ScrapPieces, after.Figures.ScrapPieces);
    }
}
