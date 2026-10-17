using Opc.Ua;
using OPCAgentSample.Domain.History;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>Alarms &amp; Conditions: aktive Alarme als AlarmConditionType-Instanzen, nur lesend (kein Quittieren, kein Schreiben).</summary>
[Collection(SimulatorFixtureGroup.Name)]
public sealed class SimulatorAlarmTests(SimulatorFixture fixture)
{
    private const string Alarm = "HalleA.Pressen.RT-04.Alarme.A-0941";

    [Fact]
    public async Task Condition_refresh_delivers_the_alarms_active_at_the_demo_clock()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var expected = ActiveAlarmIds();

        var events = await client.SubscribeAlarmsAsync();
        await events.RefreshAsync();
        await WaitUntilAsync(() => expected.All(id => events.Events.Any(e => e.ConditionName == id)));

        Assert.Contains("A-0941", expected);
        Assert.Equivalent(expected, events.Events.Select(e => e.ConditionName).Distinct().ToList(), strict: false);
        Assert.All(events.Events, e => Assert.True(e.Active && e.Retain));
    }

    [Fact]
    public async Task Alarm_carries_severity_source_station_and_assembly_text_and_acknowledge_state()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var events = await client.SubscribeAlarmsAsync();
        await events.RefreshAsync();
        await WaitUntilAsync(() => events.Events.Any(e => e.ConditionName == "A-0941"));

        var alarm = events.Events.First(e => e.ConditionName == "A-0941");

        Assert.Equal(500, alarm.Severity); // Mittel
        Assert.Equal("RT-04 / Hydraulik", alarm.SourceName);
        Assert.Equal(client.Node("HalleA.Pressen.RT-04"), alarm.SourceNode);
        Assert.Equal("ConditionWarning", alarm.ClassName);
        Assert.Equal("Zustandswarnung Presskraftaufbau 1,1 s (Soll 0,8 s)", alarm.Message);
        Assert.False(alarm.Acknowledged);
        Assert.Equal(new DateTime(2026, 10, 15, 7, 41, 0, DateTimeKind.Utc), alarm.Time);
    }

    [Fact]
    public async Task Conditions_are_browsable_below_their_station_and_counted_per_station()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        Assert.Contains("A-0941", await client.ChildNamesAsync("HalleA.Pressen.RT-04.Alarme"));
        Assert.True(await client.ReadValueAsync<bool>($"{Alarm}.ActiveState.Id"));
        Assert.False(await client.ReadValueAsync<bool>($"{Alarm}.AckedState.Id"));
        Assert.True(await client.ReadValueAsync<bool>($"{Alarm}.Retain"));
        Assert.Equal(1, await client.ReadValueAsync<int>("HalleA.Pressen.RT-04.Alarme.AktiveAlarme"));
        Assert.Equal(1, await client.ReadValueAsync<int>("HalleA.Pressen.RT-01.Alarme.AktiveMeldungen"));
        Assert.Equal(0, await client.ReadValueAsync<int>("HalleA.Pressen.RT-03.Alarme.AktiveAlarme"));

        var typeDefinition = await client.BrowseAsync(client.Node(Alarm), BrowseDirection.Forward, ReferenceTypeIds.HasTypeDefinition);
        Assert.Equal("AlarmConditionType", typeDefinition.Single().BrowseName.Name);
    }

    [Theory]
    [InlineData("Acknowledge")]
    [InlineData("Confirm")]
    [InlineData("AddComment")]
    [InlineData("Enable")]
    [InlineData("Disable")]
    [InlineData("Suppress")]
    [InlineData("RemoveFromService")]
    public async Task Condition_methods_are_rejected_with_access_denied(string method)
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var eventId = await client.ReadValueAsync<byte[]>($"{Alarm}.EventId");

        var response = await client.Session.CallAsync(null,
        [
            new CallMethodRequest
            {
                ObjectId = client.Node(Alarm),
                MethodId = client.Node($"{Alarm}.{method}"),
                InputArguments = [new Variant(eventId), new Variant(new LocalizedText("Quittiert (Test)"))],
            },
        ], default);

        Assert.Equal(StatusCodes.BadUserAccessDenied, response.Results[0].StatusCode.Code);
        Assert.False(await client.ReadValueAsync<bool>($"{Alarm}.AckedState.Id"));
        Assert.True(await client.ReadValueAsync<bool>($"{Alarm}.EnabledState.Id"));
    }

    [Fact]
    public async Task Writing_a_process_value_is_rejected_and_the_value_stays()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        const string node = "HalleA.Pressen.RT-04.Prozess.TaktzeitSoll";
        var before = await client.ReadValueAsync<double>(node);

        var response = await client.Session.WriteAsync(null,
        [
            new WriteValue { NodeId = client.Node(node), AttributeId = Attributes.Value, Value = new DataValue(new Variant(1.0)) },
            new WriteValue
            {
                NodeId = client.Node("HalleA.Oefen.OF-02.Planung.Zuordnungsregel"),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant("alle Durchmesser")),
            },
        ], default);

        Assert.All(response.Results, code => Assert.True(StatusCode.IsBad(code), code.ToString()));
        Assert.Equal(before, await client.ReadValueAsync<double>(node));
        Assert.Equal("alle Ø 180", await client.ReadValueAsync<string>("HalleA.Oefen.OF-02.Planung.Zuordnungsregel"));
    }

    private List<string> ActiveAlarmIds() =>
        [.. fixture.Generator.Generate(fixture.Scenario.HistoryStartUtc, SimulatorFixture.Now)
            .OfType<AlarmRecord>()
            .GroupBy(a => a.Id)
            .Select(g => g.MaxBy(a => a.TimestampUtc)!)
            .Where(a => a.ClearedUtc is null)
            .Select(a => a.Id)];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            try
            {
                await Task.Delay(100, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail("Zeitüberschreitung beim Warten auf Alarm-Ereignisse.");
            }
        }
    }
}
