using System.Text.Json;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Mcp;

/// <summary>
/// Referenzantworten der Spezifikation (Abschnitt 10, Datei <c>answers.json</c>) über den echten Endpunkt <c>/mcp</c> gegen die
/// aufgenommene Historie zur Demo-Uhr (Do 15.10.2026 09:42): Für jede Frage werden die genannten Tools mit den genannten Parametern
/// aufgerufen und die Zahlen der Kernaussage im Tool-Ergebnis geprüft. Abweichungen stehen in <c>docs/calibration.md</c> (Abschnitt MCP).
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class McpReferenceAnswerTests(InfrastructureFixture infrastructure)
{
    private const string ShiftLead = McpRoles.ShiftLead;
    private const string HallLead = McpRoles.HallLead;
    private const string ProductionLead = McpRoles.ProductionLead;

    private static readonly string[] s_pressPair = ["RT-01", "RT-02"];
    private static readonly string[] s_ovens = ["OF-01", "OF-02"];

    private async Task<JsonElement> CallAsync(string role, string tool, object? arguments = null) =>
        await (await McpHost.GetAsync(infrastructure)).CallAsync(role, tool, arguments);

    // ---- Schichtleiter ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Sl1_shift_handover_night_92_percent_with_RT03_gap_mixture_RT01_and_form_RT02()
    {
        var summary = await CallAsync(ShiftLead, "get_shift_summary", new { scope = "Halle A", schicht = "vorherige" });
        var alarms = await CallAsync(ShiftLead, "get_active_alarms", new { scope = "Halle A", minSeverity = "mittel" });
        var orders = await CallAsync(ShiftLead, "get_order_progress", new { scope = "Halle A" });
        var material = await CallAsync(ShiftLead, "get_material_reach", new { scope = "Halle A" });
        var maintenance = await CallAsync(ShiftLead, "get_maintenance_due", new { scope = "Halle A", horizont = "Schicht" });

        var night = summary.At("schichten.0");
        Assert.Equal("Nacht Mi 14.10.", night.Text("schicht"));
        Assert.Equal(92, night.Number("planerfuellung.prozent"));
        var stations = night.At("stationen");
        foreach (var (id, percent) in new[] { ("RT-01", 102), ("RT-02", 98), ("RT-03", 71), ("RT-04", 97), ("VP-01", 99), ("VP-02", 88) })
        {
            Assert.Equal(percent, stations.Item("station", id).Number("ist_soll_prozent"));
        }

        Assert.Equal("crit", stations.Item("station", "RT-03").Text("bewertung"));
        Assert.True(alarms.Number("anzahl") >= 1);
        Assert.Equal(76, orders.At("auftraege").Item("auftrag", "4711").Number("fortschritt_prozent"));
        Assert.Equal("RT-01", material.Text("zuerst.station"));
        Assert.Equal("10:20", material.Text("zuerst.verarbeitbar_bis"));
        var form = maintenance.At("wartungen").Item("station", "RT-02").At("form");
        Assert.Equal(94, form.Number("verschleiss_prozent"));
        Assert.Equal(1, maintenance.Number("ueberfaellig"));
        Assert.Equal("OF-01", maintenance.At("wartungen").Items().First(w => w.GetProperty("ueberfaellig").GetBoolean()).Text("station"));
    }

    [Fact]
    public async Task Sl2_RT04_lags_by_cycle_time_not_downtime_drift_since_0830_loss_about_400_pieces()
    {
        var status = await CallAsync(ShiftLead, "get_machine_status", new { machineId = "RT-04" });
        var trend = await CallAsync(ShiftLead, "get_process_trend", new { machineId = "RT-04", variable = "Presskraftaufbau", zeitraum = "06:00–jetzt" });
        var warnings = await CallAsync(ShiftLead, "get_condition_warnings", new { scope = "RT-04" });
        var maintenance = await CallAsync(ShiftLead, "get_maintenance_due", new { scope = "RT-04", baugruppe = "Hydraulik" });

        var values = status.At("prozesswerte");
        var cycle = values.Items().First(v => v.Text("name").StartsWith("Taktzeit", StringComparison.Ordinal));
        Assert.Equal(4.6, Math.Round(cycle.Number("wert"), 1));
        Assert.Equal(4.2, cycle.Number("soll"));
        var buildUp = values.Items().First(v => v.Text("name").StartsWith("Presskraftaufbau", StringComparison.Ordinal));
        Assert.Equal(1.1, Math.Round(buildUp.Number("wert"), 1));
        Assert.Equal(0.8, buildUp.Number("soll"));
        Assert.Equal(0, status.Number("stoerungen_schicht.stillstand_min"));

        var series = trend.At("verlaeufe.0");
        Assert.Equal([0.80, 0.80], series.At("verlauf").Items().Take(2).Select(b => b.Number("mittelwert")));
        Assert.True(series.Number("verlauf.2.mittelwert") > 0.80 && series.Number("verlauf.3.mittelwert") > series.Number("verlauf.2.mittelwert"));
        Assert.Equal(1.1, Math.Round(series.Number("aktuell"), 1));
        Assert.Equal("08:30", series.Text("drift.beginn_ca"));
        Assert.Equal("steigend", series.Text("drift.richtung"));
        Assert.InRange(series.Number("verlust_bis_schichtende.stueck"), 350, 450);

        Assert.Contains(warnings.At("auffaelligkeiten").Items(), w => w.Text("art") == "Zustandswarnung" && w.Text("text").Contains("Presskraftaufbau", StringComparison.Ordinal));
        Assert.Contains(maintenance.At("empfehlungen").Items(), r => r.Text("wartung").Contains("KW 45", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sl3_material_reach_RT01_first_with_38_minutes_by_processability_level_would_last_70()
    {
        var material = await CallAsync(ShiftLead, "get_material_reach", new { scope = "Halle A" });
        var order = await CallAsync(ShiftLead, "get_order_progress", new { machineId = "RT-01" });

        var first = material.At("zuerst");
        Assert.Equal("RT-01", first.Text("station"));
        Assert.Equal(38, first.Number("reichweite_min"));
        Assert.Equal("Verarbeitbarkeit der Mischung", first.Text("begrenzt_durch"));
        Assert.InRange(first.Number("reichweite_fuellstand_min"), 68, 72);
        var shortest = material.At("kuerzeste_je_station");
        Assert.Equal(55, shortest.Item("station", "RT-02").Number("reichweite_min"));
        Assert.Equal("Gewebe", shortest.Item("station", "RT-02").Text("material"));
        Assert.InRange(shortest.Item("station", "VP-01").Number("reichweite_min"), 95, 105);
        Assert.Equal("RT-01", shortest.Items().First().Text("station"));
        Assert.Equal("M-2410-17", order.At("chargen").Item("station", "RT-01").Text("charge"));
    }

    [Fact]
    public async Task Sl4_shift_target_presses_project_to_90_percent_gap_at_RT03_and_RT04()
    {
        var overview = await CallAsync(ShiftLead, "get_hall_overview", new { hallId = "A" });
        var summary = await CallAsync(ShiftLead, "get_shift_summary", new { scope = "Halle A", schicht = "aktuell" });
        var orders = await CallAsync(ShiftLead, "get_order_progress", new { scope = "Halle A" });

        var projection = summary.At("schichten.0.hochrechnung_schichtende");
        Assert.Equal(11_150, projection.Number("ist_bis_jetzt"));
        Assert.Equal(12_400, Math.Round(projection.Number("soll_bis_jetzt") / 100) * 100);
        Assert.Equal(24_100, Math.Round(projection.Number("ist_prognose") / 100) * 100);
        Assert.Equal(26_800, Math.Round(projection.Number("plan_schichtende") / 100) * 100);
        Assert.Equal(90, projection.Number("prozent"));
        var stations = projection.At("je_station");
        Assert.Equal(101, stations.Item("station", "RT-01").Number("prozent"));
        Assert.Equal(97, stations.Item("station", "RT-02").Number("prozent"));
        Assert.Equal(72, stations.Item("station", "RT-03").Number("prozent"));
        Assert.Equal(89, stations.Item("station", "RT-04").Number("prozent"));
        Assert.InRange(stations.Item("station", "RT-04").Number("taktverlust_bis_schichtende.stueck"), 350, 450);
        Assert.Equal(1, overview.Number("zusammenfassung.problem"));
        Assert.Equal(76, orders.At("auftraege").Item("auftrag", "4711").Number("fortschritt_prozent"));
        Assert.Equal("warn", orders.At("auftraege").Item("auftrag", "4711").Text("bewertung"));
    }

    [Fact]
    public async Task Sl5_VP02_has_no_problem_of_its_own_it_waits_for_the_180_charge_from_OF02()
    {
        var status = await CallAsync(ShiftLead, "get_machine_status", new { machineId = "VP-02" });
        var ovens = await CallAsync(ShiftLead, "get_hall_overview", new { hallId = "A", stationType = "Ofen" });
        var flow = await CallAsync(ShiftLead, "get_order_progress", new { machineId = "VP-02", stufe = "Ofen → Verpackung" });

        Assert.Equal("Wartet auf Vorstufe", status.Text("station.zustand"));
        Assert.Equal(64, status.Number("station.ist_soll_prozent"));
        Assert.Equal("Do 15.10. 09:05", status.Text("station.zustand_seit"));
        Assert.Equal("78 %", ovens.At("stationen").Item("id", "OF-01").Text("kennzahl.wert"));
        Assert.Equal("96 %", ovens.At("stationen").Item("id", "OF-02").Text("kennzahl.wert"));
        Assert.Equal(["RT-04", "OF-02", "VP-02"], ovens.At("ursachenketten.0.kette").Items().Select(k => k.GetString()));
        Assert.Equal("alle Ø 180", flow.At("materialfluss.oefen").Item("id", "OF-02").Text("zuordnung"));
        Assert.Equal("alle Ø 125", flow.At("materialfluss.oefen").Item("id", "OF-01").Text("zuordnung"));
    }

    [Fact]
    public async Task Sl6_hall_situation_one_problem_five_warnings_chain_and_three_tasks_one_urgent()
    {
        var overview = await CallAsync(ShiftLead, "get_hall_overview", new { hallId = "A" });
        var alarms = await CallAsync(ShiftLead, "get_active_alarms", new { scope = "Halle A", minSeverity = "mittel" });
        var items = await CallAsync(ShiftLead, "get_action_items", new { role = "Schichtleiter", hallId = "A" });

        Assert.Equal(8, overview.Number("zusammenfassung.stationen"));
        Assert.Equal(1, overview.Number("zusammenfassung.problem"));
        Assert.Equal(5, overview.Number("zusammenfassung.warnung"));
        Assert.Equal(2, overview.Number("zusammenfassung.laeuft"));
        Assert.Equal("Problem", overview.At("stationen").Item("id", "RT-03").Text("ampel"));
        Assert.Equal(["RT-04", "OF-02", "VP-02"], overview.At("ursachenketten.0.kette").Items().Select(k => k.GetString()));
        Assert.Equal(3, items.Number("anzahl"));
        Assert.Equal(1, items.Number("dringend"));
        Assert.Contains(items.At("eintraege").Items(), i => i.Text("titel").StartsWith("Rückstand RT-03", StringComparison.Ordinal));
        Assert.True(alarms.Number("anzahl") >= 1);
    }

    [Fact]
    public async Task Sl7_disruptions_this_shift_four_events_68_minutes_one_open_warning()
    {
        var alarms = await CallAsync(ShiftLead, "get_active_alarms", new { scope = "Halle A", zeitraum = "seit 06:00", inklusiveQuittiert = true });
        var pareto = await CallAsync(ShiftLead, "get_downtime_pareto", new { scope = "Halle A", zeitraum = "Schicht", gruppierung = "Station" });

        var disruptions = alarms.At("stoerungen_und_alarme");
        Assert.Equal(4, disruptions.Number("anzahl"));
        Assert.Equal(68, disruptions.Number("stillstand_min"));
        Assert.Equal(1, disruptions.Number("offene_warnungen"));
        var open = disruptions.At("eintraege").Items().Single(e => e.GetProperty("nur_alarm").GetBoolean());
        Assert.Equal("RT-04", open.Text("station"));
        Assert.Equal(65, disruptions.At("eintraege").Item("station", "RT-03").Number("stillstand_min"));
        var groups = pareto.At("gruppen");
        Assert.Equal(65, groups.Item("gruppe", "RT-03").Number("stillstand_min"));
        Assert.Equal(2, groups.Item("gruppe", "RT-02").Number("stillstand_min"));
        Assert.Equal(1, groups.Item("gruppe", "VP-01").Number("stillstand_min"));
    }

    // ---- Hallenleiterin --------------------------------------------------------------------------------------

    [Fact]
    public async Task Hl6_hall_oee_today_81_presses_79_ovens_87_packaging_84_scrap_1_9()
    {
        var oee = await CallAsync(HallLead, "get_oee", new { scope = "Halle A", zeitraum = "heute", gruppierung = "Bereich" });
        var scrap = await CallAsync(HallLead, "compare", new { dimension = "Bereich", kpi = "Ausschussquote", zeitraum = "heute" });

        Assert.Equal(81, oee.Number("gesamt.oee_prozent"));
        Assert.Equal(79, oee.At("gruppen").Item("gruppe", "Pressen").Number("oee_prozent"));
        Assert.Equal(84, oee.At("gruppen").Item("gruppe", "Verpackung").Number("oee_prozent"));
        Assert.Equal(87, oee.At("gruppen").Item("gruppe", "Öfen").Number("belegung_prozent"));
        Assert.Equal(1.9, scrap.At("zeilen").Item("name", "Pressen").Number("wert"));
    }

    [Fact]
    public async Task Hl7_maintenance_plan_five_in_48_hours_OF01_overdue_bundling_cuts_95_to_40_minutes()
    {
        var due = await CallAsync(HallLead, "get_maintenance_due", new { scope = "Halle A", horizont = "48 h" });
        var conflicts = await CallAsync(HallLead, "check_maintenance_conflicts", new { hallId = "A", zeitraum = "heute–morgen" });
        var warnings = await CallAsync(HallLead, "get_condition_warnings", new { scope = "Halle A" });

        Assert.Equal(5, due.Number("anzahl"));
        Assert.Equal(1, due.Number("ueberfaellig"));
        var door = due.At("wartungen").Item("station", "OF-01");
        Assert.Equal(2, door.Number("ueberfaellig_tage"));
        Assert.True(door.GetProperty("sicherheitsrelevant").GetBoolean());
        Assert.Equal(95, due.Number("stillstand.einzeln_min"));
        Assert.Equal(40, due.Number("stillstand.gebuendelt_min"));
        Assert.Equal(95, conflicts.Number("stillstand.einzeln_min"));
        Assert.Equal(40, conflicts.Number("stillstand.gebuendelt_min"));
        Assert.Equal(["RT-02", "RT-01"], conflicts.At("buendelungsvorschlaege").Items().Select(p => p.Text("station")));
        Assert.Contains(warnings.At("auffaelligkeiten").Items(), w => w.Text("station") == "RT-04" && w.Text("art") == "Zustandswarnung");
    }

    [Fact]
    public async Task Hl1_time_losses_week_42_fabric_feed_6_4_hours_9_of_14_at_RT03()
    {
        var pareto = await CallAsync(HallLead, "get_downtime_pareto", new { scope = "Halle A", zeitraum = "KW 42" });
        var compare = await CallAsync(HallLead, "compare", new { dimension = "Station", kpi = "Stillstand Gewebezuführung", zeitraum = "KW 42" });

        var entries = pareto.At("eintraege");
        Assert.Equal("Störung Gewebezuführung", entries.At("0").Text("reason"));
        Assert.Equal(6.4, Math.Round(entries.At("0").Number("dauer_h"), 1));
        Assert.Equal(14, entries.At("0").Number("anzahl"));
        Assert.Equal(4.1, Math.Round(entries.Item("reason", "Warten auf Mischung").Number("dauer_h"), 1));
        Assert.Equal(3.5, Math.Round(entries.Item("reason", "Warten auf Ofen").Number("dauer_h"), 1));
        Assert.Equal(3.2, Math.Round(entries.Item("reason", "Rüsten über Soll").Number("dauer_h"), 1));
        Assert.Equal(2.0, Math.Round(entries.Item("reason", "Formwechsel ungeplant").Number("dauer_h"), 1));
        Assert.Equal(1.1, Math.Round(entries.Item("reason", "Etiketten leer").Number("dauer_h"), 1));
        var rows = compare.At("zeilen");
        Assert.Equal(9, rows.Item("name", "RT-03").Number("details.anzahl"));
        Assert.Equal(14, rows.Items().Sum(r => r.Number("details.anzahl")));
    }

    [Fact]
    public async Task Hl2_quality_RT01_and_RT02_drift_since_2200_common_batch_M_2410_17_scales_unremarkable()
    {
        var scrap = await CallAsync(HallLead, "compare", new { dimension = "Rundtisch", kpi = "Ausschussquote", zeitraum = "24 h" });
        var trend = await CallAsync(HallLead, "get_process_trend", new { machineId = s_pressPair, variable = "Füllgewicht", zeitraum = "24 h" });
        var orders = await CallAsync(HallLead, "get_order_progress", new { machineId = s_pressPair });
        var scales = await CallAsync(HallLead, "get_maintenance_due", new { scope = s_pressPair, baugruppe = "Waage", historie = true });
        var climate = await CallAsync(HallLead, "get_process_trend", new { machineId = "Klima", variable = "Temperatur", zeitraum = "24 h" });
        var rt03 = await CallAsync(HallLead, "get_machine_status", new { machineId = "RT-03" });

        var rows = scrap.At("zeilen");
        Assert.Equal(2.8, rows.Item("name", "RT-01").Number("wert"));
        Assert.Equal(3.1, rows.Item("name", "RT-02").Number("wert"));
        Assert.Equal(1.2, rows.Item("name", "RT-03").Number("wert"));
        Assert.Equal(0.9, rows.Item("name", "RT-04").Number("wert"));
        foreach (var station in new[] { "RT-01", "RT-02" })
        {
            var series = trend.At("verlaeufe").Item("station", station);
            Assert.True(series.GetProperty("drift").GetProperty("erkannt").GetBoolean());
            Assert.Equal("steigend", series.Text("drift.richtung"));
            Assert.InRange(string.CompareOrdinal(series.Text("drift.beginn_ca"), "21:30"), 0, 1000);
            Assert.InRange(string.CompareOrdinal(series.Text("drift.beginn_ca"), "22:30"), -1000, 0);
            Assert.InRange(series.Number("drift.letzter_wert") - series.Number("drift.ausgangswert"), 1.2, 1.8);
            Assert.Equal(112, series.Number("soll"));
            Assert.Equal(1.5, series.Number("toleranz"));
        }

        Assert.Equal("M-2410-17", orders.At("chargen").Item("station", "RT-01").Text("charge"));
        Assert.Equal("M-2410-17", orders.At("chargen").Item("station", "RT-02").Text("charge"));
        Assert.Equal(2, scales.At("historie").Items().Count(h => h.Text("result") == "unauffällig" && h.Text("title") == "Waage kalibriert"));
        var temperature = climate.At("verlaeufe.0");
        Assert.InRange(temperature.Number("min"), 18, 26);
        Assert.InRange(temperature.Number("max"), 18, 26);
        var weight = rt03.At("prozesswerte").Items().First(v => v.Text("name").StartsWith("Füllgewicht", StringComparison.Ordinal));
        Assert.InRange(weight.Number("wert"), 111.8, 112.4);
        Assert.Equal("ok", weight.Text("bewertung"));
    }

    [Fact]
    public async Task Hl3_mould_maintenance_at_RT02_bundles_with_the_setup_at_1130_without_conflict()
    {
        var conflicts = await CallAsync(HallLead, "check_maintenance_conflicts", new { hallId = "A", zeitraum = "heute" });
        var due = await CallAsync(HallLead, "get_maintenance_due", new { scope = "RT-02" });
        var order = await CallAsync(HallLead, "get_order_progress", new { machineId = "RT-02" });

        Assert.Empty(conflicts.At("konflikte").Items());
        var proposal = conflicts.At("buendelungsvorschlaege").Item("station", "RT-02");
        Assert.True(proposal.GetProperty("passt_in_ruestfenster").GetBoolean());
        Assert.Equal("4724", proposal.Text("ruestfenster.folgeauftrag"));
        Assert.InRange(proposal.Number("verschleiss_zum_ruesten_prozent"), 97, 98);
        var perStation = conflicts.At("stillstand.je_station").Item("station", "RT-02");
        Assert.Equal(25, perStation.Number("einzeln_min"));
        Assert.Equal(0, perStation.Number("gebuendelt_min"));
        var form = due.At("wartungen.0.form");
        Assert.Equal(94, form.Number("verschleiss_prozent"));
        Assert.Equal("Do 15.10. 11:30", due.Text("wartungen.0.faellig"));
        var finished = order.At("auftraege").Item("auftrag", "4698");
        Assert.StartsWith("Do 15.10. 11:", finished.Text("voraussichtlich_fertig"), StringComparison.Ordinal);
        Assert.Equal("4724", order.At("chargen.0.folgeauftrag").GetString());
    }

    [Fact]
    public async Task Hl4_ovens_unevenly_occupied_OF02_96_OF01_78_assignment_rule_is_not_a_technical_limit()
    {
        var overview = await CallAsync(HallLead, "get_hall_overview", new { hallId = "A", stationType = "Ofen" });
        var trend = await CallAsync(HallLead, "get_process_trend", new { machineId = s_ovens, variable = "Temperaturprofil", zeitraum = "heute" });
        var flow = await CallAsync(HallLead, "get_order_progress", new { scope = "Halle A", stufe = "Ofen → Verpackung" });

        Assert.Equal("78 %", overview.At("stationen").Item("id", "OF-01").Text("kennzahl.wert"));
        Assert.Equal("96 %", overview.At("stationen").Item("id", "OF-02").Text("kennzahl.wert"));
        Assert.Equal(42, flow.Number("materialfluss.puffer.platten"));
        Assert.Equal(45, flow.Number("materialfluss.puffer.mittlere_wartezeit_min"));
        Assert.Equal("alle Ø 180", flow.At("materialfluss.oefen").Item("id", "OF-02").Text("zuordnung"));
        var of01 = trend.At("verlaeufe").Item("station", "OF-01");
        var of02 = trend.At("verlaeufe").Item("station", "OF-02");
        Assert.False(of01.GetProperty("drift").GetProperty("erkannt").GetBoolean());
        Assert.False(of02.GetProperty("drift").GetProperty("erkannt").GetBoolean());
        Assert.InRange(Math.Abs(of01.Number("verlauf.0.mittelwert") - of02.Number("verlauf.0.mittelwert")), 0, 1);
    }

    [Fact]
    public async Task Hl5_daily_meeting_yesterday_oee_82_80_72_night_loses_2_3_hours_to_mixture()
    {
        var summary = await CallAsync(HallLead, "get_shift_summary", new { scope = "Halle A", schicht = "gestern", alle = true });
        var pareto = await CallAsync(HallLead, "get_downtime_pareto", new { scope = "Halle A", zeitraum = "gestern" });
        var conflicts = await CallAsync(HallLead, "check_maintenance_conflicts", new { hallId = "A", zeitraum = "heute" });
        var material = await CallAsync(HallLead, "get_material_reach", new { scope = "Halle A" });

        Assert.Equal([82, 80, 72], summary.At("schichten").Items().Select(s => (int)s.Number("oee.oee_prozent")));
        var availability = summary.At("schichten").Items().Select(s => s.Number("oee.verfuegbarkeit_prozent")).ToList();
        Assert.True(availability[2] < availability[0] && availability[2] < availability[1]);
        var mixture = pareto.At("eintraege").Item("reason", "Warten auf Mischung");
        Assert.Equal(2.3, Math.Round(mixture.Number("dauer_h"), 1));
        Assert.Equal(65, conflicts.Number("stillstand.einzeln_min"));
        Assert.Equal("RT-01", material.Text("zuerst.station"));
    }

    // ---- Produktionsleiter -----------------------------------------------------------------------------------

    [Fact]
    public async Task Pl1_weekly_report_week_42_plan_96_oee_79_versus_78_two_orders_at_risk()
    {
        var oee = await CallAsync(ProductionLead, "get_oee", new { scope = "Werk", zeitraum = "KW 42", gruppierung = "Halle" });
        var weeks = await CallAsync(ProductionLead, "compare", new { dimension = "Woche", kpi = "OEE", zeitraum = "KW 41–42" });
        var orders = await CallAsync(ProductionLead, "get_order_progress", new { scope = "Werk", filter = "gefährdet" });
        var reliability = await CallAsync(ProductionLead, "get_reliability_kpis", new { scope = "Werk", zeitraum = "KW 42" });

        Assert.Equal(79, oee.Number("gesamt.oee_prozent"));
        Assert.Equal(96, oee.Number("gesamt.ist_soll_prozent"));
        Assert.Equal(1.41, Math.Round(oee.Number("gesamt.ist") / 1e6, 2));
        Assert.Equal(1.47, Math.Round(oee.Number("gesamt.soll") / 1e6, 2));
        Assert.Equal(1.4, oee.Number("gesamt.ausschuss_prozent"));
        Assert.Equal(77, oee.At("gruppen").Item("gruppe", "Halle A").Number("oee_prozent"));
        Assert.Equal(81, oee.At("gruppen").Item("gruppe", "Halle B").Number("oee_prozent"));
        Assert.Equal(80, oee.At("gruppen").Item("gruppe", "Halle C").Number("oee_prozent"));
        Assert.Equal(78, weeks.At("zeilen").Item("name", "KW 41").Number("wert"));
        Assert.Equal(79, weeks.At("zeilen").Item("name", "KW 42").Number("wert"));
        Assert.Equal("crit", orders.At("auftraege").Item("auftrag", "4730").Text("bewertung"));
        Assert.Equal("crit", orders.At("auftraege").Item("auftrag", "4733").Text("bewertung"));
        Assert.Equal(2, orders.Number("zusammenfassung.gefaehrdet"));
        Assert.True(reliability.Number("gesamt.stoerungen") > 0);
    }

    [Fact]
    public async Task Pl2_worst_shift_is_night_because_of_availability_mixture_waits_5_8_hours_plantwide()
    {
        var oee = await CallAsync(ProductionLead, "get_oee", new { scope = "Werk", zeitraum = "KW 42", gruppierung = "Schicht" });
        var night = await CallAsync(ProductionLead, "get_downtime_pareto", new { scope = "Werk", zeitraum = "KW 42", filter = "Nachtschicht" });
        var week = await CallAsync(ProductionLead, "get_downtime_pareto", new { scope = "Werk", zeitraum = "KW 42" });

        var groups = oee.At("gruppen");
        var nightOee = groups.Item("gruppe", "Nachtschicht");
        Assert.True(nightOee.Number("oee_prozent") < groups.Item("gruppe", "Frühschicht").Number("oee_prozent"));
        Assert.True(nightOee.Number("oee_prozent") < groups.Item("gruppe", "Spätschicht").Number("oee_prozent"));
        Assert.True(nightOee.Number("verfuegbarkeit_prozent") < groups.Item("gruppe", "Frühschicht").Number("verfuegbarkeit_prozent"));
        Assert.True(nightOee.Number("verfuegbarkeit_prozent") < groups.Item("gruppe", "Spätschicht").Number("verfuegbarkeit_prozent"));
        Assert.InRange(Math.Abs(nightOee.Number("leistung_prozent") - groups.Item("gruppe", "Frühschicht").Number("leistung_prozent")), 0, 4);
        Assert.Contains(oee.At("hinweise").Items(), h => h.GetString()!.Contains("Tageswerte", StringComparison.Ordinal));
        Assert.Equal("Warten auf Mischung", night.At("eintraege.0").Text("reason"));
        Assert.Equal(4.1, Math.Round(night.At("eintraege.0").Number("dauer_h"), 1));
        Assert.Equal(5.8, Math.Round(week.At("eintraege").Item("reason", "Warten auf Mischung").Number("dauer_h"), 1));
    }

    [Fact]
    public async Task Pl3_RT03_overhaul_mtbf_31_versus_78_hours_mttr_42_versus_38_minutes_78_percent_fabric_feed()
    {
        var hall = await CallAsync(ProductionLead, "get_reliability_kpis", new { scope = "Halle A", zeitraum = "90 Tage" });
        var rt03 = await CallAsync(ProductionLead, "get_reliability_kpis", new { scope = "RT-03", zeitraum = "90 Tage" });
        var pareto = await CallAsync(ProductionLead, "get_downtime_pareto", new { scope = "RT-03", zeitraum = "90 Tage", gruppierung = "Baugruppe" });
        var maintenance = await CallAsync(ProductionLead, "get_maintenance_due", new { scope = "RT-03" });

        Assert.Equal(31, Math.Round(rt03.Number("gesamt.mtbf_h")));
        Assert.Equal(42, Math.Round(rt03.Number("gesamt.mttr_min")));
        Assert.Equal(38, Math.Round(hall.Number("gesamt.mttr_min")));
        Assert.Equal(78, rt03.Number("schnitt_andere_rundtische.mtbf_h"));
        Assert.Equal("Störung Gewebezuführung", rt03.At("stoerungen_nach_grund.0").Text("reason"));
        Assert.Equal(78, rt03.Number("stoerungen_nach_grund.0.anteil_an_stoerungen_prozent"));
        Assert.Equal("Störung Gewebezuführung", pareto.At("gruppen").Items().Select(g => g.Text("gruppe")).First(g => g.StartsWith("Störung", StringComparison.Ordinal)));
        Assert.Equal(0, maintenance.Number("ueberfaellig"));
    }

    [Fact]
    public async Task Pl4_unplanned_share_falls_from_48_to_33_percent_one_safety_inspection_overdue()
    {
        var reliability = await CallAsync(ProductionLead, "get_reliability_kpis", new { scope = "Werk", zeitraum = "Jul–Okt", gruppierung = "Monat" });
        var overdue = await CallAsync(ProductionLead, "get_maintenance_due", new { scope = "Werk", filter = "überfällig" });

        var months = reliability.At("gruppen").Items().Select(g => (int)g.Number("anteil_ungeplant_prozent")).ToList();
        Assert.Equal([48, 45, 39, 33], months);
        Assert.Equal(1, overdue.Number("anzahl"));
        Assert.Equal("OF-01", overdue.Text("wartungen.0.station"));
        Assert.Equal(2, overdue.Number("wartungen.0.ueberfaellig_tage"));
        Assert.True(overdue.At("wartungen.0.sicherheitsrelevant").GetBoolean());
    }

    [Fact]
    public async Task Pl5_orders_at_risk_4730_and_4733_critical_4711_can_catch_up()
    {
        var orders = await CallAsync(ProductionLead, "get_order_progress", new { scope = "Werk", filter = "gefährdet" });
        var hallB = await CallAsync(ProductionLead, "get_oee", new { scope = "Halle B", zeitraum = "KW 42" });
        var compare = await CallAsync(ProductionLead, "compare", new { dimension = "Auftrag", kpi = "Fertigstellung vs. Termin" });

        var list = orders.At("auftraege");
        Assert.Equal(58, list.Item("auftrag", "4730").Number("fortschritt_prozent"));
        Assert.Equal(41, list.Item("auftrag", "4733").Number("fortschritt_prozent"));
        Assert.Equal("Di 20.10. 14:00", list.Item("auftrag", "4730").Text("termin"));
        Assert.Equal("crit", list.Item("auftrag", "4730").Text("bewertung"));
        Assert.Equal("crit", list.Item("auftrag", "4733").Text("bewertung"));
        Assert.Equal(76, list.Item("auftrag", "4711").Number("fortschritt_prozent"));
        Assert.Equal("warn", list.Item("auftrag", "4711").Text("bewertung"));
        Assert.Contains("aufholbar", list.Item("auftrag", "4711").Text("begruendung"), StringComparison.Ordinal);
        Assert.Contains("nur für 1 von 2", list.Item("auftrag", "4730").Text("begruendung"), StringComparison.Ordinal);
        Assert.Equal(81, hallB.Number("gesamt.oee_prozent"));
        Assert.Equal(58, compare.At("zeilen").Item("name", "Auftrag 4730 · Ø 230 A36").Number("wert"));
    }

    [Fact]
    public async Task Pl6_costs_of_the_week_about_41000_euro_RT03_9800_previous_week_46000()
    {
        var pareto = await CallAsync(ProductionLead, "get_downtime_pareto", new { scope = "Werk", zeitraum = "KW 42", bewertung = "Kosten" });
        var halls = await CallAsync(ProductionLead, "compare", new { dimension = "Halle", kpi = "Ausschusskosten", zeitraum = "KW 42" });
        var reliability = await CallAsync(ProductionLead, "get_reliability_kpis", new { scope = "Werk", zeitraum = "KW 42" });

        var costs = pareto.At("kosten");
        Assert.Equal(41.0, Math.Round(costs.Number("gesamt_euro") / 1000, 1));
        Assert.Equal(14.5, Math.Round(costs.Number("ausschuss_euro") / 1000, 1));
        Assert.Equal(26.5, Math.Round(costs.Number("stillstand_euro") / 1000, 1));
        Assert.Equal("RT-03", costs.Text("groesster_posten.station"));
        Assert.Equal("Störung Gewebezuführung", costs.Text("groesster_posten.grund"));
        Assert.Equal(9_800, Math.Round(costs.Number("groesster_posten.kosten_euro") / 100) * 100);
        Assert.Equal(46.0, Math.Round(pareto.Number("vorperiode.gesamt_euro") / 1000, 1));
        Assert.Equal(-11, pareto.Number("vorperiode.veraenderung_prozent"));
        var rows = halls.At("zeilen");
        Assert.Equal(19.2, Math.Round(rows.Item("name", "Halle A").Number("details.gesamt_euro") / 1000, 1));
        Assert.Equal(11.9, Math.Round(rows.Item("name", "Halle B").Number("details.gesamt_euro") / 1000, 1));
        Assert.Equal(9.9, Math.Round(rows.Item("name", "Halle C").Number("details.gesamt_euro") / 1000, 1));
        Assert.True(reliability.Number("gesamt.mtbf_h") > 0);
    }

    [Fact]
    public async Task Wk1_next_week_43_has_four_maintenances_and_recommends_pulling_the_RT04_hydraulics_forward()
    {
        var due = await CallAsync(HallLead, "get_maintenance_due", new { scope = "Halle A", von = "2026-10-19", bis = "2026-10-25", prognose = true });
        var warnings = await CallAsync(HallLead, "get_condition_warnings", new { scope = "Halle A" });
        var conflicts = await CallAsync(HallLead, "check_maintenance_conflicts", new { hallId = "A", zeitraum = "KW 43" });

        Assert.Equal(4, due.Number("anzahl"));
        var items = due.At("wartungen");
        Assert.Equal("Mo 19.10. (ganztägig)", items.Item("station", "RT-02").Text("faellig"));
        Assert.Equal(45, items.Item("station", "RT-02").Number("arbeitsdauer_min"));
        Assert.Equal(60, items.Item("station", "OF-02").Number("arbeitsdauer_min"));
        Assert.Equal(2, items.Items().Count(i => i.Text("status") == "geplant"));
        Assert.Equal(2, items.Items().Count(i => i.Text("status") == "Prognose"));
        var mould = items.Item("station", "RT-04");
        Assert.Equal("Mi 21.10.", mould.Text("form.prognose_grenze_tag"));
        Assert.Equal(4100, mould.Number("form.huebe_pro_tag"));
        Assert.Equal("Do 22.10. (ganztägig)", items.Item("station", "VP-02").Text("faellig"));

        var recommendation = due.At("empfehlungen.0");
        Assert.Equal("RT-04", recommendation.Text("station_id"));
        Assert.Contains("Hydraulik", recommendation.Text("wartung"), StringComparison.Ordinal);
        Assert.Contains("Mi 21.10. 13:00", recommendation.Text("empfehlung"), StringComparison.Ordinal);
        Assert.Contains(warnings.At("auffaelligkeiten").Items(), w => w.Text("station") == "RT-04" && w.Text("art") == "Zustandswarnung");

        var days = conflicts.At("stillstand.je_tag");
        Assert.Equal(45, days.Items().First(d => d.Text("tag").StartsWith("Mo 19.10.", StringComparison.Ordinal)).Number("einzeln_min"));
        Assert.Equal(60, days.Items().First(d => d.Text("tag").StartsWith("Di 20.10.", StringComparison.Ordinal)).Number("einzeln_min"));
        Assert.Equal(0, days.Items().First(d => d.Text("tag").StartsWith("Mi 21.10.", StringComparison.Ordinal)).Number("einzeln_min"));
        Assert.Equal(30, days.Items().First(d => d.Text("tag").StartsWith("Do 22.10.", StringComparison.Ordinal)).Number("einzeln_min"));
        Assert.Empty(conflicts.At("konflikte").Items());
    }

    // ---- Detailfragen aus den Erweiterungen ------------------------------------------------------------------

    [Fact]
    public async Task Overproduction_RT01_2050_pieces_and_RT02_3600_pieces_green_in_the_dashboard_with_buffer_58()
    {
        var result = await CallAsync(HallLead, "get_overproduction", new { scope = "Halle A" });

        Assert.Equal(2, result.Number("anzahl"));
        Assert.Equal(5_650, result.Number("summe_stueck"));
        var events = result.At("ereignisse");
        Assert.Equal(2_050, events.Item("station", "RT-01").Number("stueck"));
        Assert.Equal("Warnung", events.Item("station", "RT-01").Text("ampel_im_dashboard"));
        var rt02 = events.Item("station", "RT-02");
        Assert.Equal(3_600, rt02.Number("stueck"));
        Assert.Equal(118, rt02.Number("ist_soll_prozent"));
        Assert.Equal("Läuft", rt02.Text("ampel_im_dashboard"));
        Assert.Equal(58, rt02.Number("folgen.puffer_spitze_platten"));
        var waits = rt02.At("folgen.warten_auf_ofen");
        Assert.Equal(70, waits.Items().First(w => w.Text("station") == "RT-04" && w.Number("dauer_min") == 70).Number("dauer_min"));
        Assert.Contains(waits.Items(), w => w.Text("station") == "VP-02" && w.Number("dauer_min") == 45);
    }

    [Fact]
    public async Task Traceability_cartons_with_batch_M_2410_17_are_not_shipped_yet_and_passed_the_tests()
    {
        var batch = await CallAsync(HallLead, "trace_batch", new { charge = "M-2410-17" });

        var summary = batch.At("zusammenfassung");
        Assert.Equal(2, summary.Number("pressenlose"));
        Assert.Equal(7, summary.Number("ofenchargen"));
        Assert.Equal(17, summary.Number("paletten"));
        Assert.Equal(2_040, summary.Number("kartons"));
        Assert.Equal(0, summary.Number("kartons_ausgeliefert"));
        Assert.Equal(2_040, summary.Number("kartons_nicht_ausgeliefert"));
        Assert.True(summary.GetProperty("pruefungen_bestanden").GetBoolean());
        Assert.Equal(["RT-01", "RT-02"], batch.At("pressenlose").Items().Select(l => l.Text("station")));
        Assert.All(batch.At("paletten").Items(), p => Assert.Equal("VP-01", p.Text("verpackung")));

        var firstPallet = batch.Text("paletten.0.palette");
        var back = await CallAsync(HallLead, "trace_batch", new { charge = firstPallet });
        Assert.Equal("Palette", back.Text("ausgang.art"));
        Assert.Contains("M-2410-17", back.At("mischungschargen").Items().Select(b => b.GetString()));
    }

    [Fact]
    public async Task Christmas_shutdown_is_a_maintenance_window_with_oven_revisions_and_general_inspections()
    {
        var due = await CallAsync(HallLead, "get_maintenance_due", new { scope = "Halle A", von = "2026-12-24", bis = "2027-01-06", prognose = true });

        var items = due.At("wartungen");
        Assert.Equal(2, items.Items().Count(i => i.Text("art") == "Revision"));
        Assert.Equal(4, items.Items().Count(i => i.Text("art") == "Prüfung" && i.Text("titel").StartsWith("Generalinspektion", StringComparison.Ordinal)));
        Assert.Equal(["OF-01", "OF-02"], items.Items().Where(i => i.Text("art") == "Revision").Select(i => i.Text("station")).Order());
    }

    [Fact]
    public async Task Action_items_per_role_come_from_the_escalation_rules()
    {
        var shift = await CallAsync(ShiftLead, "get_action_items");
        var hall = await CallAsync(HallLead, "get_action_items");
        var production = await CallAsync(ProductionLead, "get_action_items");

        Assert.Equal([3, 4, 3], new[] { shift.Number("anzahl"), hall.Number("anzahl"), production.Number("anzahl") }.Select(n => (int)n));
        Assert.Contains(hall.At("eintraege").Items(), i => i.Text("titel") == "Qualitätsabweichung Charge M-2410-17");
        Assert.Contains(hall.At("eintraege").Items(), i => i.Text("titel") == "Sicherheitsprüfung OF-01 überfällig" && i.Text("stufe") == "crit");
        Assert.Contains(production.At("eintraege").Items(), i => i.Text("titel") == "Zwei Ø-230-Aufträge gefährdet");
        Assert.Contains(production.At("eintraege").Items(), i => i.Text("titel").StartsWith("Investition: Teilüberholung Gewebezuführung RT-03", StringComparison.Ordinal));
        Assert.Contains(shift.At("eintraege").Items(), i => i.Text("titel") == "Mischung A60 für RT-01 anfordern" && i.Text("frist") == "bis 09:50");
    }
}
