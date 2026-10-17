using Opc.Ua;
using OPCAgentSample.Domain;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>Live-Werte zur angehaltenen Demo-Uhr (Do 15.10.2026 09:42) stimmen mit dem <c>LiveStateProvider</c> überein.</summary>
[Collection(SimulatorFixtureGroup.Name)]
public sealed class SimulatorLiveValueTests(SimulatorFixture fixture)
{
    private static readonly string[] s_productionStations = ["RT-01", "RT-02", "RT-03", "RT-04", "VP-01", "VP-02"];

    [Fact]
    public async Task Rt04_press_force_build_up_is_about_1_1_seconds_and_taktzeit_about_4_6()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var snapshot = fixture.Provider.Station("RT-04", SimulatorFixture.Now);

        var buildUp = await client.ReadValueAsync<double>("HalleA.Pressen.RT-04.Prozess.PresskraftAufbauzeit");
        var cycleMean = await client.ReadValueAsync<double>("HalleA.Pressen.RT-04.Prozess.TaktzeitMittel5Min");

        Assert.InRange(buildUp, 1.05, 1.15);
        Assert.Equal(snapshot.BuildUpSeconds!.Value, buildUp, 0.05);
        Assert.Equal(snapshot.CycleTimeSeconds!.Value, cycleMean, 1e-9);
        Assert.InRange(cycleMean, 4.5, 4.7);
        Assert.Equal(4.2, await client.ReadValueAsync<double>("HalleA.Pressen.RT-04.Prozess.TaktzeitSoll"));
    }

    [Fact]
    public async Task Hall_climate_is_23_4_degrees_and_48_percent()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var climate = fixture.Provider.Climate(SimulatorFixture.Now);

        var temperature = await client.ReadAsync("HalleA.Klima.Temperatur");
        var humidity = await client.ReadValueAsync<double>("HalleA.Klima.Luftfeuchte");

        Assert.Equal(climate.Celsius, (double)temperature.Value);
        Assert.Equal(climate.Humidity, humidity);
        Assert.Equal(23.4, Math.Round((double)temperature.Value, 1));
        Assert.Equal(48, Math.Round(humidity));
        Assert.True(await client.ReadValueAsync<bool>("HalleA.Klima.ImSollbereich"));
        // Der Zeitstempel ist die Simulationszeit, nicht die Realzeit.
        Assert.Equal(SimulatorFixture.Now.UtcDateTime, temperature.SourceTimestamp);
    }

    [Theory]
    [MemberData(nameof(ProductionStations))]
    public async Task Operating_state_and_counters_match_the_live_state_provider(string stationId)
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var snapshot = fixture.Provider.Station(stationId, SimulatorFixture.Now);
        var station = PlantLayout.Get(stationId);
        var path = $"HalleA.{(station.Area == Area.Pressing ? "Pressen" : "Verpackung")}.{stationId}";

        Assert.Equal((int)snapshot.State, await client.ReadValueAsync<int>($"{path}.Zustand.Betriebszustand"));
        Assert.Equal(snapshot.StateSinceUtc.UtcDateTime, await client.ReadValueAsync<DateTime>($"{path}.Zustand.ZustandSeit"));
        Assert.Equal(snapshot.PiecesSinceShiftStart - snapshot.ScrapSinceShiftStart, await client.ReadValueAsync<int>($"{path}.Zaehler.StueckGut"));
        Assert.Equal(snapshot.ScrapSinceShiftStart, await client.ReadValueAsync<int>($"{path}.Zaehler.StueckAusschuss"));
        Assert.Equal(snapshot.OrderId ?? string.Empty, await client.ReadValueAsync<string>($"{path}.Auftrag.AuftragsNr"));
        Assert.Equal(snapshot.PlannedShiftQuantity, await client.ReadValueAsync<int>($"{path}.Auftrag.PlanmengeSchicht"));
    }

    [Fact]
    public async Task Known_start_state_of_the_story_is_visible()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        Assert.Equal(2_760, await client.ReadValueAsync<int>("HalleA.Pressen.RT-04.Zaehler.StueckGut")
            + await client.ReadValueAsync<int>("HalleA.Pressen.RT-04.Zaehler.StueckAusschuss"));
        Assert.Equal((int)OperatingState.WaitingForUpstream, await client.ReadValueAsync<int>("HalleA.Verpackung.VP-02.Zustand.Betriebszustand"));
        Assert.Equal("Executing", await client.ReadValueAsync<string>("HalleA.Pressen.RT-01.Zustand.MachineryItemState"));
        Assert.Equal("NotExecuting", await client.ReadValueAsync<string>("HalleA.Verpackung.VP-02.Zustand.MachineryItemState"));
        Assert.Equal(42, await client.ReadValueAsync<int>("HalleA.Puffer.PU-OFEN.Bestand.AnzahlPlatten"));
        Assert.Equal("F-180-03", await client.ReadValueAsync<string>("HalleA.Pressen.RT-04.Werkzeug.FormId"));
        Assert.Equal("F-125-07", await client.ReadValueAsync<string>("HalleA.Pressen.RT-02.Werkzeug.FormId"));
        Assert.Equal("M-2410-17", await client.ReadValueAsync<string>("HalleA.Pressen.RT-02.Material.ChargenId"));
    }

    [Fact]
    public async Task Missing_data_is_reported_with_bad_no_data_and_not_as_zero()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        var form = await client.ReadAsync("HalleA.Pressen.RT-01.Werkzeug.HuebeSeitWechsel");

        Assert.Equal(StatusCodes.BadNoData, form.StatusCode.Code);
    }

    [Fact]
    public async Task Ovens_report_occupancy_charge_and_temperatures()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);

        foreach (var ovenId in new[] { "OF-01", "OF-02" })
        {
            var oven = fixture.Provider.Oven(ovenId, SimulatorFixture.Now);
            var path = $"HalleA.Oefen.{ovenId}";

            Assert.Equal(5, await client.ReadValueAsync<int>($"{path}.Kapazitaet.Chargenplaetze"));
            Assert.Equal(oven.OccupiedPlaces, await client.ReadValueAsync<int>($"{path}.Kapazitaet.BelegtePlaetze"));
            Assert.Equal(oven.OccupancySinceShiftStart * 100, await client.ReadValueAsync<double>($"{path}.Kapazitaet.Belegung"), 1e-9);
            Assert.Equal(oven.HoldTemperatureCelsius, await client.ReadValueAsync<double>($"{path}.Temperatur.Zone1Soll"));
            Assert.InRange(await client.ReadValueAsync<double>($"{path}.Temperatur.Zone2Ist"), 170, 190);
        }
    }

    [Fact]
    public async Task Shift_calendar_and_simulation_clock_are_readable()
    {
        await using var client = await OpcTestClient.ConnectAsync(fixture.EndpointUrl, fixture.ClientPki);
        var shift = fixture.Scenario.Calendar.ShiftPeriodOf(SimulatorFixture.Now);

        Assert.Equal("Früh", await client.ReadValueAsync<string>("Schichtkalender.AktuelleSchicht"));
        Assert.Equal(shift.Key, await client.ReadValueAsync<string>("Schichtkalender.SchichtSchluessel"));
        Assert.Equal(shift.StartUtc.UtcDateTime, await client.ReadValueAsync<DateTime>("Schichtkalender.Beginn"));
        Assert.Equal(shift.EndUtc.UtcDateTime, await client.ReadValueAsync<DateTime>("Schichtkalender.Ende"));
        Assert.True(await client.ReadValueAsync<bool>("Schichtkalender.InProduktion"));
        Assert.Equal(SimulatorFixture.Now.UtcDateTime, await client.ReadValueAsync<DateTime>("Werk.Simulation.Zeit"));
        Assert.Equal(0.0, await client.ReadValueAsync<double>("Werk.Simulation.Geschwindigkeit"));
    }

    public static TheoryData<string> ProductionStations() => [.. s_productionStations];
}
