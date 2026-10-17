using System.Diagnostics;
using Opc.Ua;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Domain.Opc;
using Xunit.Abstractions;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>HistoryRead (Raw) liefert exakt die Datensätze des Generators, auch seitenweise über ContinuationPoints.</summary>
[Collection(SimulatorFixtureGroup.Name)]
public sealed class SimulatorHistoryTests(SimulatorFixture fixture, ITestOutputHelper output)
{
    private const string Rt04 = "HalleA.Pressen.RT-04";

    private DateTimeOffset HistoryStart => fixture.Scenario.HistoryStartUtc;

    private DateTimeOffset Local(int hour, int minute, int day = 15) =>
        fixture.Scenario.Calendar.ToUtc(new DateTime(2026, 10, day, hour, minute, 0));

    [Fact]
    public async Task Cycle_values_of_a_window_equal_the_generator_records()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var (from, until) = (Local(8, 30), SimulatorFixture.Now);
        var expected = fixture.Generator.Cycles("RT-04", from, until).ToList();

        var result = await client.HistoryReadAsync($"{Rt04}.Prozess.Taktzeit", from.UtcDateTime, until.UtcDateTime);

        Assert.Equal(expected.Count, result.Values.Count);
        Assert.InRange(expected.Count, 600, 1_000);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].CycleTimeSeconds, (double)result.Values[i].Value);
            Assert.Equal(expected[i].TimestampUtc.UtcDateTime, result.Values[i].SourceTimestamp);
        }

        // Die Drift ist sichtbar: Presskraftaufbau steigt im Fenster von ca. 0,8 auf 1,1 s.
        var buildUp = await client.HistoryReadAsync($"{Rt04}.Prozess.PresskraftAufbauzeit", from.UtcDateTime, until.UtcDateTime);
        Assert.InRange((double)buildUp.Values[0].Value, 0.75, 0.85);
        Assert.InRange((double)buildUp.Values[^1].Value, 1.05, 1.15);
    }

    [Fact]
    public async Task Paging_with_continuation_points_returns_the_same_values()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var (from, until) = (Local(6, 0), SimulatorFixture.Now);
        var expected = fixture.Generator.Cycles("RT-04", from, until).ToList();

        var result = await client.HistoryReadAsync($"{Rt04}.Prozess.Presskraft", from.UtcDateTime, until.UtcDateTime, numValuesPerNode: 250);

        Assert.Equal((int)Math.Ceiling(expected.Count / 250.0), result.Pages);
        Assert.Equal(expected.Select(c => c.PressForceKn), result.Values.Select(v => (double)v.Value));
        Assert.Equal(expected.Select(c => c.TimestampUtc.UtcDateTime), result.Values.Select(v => v.SourceTimestamp));
    }

    [Fact]
    public async Task All_cycle_values_of_a_rotary_table_are_retrievable_in_pages()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var expectedCount = fixture.Generator.Cycles("RT-04", HistoryStart, SimulatorFixture.Now).Count();

        var watch = Stopwatch.StartNew();
        var result = await client.HistoryReadAsync($"{Rt04}.Prozess.Fuellgewicht", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);
        watch.Stop();

        output.WriteLine($"RT-04 Fuellgewicht: {result.Values.Count} Werte in {result.Pages} Seiten, {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(expectedCount, result.Values.Count);
        Assert.Equal((int)Math.Ceiling(expectedCount / 5_000.0), result.Pages);
        Assert.InRange(expectedCount, 100_000, 140_000);
        Assert.True(result.Values.Zip(result.Values.Skip(1)).All(pair => pair.First.SourceTimestamp < pair.Second.SourceTimestamp));
    }

    [Fact]
    public async Task All_cycle_values_of_all_rotary_tables_are_retrievable()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        string[] tables = ["RT-01", "RT-02", "RT-03", "RT-04"];
        var expectedCount = tables.Sum(id => fixture.Generator.Cycles(id, HistoryStart, SimulatorFixture.Now).Count());

        var watch = Stopwatch.StartNew();
        var pages = 0;
        var received = 0;
        foreach (var id in tables)
        {
            var result = await client.HistoryReadAsync(
                $"HalleA.Pressen.{id}.Prozess.Taktzeit", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);
            received += result.Values.Count;
            pages += result.Pages;
        }

        watch.Stop();
        output.WriteLine($"Alle Zykluswerte (Taktzeit, 4 Rundtische): {received} Werte in {pages} Seiten, {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(expectedCount, received);
        Assert.InRange(received, 500_000, 600_000);
    }

    [Fact]
    public async Task Cycle_metadata_carry_order_article_batch_and_scrap_reason()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var (from, until) = (Local(8, 0, day: 8), Local(9, 0, day: 8));
        var expected = fixture.Generator.Cycles("RT-04", from, until).ToList();

        Task<HistoryResult> Read(string name) => client.HistoryReadAsync($"{Rt04}.Zyklus.{name}", from.UtcDateTime, until.UtcDateTime);

        Assert.Equal(expected.Select(c => c.OrderId ?? string.Empty), (await Read("AuftragsNr")).Values.Select(v => (string)v.Value));
        Assert.Equal(expected.Select(c => c.ArticleId), (await Read("ArtikelNr")).Values.Select(v => (string)v.Value));
        Assert.Equal(expected.Select(c => c.MixtureBatchId), (await Read("MischungChargenId")).Values.Select(v => (string)v.Value));
        Assert.Equal(expected.Select(c => c.TargetCycleSeconds), (await Read("SollTaktzeit")).Values.Select(v => (double)v.Value));
        Assert.Equal(expected.Select(c => c.Scrap is { } s ? (int)s + 1 : 0), (await Read("Ausschussgrund")).Values.Select(v => (int)v.Value));
        Assert.Contains(expected, c => !c.IsGood); // Qualitätssperre nach Formwechsel (Do 08.10. früh) hat Ausschuss
    }

    [Fact]
    public async Task Minute_values_equal_the_generator_records()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var (from, until) = (Local(6, 0), SimulatorFixture.Now);

        var climate = await client.HistoryReadAsync("HalleA.Klima.Temperatur", from.UtcDateTime, until.UtcDateTime);
        var oil = await client.HistoryReadAsync($"{Rt04}.Hydraulik.Oeltemperatur", from.UtcDateTime, until.UtcDateTime, numValuesPerNode: 50);

        var expectedClimate = MinuteValues(MinuteVariables.HallAClimateSource, MinuteVariables.Temperature, from, until);
        var expectedOil = MinuteValues("RT-04", MinuteVariables.OilTemperature, from, until);
        Assert.Equal(222, expectedClimate.Count);
        Assert.Equal(expectedClimate.Select(m => m.Value), climate.Values.Select(v => (double)v.Value));
        Assert.Equal(expectedClimate.Select(m => m.TimestampUtc.UtcDateTime), climate.Values.Select(v => v.SourceTimestamp));
        Assert.Equal(expectedOil.Select(m => m.Value), oil.Values.Select(v => (double)v.Value));
        Assert.True(oil.Pages > 1);
    }

    [Fact]
    public async Task Integer_minute_values_keep_their_type()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var (from, until) = (Local(6, 0), SimulatorFixture.Now);

        var plates = await client.HistoryReadAsync("HalleA.Puffer.PU-OFEN.Bestand.AnzahlPlatten", from.UtcDateTime, until.UtcDateTime);

        var expected = MinuteValues("PU-OFEN", MinuteVariables.PlateCount, from, until);
        Assert.All(plates.Values, v => Assert.IsType<int>(v.Value));
        Assert.Equal(expected.Select(m => (int)m.Value), plates.Values.Select(v => (int)v.Value));
    }

    [Fact]
    public async Task Operating_state_history_equals_the_state_changes()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var expected = fixture.Generator.Generate(HistoryStart, SimulatorFixture.Now)
            .OfType<StateChange>().Where(s => s.StationId == "RT-03").ToList();

        var result = await client.HistoryReadAsync(
            "HalleA.Pressen.RT-03.Zustand.Betriebszustand", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);

        Assert.Equal(expected.Select(s => (int)s.State), result.Values.Select(v => (int)v.Value));
        Assert.Equal(expected.Select(s => s.TimestampUtc.UtcDateTime), result.Values.Select(v => v.SourceTimestamp));
        Assert.Contains(result.Values, v => (int)v.Value == (int)OperatingState.Fault);
    }

    [Fact]
    public async Task Loss_events_are_delivered_as_json_with_beginning_and_end_versions()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var expected = fixture.Generator.Generate(HistoryStart, SimulatorFixture.Now)
            .OfType<LossEvent>().Where(l => l.StationId == "RT-03").ToList();

        var result = await client.HistoryReadAsync(
            "HalleA.Pressen.RT-03.Ereignisse.Verluste", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime, numValuesPerNode: 100);

        var actual = result.Values.Select(v => HistoryJson.Deserialize<LossEvent>((string)v.Value)).ToList();
        Assert.Equal(expected, actual);
        Assert.Contains(actual, l => l.Reason == LossReasons.FabricFeedFault && l.StartUtc == Local(5, 12));
        Assert.Contains(actual, l => l is { IsOpen: false, Category: LossCategory.Breakdown, Cause: "Greifer 2 klemmt" });
    }

    [Fact]
    public async Task Alarm_and_order_events_are_delivered_as_json()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var records = fixture.Generator.Generate(HistoryStart, SimulatorFixture.Now).ToList();

        var alarms = await client.HistoryReadAsync(
            "HalleA.Pressen.RT-04.Ereignisse.Alarmmeldungen", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);
        var orders = await client.HistoryReadAsync(
            "HalleA.Pressen.RT-04.Ereignisse.Auftragsereignisse", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);

        Assert.Equal(
            records.OfType<AlarmRecord>().Where(a => a.StationId == "RT-04"),
            alarms.Values.Select(v => HistoryJson.Deserialize<AlarmRecord>((string)v.Value)));
        Assert.Equal(
            records.OfType<OrderEvent>().Where(o => o.StationId == "RT-04"),
            orders.Values.Select(v => HistoryJson.Deserialize<OrderEvent>((string)v.Value)));
        Assert.NotEmpty(orders.Values);
    }

    [Fact]
    public async Task Traceability_records_of_ovens_and_packaging_are_available()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var records = fixture.Generator.Generate(HistoryStart, SimulatorFixture.Now).ToList();

        var charges = await client.HistoryReadAsync(
            "HalleA.Oefen.OF-01.Ereignisse.Ofenchargen", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);
        var pallets = await client.HistoryReadAsync(
            "HalleA.Verpackung.VP-01.Ereignisse.Paletten", HistoryStart.UtcDateTime, SimulatorFixture.Now.UtcDateTime);

        Assert.Equal(
            records.OfType<OvenCharge>().Count(c => c.StationId == "OF-01"),
            charges.Values.Count(v => HistoryJson.Deserialize<OvenCharge>((string)v.Value).PressLotIds is not null));
        Assert.Equal(records.OfType<PalletRecord>().Count(p => p.StationId == "VP-01"), pallets.Values.Count);
    }

    [Fact]
    public async Task Reading_backwards_returns_the_newest_values_first()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var from = Local(9, 0);
        var expected = fixture.Generator.Cycles("RT-04", from, SimulatorFixture.Now).Reverse().Take(5).ToList();

        // Endzeit vor Startzeit: rückwärts lesen.
        var result = await client.HistoryReadAsync($"{Rt04}.Prozess.Dicke", SimulatorFixture.Now.UtcDateTime, from.UtcDateTime, numValuesPerNode: 5, maxPages: 1);

        Assert.Equal(expected.Select(c => c.ThicknessMm), result.Values.Select(v => (double)v.Value));
    }

    [Fact]
    public async Task Windows_after_the_simulation_time_are_cut_off_at_now()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        var result = await client.HistoryReadAsync(
            $"{Rt04}.Prozess.Presskraft", Local(9, 30).UtcDateTime, SimulatorFixture.Now.UtcDateTime.AddDays(2));

        Assert.All(result.Values, v => Assert.True(v.SourceTimestamp < SimulatorFixture.Now.UtcDateTime));
        Assert.NotEmpty(result.Values);
    }

    [Fact]
    public async Task Released_continuation_points_cannot_be_used_again()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var details = new ReadRawModifiedDetails
        {
            StartTime = HistoryStart.UtcDateTime,
            EndTime = SimulatorFixture.Now.UtcDateTime,
            NumValuesPerNode = 10,
            IsReadModified = false,
            ReturnBounds = false,
        };
        var node = client.Node($"{Rt04}.Prozess.Presskraft");
        var first = await client.Session.HistoryReadAsync(
            null, new ExtensionObject(details), TimestampsToReturn.Both, false, [new HistoryReadValueId { NodeId = node }], default);
        Assert.True(StatusCode.IsGood(first.Results[0].StatusCode), first.Results[0].StatusCode.ToString());
        var point = first.Results[0].ContinuationPoint;
        Assert.NotNull(point);

        var released = await client.Session.HistoryReadAsync(
            null, new ExtensionObject(details), TimestampsToReturn.Both, true,
            [new HistoryReadValueId { NodeId = node, ContinuationPoint = point }], default);
        var reused = await client.Session.HistoryReadAsync(
            null, new ExtensionObject(details), TimestampsToReturn.Both, false,
            [new HistoryReadValueId { NodeId = node, ContinuationPoint = point }], default);

        Assert.True(StatusCode.IsGood(released.Results[0].StatusCode), released.Results[0].StatusCode.ToString());
        Assert.True(StatusCode.IsBad(reused.Results[0].StatusCode));
    }

    [Fact]
    public async Task Non_historized_nodes_and_invalid_continuation_points_are_rejected()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var details = new ReadRawModifiedDetails
        {
            StartTime = HistoryStart.UtcDateTime,
            EndTime = SimulatorFixture.Now.UtcDateTime,
            NumValuesPerNode = 10,
            IsReadModified = false,
            ReturnBounds = false,
        };

        var plain = await client.Session.HistoryReadAsync(
            null, new ExtensionObject(details), TimestampsToReturn.Both, false,
            [new HistoryReadValueId { NodeId = client.Node($"{Rt04}.Identitaet.Bezeichnung") }], default);
        var invalid = await client.Session.HistoryReadAsync(
            null, new ExtensionObject(details), TimestampsToReturn.Both, false,
            [new HistoryReadValueId { NodeId = client.Node($"{Rt04}.Prozess.Presskraft"), ContinuationPoint = Guid.NewGuid().ToByteArray() }], default);

        Assert.Equal(StatusCodes.BadHistoryOperationUnsupported, plain.Results[0].StatusCode.Code);
        Assert.Equal(StatusCodes.BadContinuationPointInvalid, invalid.Results[0].StatusCode.Code);
    }

    private List<MinuteValue> MinuteValues(string station, string variable, DateTimeOffset from, DateTimeOffset until) =>
        [.. fixture.Generator.Generate(from, until).OfType<MinuteValue>().Where(m => m.StationId == station && m.Variable == variable)];
}
