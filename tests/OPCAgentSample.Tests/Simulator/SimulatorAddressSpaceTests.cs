using Opc.Ua;
using OPCAgentSample.Domain;
using OPCAgentSample.Simulator;

namespace OPCAgentSample.Tests.Simulator;

[Collection(SimulatorFixtureGroup.Name)]
public sealed class SimulatorAddressSpaceTests(SimulatorFixture fixture)
{
    private static readonly string[] s_pressGroups =
    [
        "Identitaet", "Zustand", "Auftrag", "Zaehler", "Energie", "Ereignisse", "Alarme", "Prozess", "Zyklus", "Teilstationen",
        "Material", "Werkzeug", "Hydraulik", "Ausschussgruende",
    ];

    private static readonly string[] s_plantChildren = ["Simulation", "Schichtkalender", "HalleA", "HalleB", "HalleC"];
    private static readonly string[] s_hallAChildren = ["Klima", "Pressen", "Puffer", "Oefen", "Verpackung"];
    private static readonly string[] s_ovenGroups =
        ["Identitaet", "Zustand", "Ereignisse", "Alarme", "Kapazitaet", "LaufendeCharge", "Temperatur", "Planung", "Sicherheit"];
    private static readonly string[] s_otherHallChildren = ["Zustand", "Zaehler"];

    [Fact]
    public async Task Plant_namespace_has_index_2_and_the_agreed_uri()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        Assert.Equal(2, client.NamespaceIndex);
        Assert.Equal(2, client.Session.NamespaceUris.GetIndex("urn:vompa:opc-agent-sample:plant"));
    }

    [Fact]
    public async Task Objects_folder_contains_plant_with_halls_shift_calendar_and_simulation()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        Assert.Contains("Werk", await ChildNames(client, ObjectIds.ObjectsFolder));
        var plant = await client.ChildNamesAsync("Werk");
        Assert.Equivalent(s_plantChildren, plant, strict: false);
        Assert.Equivalent(s_hallAChildren, await client.ChildNamesAsync("HalleA"), strict: false);
    }

    [Fact]
    public async Task All_stations_exist_under_their_area_with_the_defined_type()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        var expected = new Dictionary<string, string>
        {
            ["HalleA.Pressen"] = "RundtischType", ["HalleA.Puffer"] = "PufferType", ["HalleA.Oefen"] = "OfenType",
            ["HalleA.Verpackung"] = "VerpackungType",
        };
        foreach (var (area, typeName) in expected)
        {
            foreach (var station in await client.ChildNamesAsync(area))
            {
                var definition = await client.BrowseAsync(
                    client.Node($"{area}.{station}"), BrowseDirection.Forward, ReferenceTypeIds.HasTypeDefinition);
                Assert.Equal(typeName, definition.Single().BrowseName.Name);
            }
        }

        Assert.Equal(["RT-01", "RT-02", "RT-03", "RT-04"], await client.ChildNamesAsync("HalleA.Pressen"));
        Assert.Equal(["PU-OFEN"], await client.ChildNamesAsync("HalleA.Puffer"));
        Assert.Equal(["OF-01", "OF-02"], await client.ChildNamesAsync("HalleA.Oefen"));
        Assert.Equal(["VP-01", "VP-02"], await client.ChildNamesAsync("HalleA.Verpackung"));
    }

    [Fact]
    public async Task Station_types_derive_from_StationType()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        foreach (var type in new[] { "RundtischType", "OfenType", "PufferType", "VerpackungType" })
        {
            var parents = await client.BrowseAsync(client.Node($"Typen.{type}"), BrowseDirection.Inverse, ReferenceTypeIds.HasSubtype);
            Assert.Equal("StationType", parents.Single().BrowseName.Name);
        }

        var stationType = await client.BrowseAsync(client.Node("Typen.StationType"), BrowseDirection.Inverse, ReferenceTypeIds.HasSubtype);
        Assert.Equal(ObjectTypeIds.BaseObjectType, ExpandedNodeId.ToNodeId(stationType.Single().NodeId, client.Session.NamespaceUris));
    }

    [Fact]
    public async Task Rotary_table_exposes_all_groups_of_the_specification()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        Assert.Equivalent(s_pressGroups, await client.ChildNamesAsync("HalleA.Pressen.RT-04"), strict: false);
        var process = await client.ChildNamesAsync("HalleA.Pressen.RT-04.Prozess");
        Assert.Contains("PresskraftAufbauzeit", process);
        Assert.Contains("Fuellgewicht", process);
        Assert.Contains("Presskraft", process);
        Assert.Contains("Formtemperatur", process);
    }

    [Fact]
    public async Task Oven_and_buffer_and_packaging_expose_their_groups()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        Assert.Equivalent(s_ovenGroups, await client.ChildNamesAsync("HalleA.Oefen.OF-02"), strict: false);
        Assert.Contains("AnzahlPlatten", await client.ChildNamesAsync("HalleA.Puffer.PU-OFEN.Bestand"));
        Assert.Contains("Kartonmagazin", await client.ChildNamesAsync("HalleA.Verpackung.VP-02.Verpackung"));
        Assert.Contains("Auftrag", await client.ChildNamesAsync("HalleA.Verpackung.VP-02"));
    }

    [Fact]
    public async Task Oven_assignment_rule_is_a_readable_node()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        foreach (var oven in fixture.Scenario.Ovens)
        {
            var rule = await client.ReadValueAsync<string>($"HalleA.Oefen.{oven.StationId}.Planung.Zuordnungsregel");
            Assert.Equal(oven.AssignmentRule, rule);
        }

        Assert.Equal("alle Ø 180", await client.ReadValueAsync<string>("HalleA.Oefen.OF-02.Planung.Zuordnungsregel"));
    }

    [Fact]
    public async Task Process_values_carry_engineering_units()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        var expected = new Dictionary<string, string>
        {
            ["HalleA.Pressen.RT-04.Prozess.Taktzeit"] = "s",
            ["HalleA.Pressen.RT-04.Prozess.Presskraft"] = "kN",
            ["HalleA.Pressen.RT-04.Prozess.Fuellgewicht"] = "g",
            ["HalleA.Pressen.RT-04.Prozess.Formtemperatur"] = "°C",
            ["HalleA.Pressen.RT-04.Hydraulik.Systemdruck"] = "bar",
            ["HalleA.Klima.Temperatur"] = "°C",
        };
        foreach (var (node, unit) in expected)
        {
            var units = await client.BrowseAsync(client.Node(node), BrowseDirection.Forward, ReferenceTypeIds.HasProperty);
            var property = units.Single(r => r.BrowseName.Name == "EngineeringUnits");
            var value = await client.ReadNodeAsync(ExpandedNodeId.ToNodeId(property.NodeId, client.Session.NamespaceUris), Attributes.Value);
            var information = (EUInformation)ExtensionObject.ToEncodeable((ExtensionObject)value.Value);
            Assert.Equal(unit, information.DisplayName.Text);
        }
    }

    [Fact]
    public async Task Operating_state_is_an_enumeration_with_the_specified_states()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        var dataType = await client.ReadNodeAsync(client.Node("HalleA.Pressen.RT-04.Zustand.Betriebszustand"), Attributes.DataType);
        Assert.Equal(client.Node("Typen.Betriebszustand"), (NodeId)dataType.Value);

        var strings = await client.ReadAsync($"Typen.Betriebszustand.{BrowseNames.EnumStrings}");
        var states = ((LocalizedText[])strings.Value).Select(t => t.Text).ToArray();
        Assert.Equal(
            ["Läuft", "Leerlauf", "Rüsten", "Störung", "WartungGeplant", "WartetMaterial", "WartetVorstufe", "LäuftOhneAuftrag"], states);
        Assert.Equal(Enum.GetValues<OperatingState>().Length, states.Length);
    }

    [Fact]
    public async Task Halls_B_and_C_offer_only_state_and_counters()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        foreach (var hall in new[] { "HalleB", "HalleC" })
        {
            Assert.Equivalent(s_otherHallChildren, await client.ChildNamesAsync(hall), strict: false);
            Assert.True(await client.ReadValueAsync<long>($"{hall}.Zaehler.2026-W42.F.StueckGut") > 0);
        }

        // Soll: Werk-Zähler Halle B, Woche 42, Früh = 245.000 Ist abzüglich 3.564 Ausschuss.
        Assert.Equal(245_000L - 3_564, await client.ReadValueAsync<long>("HalleB.Zaehler.2026-W42.F.StueckGut"));
    }

    [Fact]
    public async Task No_variable_of_the_plant_is_writable()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        var variables = 0;
        var pending = new Queue<NodeId>([client.Node("Werk")]);
        while (pending.Count > 0)
        {
            foreach (var reference in await client.BrowseAsync(pending.Dequeue()))
            {
                var id = ExpandedNodeId.ToNodeId(reference.NodeId, client.Session.NamespaceUris);
                if (reference.NodeClass == NodeClass.Variable)
                {
                    variables++;
                    var level = await client.ReadNodeAsync(id, Attributes.UserAccessLevel);
                    Assert.True(level.Value is byte, $"{id}: {level.StatusCode}");
                    Assert.Equal(0, (byte)level.Value & AccessLevels.CurrentWrite);
                }
                else
                {
                    pending.Enqueue(id);
                }
            }
        }

        Assert.True(variables > 400, $"Nur {variables} Variablen gefunden.");
    }

    private static async Task<List<string>> ChildNames(OpcTestClient client, NodeId node) =>
        [.. (await client.BrowseAsync(node)).Select(r => r.BrowseName.Name)];
}
