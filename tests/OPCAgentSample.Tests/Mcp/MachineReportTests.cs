using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Mcp;

/// <summary>
/// Maschinen-Report (<c>GET /api/reports/machines</c>) gegen die aufgenommene Historie zur Demo-Uhr (Do 15.10.2026 09:42):
/// Die Zahlen müssen mit den MCP-Tools und den Referenzantworten übereinstimmen, weil beide dieselben Funktionen nutzen.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class MachineReportTests(InfrastructureFixture infrastructure)
{
    private static readonly string[] s_ranges = ["shift", "yesterday", "week", "fourWeeks"];

    private async Task<McpHost> HostAsync() => await McpHost.GetAsync(infrastructure);

    private async Task<JsonElement> ReportAsync(string role, string? range = null, bool? compare = null)
    {
        var host = await HostAsync();
        using var client = host.Factory.CreateClient();
        var query = $"/api/reports/machines?role={role}"
            + (range is null ? string.Empty : $"&range={range}")
            + (compare is null ? string.Empty : $"&compare={compare.Value.ToString().ToLowerInvariant()}");
        using var response = await client.GetAsync(new Uri(query, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> CallAsync(string role, string tool, object? arguments = null) =>
        await (await HostAsync()).CallAsync(role, tool, arguments);

    private static JsonElement Station(JsonElement report, string id) => report.GetProperty("stations").Items().First(s => s.Text("id") == id);

    private static IEnumerable<string> Names(JsonElement report) => report.GetProperty("stations").Items().Select(s => s.Text("id"));

    // ---- Zeiträume und Rollen --------------------------------------------------------------------------------

    [Theory]
    [InlineData(McpRoles.ShiftLead, "shift")]
    [InlineData(McpRoles.HallLead, "week")]
    [InlineData(McpRoles.ProductionLead, "fourWeeks")]
    public async Task Role_decides_the_default_range(string role, string expected)
    {
        var report = await ReportAsync(role);

        Assert.Equal(expected, report.Text("range"));
        Assert.Equal(expected, report.Text("defaultRange"));
        Assert.Equal(role, report.Text("role"));
        Assert.True(report.GetProperty("compare").GetBoolean());
        Assert.Equal(["RT-01", "RT-02", "RT-03", "RT-04", "OF-01", "OF-02", "VP-01", "VP-02"], Names(report));
        Assert.Equal("Do 15.10. 09:42", report.Text("dataAsOf"));
    }

    [Fact]
    public async Task Every_range_can_be_chosen_and_names_the_previous_period()
    {
        foreach (var range in s_ranges)
        {
            var report = await ReportAsync(McpRoles.HallLead, range);
            Assert.Equal(range, report.Text("range"));
            Assert.Equal(McpRoles.HallLead, report.Text("role"));
            Assert.Equal("week", report.Text("defaultRange"));
            Assert.False(string.IsNullOrEmpty(report.Text("previousLabel")));
        }
    }

    [Fact]
    public async Task Compare_false_drops_the_previous_period()
    {
        var report = await ReportAsync(McpRoles.HallLead, "week", compare: false);

        Assert.False(report.GetProperty("compare").GetBoolean());
        Assert.False(report.Has("previousLabel"));
        Assert.False(report.At("summary").Has("previous"));
        Assert.False(report.At("costs").Has("previousTotalEuro"));
        Assert.All(report.GetProperty("stations").Items(), s => Assert.False(s.Has("previous")));
    }

    [Fact]
    public async Task Unknown_range_and_unknown_role_are_rejected()
    {
        var host = await HostAsync();
        using var client = host.Factory.CreateClient();

        using var badRange = await client.GetAsync(new Uri("/api/reports/machines?role=hall_lead&range=year", UriKind.Relative));
        using var badRole = await client.GetAsync(new Uri("/api/reports/machines?role=boss", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badRole.StatusCode);
    }

    [Fact]
    public async Task Report_contains_no_personal_data()
    {
        var text = (await ReportAsync(McpRoles.ProductionLead)).GetRawText();

        foreach (var name in new[] { "Huber", "Gruber", "Wallner", "Martin", "Katharina", "Stefan" })
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }
    }

    // ---- Abgleich mit den MCP-Tools ---------------------------------------------------------------------------

    [Fact]
    public async Task Week_oee_per_station_and_hall_matches_get_oee()
    {
        var report = await ReportAsync(McpRoles.HallLead, "week");
        var oee = await CallAsync(McpRoles.HallLead, "get_oee", new { scope = "Halle A", zeitraum = "diese Woche", gruppierung = "Station" });

        foreach (var row in oee.At("gruppen").Items())
        {
            var station = Station(report, row.Text("gruppe"));
            if (row.Has("belegung_prozent"))
            {
                Assert.Equal(row.Number("belegung_prozent"), station.Number("occupancyPercent"));
                continue;
            }

            Assert.Equal(row.Number("oee_prozent"), station.Number("oee.oeePercent"));
            Assert.Equal(row.Number("verfuegbarkeit_prozent"), station.Number("oee.availabilityPercent"));
            Assert.Equal(row.Number("leistung_prozent"), station.Number("oee.performancePercent"));
            Assert.Equal(row.Number("qualitaet_prozent"), station.Number("oee.qualityPercent"));
            Assert.Equal(row.Number("ist"), station.Number("actual"));
            Assert.Equal(row.Number("soll"), station.Number("planned"));
        }

        Assert.Equal(oee.Number("gesamt.oee_prozent"), report.Number("summary.oee.oeePercent"));
        Assert.Equal(77, report.Number("summary.oee.oeePercent")); // Wochenbericht: Halle A KW 42 = 77 %
    }

    [Fact]
    public async Task Week_pareto_and_costs_match_get_downtime_pareto_and_the_reference_values()
    {
        var report = await ReportAsync(McpRoles.HallLead, "week");
        var hall = await CallAsync(McpRoles.HallLead, "get_downtime_pareto", new { scope = "Halle A", zeitraum = "diese Woche", bewertung = "Kosten" });
        var press = await CallAsync(McpRoles.HallLead, "get_downtime_pareto", new { scope = "RT-03", zeitraum = "diese Woche", bewertung = "Kosten" });

        Assert.Equal(hall.Number("kosten.gesamt_euro"), report.Number("costs.totalEuro"));
        Assert.Equal(hall.Number("kosten.stillstand_euro"), report.Number("costs.downtimeEuro"));
        Assert.Equal(hall.Number("kosten.ausschuss_euro"), report.Number("costs.scrapEuro"));
        Assert.Equal(hall.Text("kosten.groesster_posten.station"), report.Text("costs.topItems.0.station"));
        Assert.Equal(hall.Text("kosten.groesster_posten.grund"), report.Text("costs.topItems.0.reason"));

        var rt03 = Station(report, "RT-03").GetProperty("pareto");
        foreach (var (expected, index) in press.At("eintraege").Items().Take(3).Select((e, i) => (e, i)))
        {
            Assert.Equal(expected.Text("reason"), rt03[index].Text("reason"));
            Assert.Equal(expected.Number("dauer_min"), rt03[index].Number("minutes"));
            Assert.Equal(expected.Number("anzahl"), rt03[index].Number("count"));
            Assert.Equal(expected.Number("kosten_euro"), rt03[index].Number("euro"));
        }

        // Referenz KW 42: Gewebezuführung 6,4 h und 14 Ereignisse in Halle A, davon 9 an RT-03.
        const string Fabric = "Störung Gewebezuführung";
        var total = report.GetProperty("stations").Items()
            .SelectMany(s => s.GetProperty("pareto").Items()).Where(p => p.Text("reason") == Fabric).Sum(p => p.Number("minutes"));
        Assert.InRange(total / 60, 6.35, 6.45);
        Assert.Equal(9, rt03.Items().First(p => p.Text("reason") == Fabric).Number("count"));
        Assert.Equal(hall.At("eintraege").Item("reason", Fabric).Number("dauer_min"), total, 0.2);
    }

    [Fact]
    public async Task Yesterday_shift_comparison_matches_the_reference_82_80_72()
    {
        var report = await ReportAsync(McpRoles.HallLead, "yesterday");
        var shifts = report.At("shiftComparison.shifts");

        Assert.Equal([82, 80, 72], shifts.Items().Select(s => (int)s.Number("oee.oeePercent")));
        Assert.Equal(["Früh", "Spät", "Nacht"], shifts.Items().Select(s => s.Text("shift")));
        Assert.Equal(78, report.Number("summary.oee.oeePercent")); // Tagesbesprechung: gestern 78 %
        Assert.All(shifts.Items(), s => Assert.True(s.Number("startupLossPieces") > 0 && s.Number("startupLossCount") > 0));
        Assert.True(shifts.Items().Last().Number("oee.availabilityPercent") < shifts.Items().First().Number("oee.availabilityPercent"));
    }

    [Fact]
    public async Task Current_shift_matches_get_shift_summary_and_flags_the_drift_on_RT04()
    {
        var report = await ReportAsync(McpRoles.ShiftLead, "shift");
        var summary = await CallAsync(McpRoles.ShiftLead, "get_shift_summary", new { scope = "Halle A", schicht = "aktuell" });
        var stations = summary.At("schichten.0.stationen");

        foreach (var id in new[] { "RT-01", "RT-02", "RT-03", "RT-04", "VP-01", "VP-02" })
        {
            var line = stations.Item("station", id);
            var station = Station(report, id);
            Assert.Equal(line.Number("ist"), station.Number("actual"));
            Assert.Equal(line.Number("soll"), station.Number("planned"));
            Assert.Equal(line.Number("ist_soll_prozent"), station.Number("planPercent"));
            Assert.Equal(line.Number("stillstand_min"), station.Number("downtimeMinutes"));
        }

        Assert.Equal("Früh Do 15.10.", report.Text("rangeLabel").Split(" · ")[1]);
        var cycle = Station(report, "RT-04").GetProperty("trends").Items().First(t => t.Text("key") == "cycle");
        Assert.True(cycle.Number("outsidePercent") > 10, "Die Taktdrift ab 08:30 liegt außerhalb des Bands.");
        Assert.Equal(cycle.GetProperty("labels").GetArrayLength(), cycle.GetProperty("values").GetArrayLength());
        var finding = report.GetProperty("findings").Items().First(f => f.Text("id") == "process-RT-04-cycle");
        Assert.Equal("RT-04", finding.Text("station"));
        Assert.Contains("RT-04", finding.Text("question"), StringComparison.Ordinal);
        Assert.Contains("laufenden Schicht", finding.Text("question"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ask_context_names_the_exact_range_and_the_values_behind_each_kind_of_finding()
    {
        var host = await HostAsync();
        using var scope = host.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ReportAskContextResolver>();

        var process = (await resolver.ResolveAsync(new ReportRef("machines", "shift", "process-RT-04-cycle"), CancellationToken.None)).Text;
        var shifts = (await resolver.ResolveAsync(new ReportRef("machines", "week", "shift-worst"), CancellationToken.None)).Text;
        var mould = (await resolver.ResolveAsync(new ReportRef("machines", "week", "mould-RT-02"), CancellationToken.None)).Text;

        Assert.Contains("Zeitraum-Angabe für die Tools: \"aktuelle Schicht\"", process, StringComparison.Ordinal);
        Assert.Contains("Von Do 15.10. 06:00 bis Do 15.10. 14:00", process, StringComparison.Ordinal);
        Assert.Contains("Taktzeit gegen Soll: Toleranzband", process, StringComparison.Ordinal);
        Assert.Matches(@"\d+,\d % der Werte außerhalb", process);

        Assert.Contains("Zeitraum-Angabe für die Tools: \"diese Woche\"", shifts, StringComparison.Ordinal);
        Assert.Contains("Schichtvergleich der Halle A", shifts, StringComparison.Ordinal);
        Assert.Contains("Nachtschicht: OEE", shifts, StringComparison.Ordinal);
        Assert.Contains("Anlaufverlust nach Wechseln", shifts, StringComparison.Ordinal);

        Assert.Contains("Form ", mould, StringComparison.Ordinal);
        Assert.Contains("der Standzeit", mould, StringComparison.Ordinal);
        Assert.InRange(shifts.Length + process.Length + mould.Length, 1, 6000);
    }

    [Fact]
    public async Task Form_wear_and_overdue_maintenance_match_get_maintenance_due()
    {
        var report = await ReportAsync(McpRoles.HallLead, "week");
        var maintenance = await CallAsync(McpRoles.HallLead, "get_maintenance_due", new { scope = "Halle A", horizont = "Schicht" });

        var form = maintenance.At("wartungen").Item("station", "RT-02").At("form");
        Assert.Equal(form.Number("verschleiss_prozent"), Station(report, "RT-02").Number("maintenance.mould.wearPercent"));
        Assert.Equal("OF-01", maintenance.At("wartungen").Items().First(w => w.GetProperty("ueberfaellig").GetBoolean()).Text("station"));
        Assert.Contains(Station(report, "OF-01").At("maintenance.items").Items(), i => i.GetProperty("overdue").GetBoolean());
    }

    [Fact]
    public async Task Night_reaction_times_come_from_the_notes_and_four_weeks_carry_costs()
    {
        var report = await ReportAsync(McpRoles.ProductionLead, "fourWeeks");
        var shifts = report.At("shiftComparison.shifts");

        Assert.Equal("Nacht", shifts.Items().Last().Text("shift"));
        Assert.InRange(shifts.Items().Last().Number("reactionMinutes"), 10, 20);
        Assert.False(shifts.Items().First().Has("reactionMinutes"));
        Assert.True(report.Number("costs.totalEuro") > 0);
        Assert.True(report.At("costs").Has("previousTotalEuro"));
        Assert.Contains(report.At("notes").Items(), n => n.GetString()!.Contains("KW 41", StringComparison.Ordinal));
        Assert.Equal("KW 39 bis KW 42", report.Text("rangeLabel").Split(" · ")[1]);
    }
}
