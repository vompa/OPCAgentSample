using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Kpis;
using OPCAgentSample.Server.Live;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Tools für Kennzahlen: Schichtbericht, OEE, Stillstand-Pareto, Vergleiche, Zuverlässigkeit und Überproduktion. Die Rechenlogik
/// liegt vollständig in der Kennzahlen-Bibliothek; hier werden Zeiträume aufgelöst, Blöcke gelesen und das Ergebnis mit Einheit,
/// Bewertung, Datenstand und Quelle ausgegeben. Summen werden addiert, Prozente nie gemittelt.
/// </summary>
[McpServerToolType]
public sealed class KpiTools(
    McpSession session,
    ScopeResolver scopes,
    TimeRangeResolver ranges,
    KpiAccess kpis,
    KpiBlockService blocks,
    PlantCostRates rates,
    PlantQueries queries,
    StationStateStore states,
    OrderService orders,
    ShiftCalendar calendar,
    IOptions<PlantOptions> plant,
    IOptions<McpOptions> options)
{
    private const double OeeGood = 0.80;
    private const double OeeWarn = 0.65;

    [McpServerTool(Name = "get_shift_summary", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Fasst eine oder mehrere Schichten zusammen: Planerfüllung der Pressen (Ist/Soll), Stück gut und Ausschuss (nach Grund), "
        + "OEE mit Verfügbarkeit, Leistung und Qualität, Stillstand nach Grund und je Station Ist, Soll und Bewertung. Für die laufende "
        + "Schicht kommt die Hochrechnung bis Schichtende (Ist × Schichtdauer / verstrichene Zeit) samt Lücke je Rundtisch und dem Verlust durch "
        + "verlängerte Taktzeit. Die Schicht ist 'aktuell', 'vorherige', 'heute', 'gestern' (alle Schichten), 'Früh', 'Spät', 'Nacht' oder ein "
        + "Schlüssel wie 2026-10-14N. Nächster Schritt: get_downtime_pareto für die Gründe des Stillstands, get_order_progress für die Aufträge.")]
    public async Task<CallToolResult> GetShiftSummary(
        [Description("Bereich: Halle A, Bereich oder Station. Standard: Halle A.")] string? scope = null,
        [Description("Schicht: aktuell, vorherige, heute, gestern, Früh, Spät, Nacht oder Schlüssel (2026-10-14N). Standard: aktuell.")] string? schicht = null,
        [Description("true = bei 'aktuell' alle Schichten des Tages.")] bool alle = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        if (!resolved.HasHallA) throw new McpException("Schichtberichte gibt es für Halle A (die Hallen B und C liefern nur Tageswerte).");

        var now = await session.NowAsync(cancellationToken);
        var shifts = ranges.ResolveShifts(schicht, now, alle);
        var costRates = await rates.GetAsync(cancellationToken);
        var presses = resolved.Stations.Where(s => s.Area == Area.Pressing).Select(s => s.Id).ToList();
        var oeeStations = resolved.Stations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id).ToList();
        var planStations = presses.Count > 0 ? presses : oeeStations;
        var snapshot = await states.GetAsync(cancellationToken);
        session.Sources.Add(snapshot.SourceLabel, snapshot.Snapshot.SourceUntilUtc);

        var result = new List<object>();
        foreach (var shift in shifts)
        {
            var block = await blocks.GetAsync(shift, cancellationToken);
            session.Sources.Add(block.SourceLabel, block.Block.SourceUntilUtc);
            var data = block.Block;
            var plan = PlanFulfillment.From(data.Figures(planStations));
            var figures = data.Figures(resolved.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id));
            var pareto = DowntimePareto.Build(data, resolved.HallAIds, costRates);
            var open = shift.EndUtc > now;
            var scrapByReason = presses.Count > 0
                ? (await queries.CycleAggregatesAsync(shift.StartUtc, open ? now : shift.EndUtc, presses))
                    .Values.SelectMany(a => a.ScrapByReason).GroupBy(p => p.Key).ToDictionary(g => ScrapWord(g.Key), g => g.Sum(p => p.Value))
                : [];
            var entry = new Dictionary<string, object?>
            {
                ["schicht"] = ranges.ShiftLabel(shift),
                ["schluessel"] = shift.Key,
                ["von"] = Fmt.Stamp(shift.StartUtc),
                ["bis"] = Fmt.Stamp(shift.EndUtc),
                ["abgeschlossen"] = !open,
                ["datenstand"] = Fmt.Stamp(data.SourceUntilUtc),
                ["planerfuellung"] = new
                {
                    Bereich = presses.Count > 0 ? "Pressen" : "gewählter Bereich",
                    Prozent = Fmt.Percent(plan.Ratio),
                    Ist = plan.Actual,
                    Soll = Fmt.Round(plan.Planned, 0),
                    Bewertung = RatePlan(plan.Ratio),
                },
                ["produktion"] = new
                {
                    Gut = figures.GoodPieces,
                    Ausschuss = figures.ScrapPieces,
                    AusschussProzent = figures.TotalPieces > 0 ? Fmt.Round(100.0 * figures.ScrapPieces / figures.TotalPieces, 1) : (double?)null,
                    AusschussNachGrund = scrapByReason,
                },
                ["oee"] = oeeStations.Count > 0 ? OeeBlock(data.Figures(oeeStations)) : null,
                ["stillstand"] = new
                {
                    SummeMin = Fmt.Minutes(TimeSpan.FromTicks(pareto.Sum(p => p.Duration.Ticks))),
                    NachGrund = pareto.Take(6).Select(p => new { Grund = p.Reason, DauerMin = Fmt.Minutes(p.Duration), p.Count }),
                },
                ["stationen"] = resolved.Stations.Where(s => s.Area is not Area.Buffer and not Area.Curing).Select(s => StationLine(s, data, now, shift)),
            };

            if (open && shift.StartUtc <= now) entry["hochrechnung_schichtende"] = await ProjectionAsync(shift, data, presses, snapshot.Snapshot, now, cancellationToken);
            result.Add(entry);
        }

        return await new Reply("get_shift_summary")
            .Add("bereich", resolved.Label)
            .Add("schichten", result)
            .BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_oee", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Berechnet die OEE (Verfügbarkeit × Leistung × Qualität) aus den Summen der Grundgrößen, nie aus gemittelten Prozenten. "
        + "Gruppierung: Station, Bereich (Pressen, Verpackung; Öfen als Belegung), Halle (A, B, C), Schicht (Früh, Spät, Nacht über den Zeitraum), "
        + "Tag oder gesamt. Halle-OEE = Pressen und Verpackung. Zeiträume: heute, gestern, diese Woche, Vorwoche, KW 42, 24 h, 90 Tage ... "
        + "Hallen B und C liegen nur als Tageswerte vor (kein Schichtvergleich). Bewertung: ab 80 % in Ordnung, ab 65 % Warnung. "
        + "Nächster Schritt: get_downtime_pareto, um die Verluste hinter einer niedrigen Verfügbarkeit zu finden.")]
    public async Task<CallToolResult> GetOee(
        [Description("Bereich: Halle A, Werk, Bereich oder Station. Standard: die Sicht der Rolle.")] string? scope = null,
        [Description("Zeitraum, z. B. heute, gestern, diese Woche, KW 42, Vorwoche. Standard: heute.")] string? zeitraum = null,
        [Description("Gruppierung: Station, Bereich, Halle, Schicht, Tag. Ohne Angabe: ein Gesamtwert.")] string? gruppierung = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var range = ranges.Resolve(zeitraum, now, defaultText: "heute");
        var group = Norm(gruppierung);
        var reply = new Reply("get_oee").Add("bereich", resolved.Label).Add("zeitraum", Views.RangeInfo(range, now));
        var rows = new List<object>();

        if (group == "schicht")
        {
            foreach (var kind in Enum.GetValues<Shift>())
            {
                var data = await kpis.GetByShiftKindAsync(range, kind, cancellationToken);
                rows.Add(OeeRow($"{ShiftCalendar.ShiftLabel(kind)}schicht", data.Figures(resolved.Stations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id)), data.Figures(DiscIds(resolved))));
            }

            if (resolved.CounterStations.Count > 0) session.Notes.Add("Die Hallen B und C liegen nur als Tageswerte vor; der Schichtvergleich gilt für Halle A.");
            return await reply.Add("gruppen", rows).BuildAsync(session, cancellationToken);
        }

        if (group == "tag")
        {
            foreach (var day in ranges.DaysOf(range, now))
            {
                var data = await kpis.GetAsync(day, resolved.CounterStations.Count > 0, cancellationToken);
                rows.Add(OeeRow(day.Label, data.Figures(resolved.OeeStationIds), data.Figures(DiscIds(resolved))));
            }

            return await reply.Add("gruppen", rows).BuildAsync(session, cancellationToken);
        }

        var block = await kpis.GetAsync(range, resolved.CounterStations.Count > 0, cancellationToken);
        var occupancy = await OccupancyAsync(resolved.Stations.Where(s => s.Area == Area.Curing).Select(s => s.Id).ToList(), range, now, cancellationToken);
        switch (group)
        {
            case "station":
                foreach (var station in resolved.Stations.Where(s => s.Area != Area.Buffer))
                {
                    rows.Add(station.Area == Area.Curing ? OvenRow(station.Id, occupancy.GetValueOrDefault(station.Id) ?? OccupancyFigures.Empty) : OeeRow(station.Id, block.Figures(station.Id)));
                }

                break;
            case "bereich":
                foreach (var area in new[] { Area.Pressing, Area.Curing, Area.Packaging }.Where(a => resolved.Stations.Any(s => s.Area == a)))
                {
                    var ids = resolved.Stations.Where(s => s.Area == area).Select(s => s.Id).ToList();
                    rows.Add(area == Area.Curing
                        ? OvenRow("Öfen", occupancy.Values.Aggregate(OccupancyFigures.Empty, (sum, o) => sum.Add(o)))
                        : OeeRow(Views.AreaWord(area), block.Figures(ids)));
                }

                break;
            case "halle":
                if (resolved.HasHallA) rows.Add(OeeRow("Halle A", block.Figures(resolved.Stations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id)), block.Figures(resolved.Stations.Where(s => s.Area == Area.Pressing).Select(s => s.Id))));
                rows.AddRange(resolved.CounterStations.Select(c => OeeRow(c == "HALLE-B" ? "Halle B" : "Halle C", block.Figures(c))));
                break;
            case "":
            case "gesamt":
                break;
            default:
                throw new McpException($"Unbekannte Gruppierung '{gruppierung}'. Gültig: Station, Bereich, Halle, Schicht, Tag.");
        }

        var overall = resolved.OeeStationIds.ToList();
        if (overall.Count > 0) reply.Add("gesamt", OeeRow(resolved.Label, block.Figures(overall), block.Figures(DiscIds(resolved))));
        if (occupancy.Count > 0 && group != "bereich" && group != "station") reply.Add("oefen", OvenRow("Öfen", occupancy.Values.Aggregate(OccupancyFigures.Empty, (sum, o) => sum.Add(o))));
        if (rows.Count > 0) reply.Add("gruppen", rows);
        if (resolved.CounterStations.Count > 0 && block.Counters.Count == 0) session.Notes.Add("Für Hallen B und C liegen im Zeitraum noch keine Tageswerte vor.");
        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_downtime_pareto", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Zeigt den ungeplanten Stillstand nach Grund (Pareto) mit Dauer, Anzahl, Anteil und Kosten (Stillstand × Kostensatz der Station); "
        + "geplante Stopps, Taktverluste und Überproduktion zählen nicht. Mit bewertung 'Kosten' wird nach Kosten sortiert und die Kostensumme "
        + "(Ausschuss + Stillstand), der größte Einzelposten und die Vorperiode ausgewiesen. Gruppierung: Station oder Baugruppe. Der Filter kann "
        + "eine Schicht (Nachtschicht) oder ein Grund (Mischung) sein. Nächster Schritt: compare für den Vergleich der Stationen, get_machine_status für Details.")]
    public async Task<CallToolResult> GetDowntimePareto(
        [Description("Bereich: Halle A, Werk, Bereich oder Station. Standard: die Sicht der Rolle.")] string? scope = null,
        [Description("Zeitraum, z. B. Schicht, gestern, KW 42, 90 Tage. Standard: aktuelle Schicht.")] string? zeitraum = null,
        [Description("Gruppierung: Station oder Baugruppe. Ohne Angabe: nach Grund.")] string? gruppierung = null,
        [Description("Filter: Frühschicht, Spätschicht, Nachtschicht oder ein Teil des Grunds (z. B. Mischung).")] string? filter = null,
        [Description("'Kosten' = nach Kosten sortieren und Kosten ausweisen.")] string? bewertung = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var range = ranges.Resolve(zeitraum, now, defaultText: "aktuelle Schicht");
        var costRates = await rates.GetAsync(cancellationToken);
        var byCost = Norm(bewertung).StartsWith("kosten", StringComparison.Ordinal);
        var kind = ShiftKind(filter);
        var reasonFilter = kind is null && !string.IsNullOrWhiteSpace(filter) ? filter.Trim() : null;
        var counters = resolved.CounterStations.Count > 0 && kind is null;
        var data = kind is { } k ? await kpis.GetByShiftKindAsync(range, k, cancellationToken) : await kpis.GetAsync(range, counters, cancellationToken);
        if (kind is not null && resolved.CounterStations.Count > 0) session.Notes.Add("Die Hallen B und C liegen nur als Tageswerte vor und fehlen im Schichtfilter.");
        var combined = data.Combined();
        var stationIds = resolved.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id).Concat(resolved.CounterStations).ToList();

        var reply = new Reply("get_downtime_pareto")
            .Add("bereich", resolved.Label)
            .Add("zeitraum", Views.RangeInfo(range, now));
        if (kind is { } shiftKind) reply.Add("filter", $"{ShiftCalendar.ShiftLabel(shiftKind)}schichten im Zeitraum");

        var entries = DowntimePareto.Build(combined, stationIds, costRates)
            .Where(e => reasonFilter is null || e.Reason.Contains(reasonFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var group = Norm(gruppierung);
        var total = TimeSpan.FromTicks(entries.Sum(e => e.Duration.Ticks));

        switch (group)
        {
            case "station":
                reply.Add("gruppen", StationGroups(combined, stationIds, costRates, reasonFilter, byCost));
                break;
            case "baugruppe":
                reply.Add("gruppen", await AssemblyGroupsAsync(range, stationIds, costRates, now, byCost, entries, cancellationToken));
                break;
            case "":
            case "grund":
                break;
            default:
                throw new McpException($"Unbekannte Gruppierung '{gruppierung}'. Gültig: Station, Baugruppe.");
        }

        var sorted = (byCost ? entries.OrderByDescending(e => e.CostEuro) : entries.OrderByDescending(e => e.Duration)).ToList();
        if (sorted.Count > options.Value.MaxRows) session.Notes.Add($"Einträge gekürzt: {options.Value.MaxRows} von {sorted.Count} gezeigt.");
        reply.Add("eintraege", sorted.Take(options.Value.MaxRows).Select(e => new
        {
            e.Reason,
            DauerMin = Fmt.Minutes(e.Duration),
            DauerH = Fmt.Hours(e.Duration),
            Anzahl = e.Count,
            AnteilProzent = total > TimeSpan.Zero ? Fmt.Round(100 * e.Duration / total, 1) : 0,
            KostenEuro = Fmt.Round(e.CostEuro, 0),
        })).Add("summe", new
        {
            DauerMin = Fmt.Minutes(total),
            DauerH = Fmt.Hours(total),
            Anzahl = entries.Sum(e => e.Count),
            KostenEuro = Fmt.Round(entries.Sum(e => e.CostEuro), 0),
        });

        if (byCost) await AddCostsAsync(reply, range, combined, stationIds, costRates, counters, now, cancellationToken);
        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "compare", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Vergleicht eine Kennzahl über eine Dimension und liefert eine Tabelle mit Wert, Einheit und Bewertung. Dimensionen: Station "
        + "(oder Rundtisch), Bereich, Halle, Schicht, Woche, Tag, Auftrag. Kennzahlen: OEE, Verfügbarkeit, Leistung, Qualität, Planerfüllung, "
        + "Ausschussquote, Ausschusskosten, Kosten, Stillstand (optional mit Grund, z. B. 'Stillstand Gewebezuführung'), MTBF, MTTR, "
        + "'Fertigstellung vs. Termin'. Zeiträume wie bei get_oee, zusätzlich 24 h. Nächster Schritt: get_downtime_pareto oder get_oee mit der "
        + "auffälligen Zeile als scope.")]
    public async Task<CallToolResult> Compare(
        [Description("Dimension: Station, Rundtisch, Bereich, Halle, Schicht, Woche, Tag oder Auftrag.")] string dimension,
        [Description("Kennzahl, z. B. OEE, Ausschussquote, Ausschusskosten, Stillstand Gewebezuführung, MTBF.")] string kpi,
        [Description("Zeitraum, z. B. heute, KW 42, KW 41-42, 24 h. Standard: heute.")] string? zeitraum = null,
        [Description("Bereich, der verglichen wird. Standard: die Sicht der Rolle.")] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var dim = Norm(dimension);
        var key = Norm(kpi);
        var range = ranges.Resolve(zeitraum, now, defaultText: "heute");
        var reply = new Reply("compare").Add("dimension", dimension).Add("kennzahl", kpi).Add("bereich", resolved.Label).Add("zeitraum", Views.RangeInfo(range, now));

        if (dim == "auftrag")
        {
            var views = await orders.GetAsync(now, [.. resolved.HallAIds], resolved.CounterStations.Select(c => c == "HALLE-B" ? "B" : "C").ToList(), null, cancellationToken);
            session.Sources.Add("Auftragsdaten", now);
            return await reply.Add("zeilen", views.Select(v => new
            {
                Name = $"Auftrag {v.Order.Id} · {v.ArticleLabel}",
                Wert = Fmt.Percent(v.Progress),
                Einheit = "% fertig",
                Termin = Fmt.Stamp(v.Order.DueUtc),
                Bewertung = v.Rating,
                BewertungText = RatingText.Word(v.Rating),
                Begruendung = v.Reason,
            })).BuildAsync(session, cancellationToken);
        }

        if (key.StartsWith("ausschussquote", StringComparison.Ordinal) && range is { Kind: RangeKind.Custom, Periods.Count: 0 } && dim is "station" or "rundtisch" or "presse" or "pressen")
        {
            return await ScrapRateByCycleAsync(reply, resolved, range, now, cancellationToken);
        }

        var members = await MembersAsync(dim, range, resolved, now, cancellationToken);
        var costRates = await rates.GetAsync(cancellationToken);
        var rows = new List<(string Name, double? Value, object Row)>();
        foreach (var member in members)
        {
            rows.Add(await ValueAsync(member, key, kpi, costRates, range, now, cancellationToken));
        }

        var ordered = rows.Where(r => r.Value is not null).ToList();
        object? worst = ordered.Count == 0 ? null : ordered.OrderBy(r => r.Value).First().Name;
        return await reply
            .Add("zeilen", rows.Select(r => r.Row))
            .Add("niedrigster_wert", worst)
            .BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_reliability_kpis", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Berechnet Zuverlässigkeitskennzahlen: MTBF (Laufzeit / Störungen), MTTR (Störungsdauer / Störungen), Anzahl Störungen und den "
        + "Anteil ungeplanter Stillstände (Störung / (Störung + geplante Wartung)), je Station, Monat oder gesamt, bei einer Station auch den Anteil "
        + "der Störungen je Grund und den Vergleich mit den anderen Rundtischen. Lange Zeiträume (90 Tage, Jul-Okt) kommen aus den "
        + "Tageskennzahlen abgeschlossener Tage. Nächster Schritt: get_downtime_pareto mit scope=Station und get_maintenance_due für überfällige Wartung.")]
    public async Task<CallToolResult> GetReliabilityKpis(
        [Description("Bereich: Halle A, Werk oder Station. Standard: die Sicht der Rolle.")] string? scope = null,
        [Description("Zeitraum, z. B. KW 42, 90 Tage, Jul-Okt. Standard: 90 Tage.")] string? zeitraum = null,
        [Description("Gruppierung: Station oder Monat. Ohne Angabe: ein Gesamtwert.")] string? gruppierung = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var range = ranges.Resolve(zeitraum, now, defaultText: "90 Tage");
        var stationIds = resolved.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id).Concat(resolved.CounterStations).ToList();
        var days = await ClosedDaysAsync(range, stationIds, cancellationToken);
        var group = Norm(gruppierung);
        var reply = new Reply("get_reliability_kpis").Add("bereich", resolved.Label).Add("zeitraum", Views.RangeInfo(range, now));

        var total = ProductionFigures.Sum(days.Select(d => d.Figures));
        reply.Add("gesamt", ReliabilityRow(resolved.Label, total));
        switch (group)
        {
            case "station":
                reply.Add("gruppen", days.GroupBy(d => d.StationId).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => ReliabilityRow(g.Key, ProductionFigures.Sum(g.Select(d => d.Figures)))));
                break;
            case "monat":
                reply.Add("gruppen", days.GroupBy(d => (d.Day.Year, d.Day.Month)).OrderBy(g => g.Key)
                    .Select(g => ReliabilityRow(MonthLabel(g.Key.Year, g.Key.Month, g.Max(d => d.Day), now), ProductionFigures.Sum(g.Select(d => d.Figures)))));
                break;
            case "":
                break;
            default:
                throw new McpException($"Unbekannte Gruppierung '{gruppierung}'. Gültig: Station, Monat.");
        }

        if (resolved.IsSingleStation)
        {
            var station = resolved.Stations[0];
            var own = days.Where(d => d.StationId == station.Id).ToList();
            var failures = Reliability.From(ProductionFigures.Sum(own.Select(d => d.Figures))).Failures;
            var reasons = own.SelectMany(d => d.Reasons).GroupBy(p => p.Key).Select(g => (Reason: g.Key, Count: g.Sum(p => p.Value.Count), Duration: TimeSpan.FromTicks(g.Sum(p => p.Value.Duration.Ticks))))
                .Where(r => r.Reason.StartsWith("Störung", StringComparison.Ordinal) && r.Count > 0)
                .OrderByDescending(r => r.Count).ToList();
            reply.Add("stoerungen_nach_grund", reasons.Select(r => new
            {
                r.Reason,
                r.Count,
                AnteilAnStoerungenProzent = failures > 0 ? Fmt.Percent((double)r.Count / failures) : 0,
                DauerMin = Fmt.Minutes(r.Duration),
            }));

            if (station.Area == Area.Pressing)
            {
                var others = PlantLayout.HallAStations.Where(s => s.Area == Area.Pressing && s.Id != station.Id).Select(s => s.Id).ToList();
                var otherDays = await ClosedDaysAsync(range, others, cancellationToken);
                var mtbfs = others.Select(id => Reliability.From(ProductionFigures.Sum(otherDays.Where(d => d.StationId == id).Select(d => d.Figures))).Mtbf)
                    .Where(m => m is not null).Select(m => m!.Value.TotalHours).ToList();
                if (mtbfs.Count > 0) reply.Add("schnitt_andere_rundtische", new { MtbfH = Fmt.Round(mtbfs.Average(), 0), Anzahl = mtbfs.Count });
            }
        }

        session.Notes.Add("Zuverlässigkeitskennzahlen rechnen mit abgeschlossenen Produktionstagen (Tageskennzahlen); der laufende Tag fehlt.");
        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_overproduction", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Findet Überproduktion: Produktion über den Auftrag hinaus oder vor Bedarf. Je Ereignis Station, Auftrag, Zeitraum, Stückzahl, "
        + "Ist/Soll der Schicht (kann über 100 % und im Dashboard trotzdem grün sein) und die Folgen: Pufferfüllung vor den Öfen (Spitze mit "
        + "Zeitpunkt) und Wartezeiten auf den Ofen an den nachfolgenden Stationen (Ursachenkette Überproduktion → Puffer voll → Presse wartet → "
        + "Verpackung steht). Ohne Zeitraum die letzten 14 Tage. Nächster Schritt: get_hall_overview oder get_order_progress.")]
    public async Task<CallToolResult> GetOverproduction(
        [Description("Bereich: Halle A oder Station. Standard: Halle A.")] string? scope = null,
        [Description("Zeitraum, z. B. KW 41, diese Woche, 14 Tage. Standard: letzte 14 Tage.")] string? zeitraum = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var range = ranges.Resolve(zeitraum, now, defaultText: "14 Tage");
        var ids = resolved.HallAIds.ToList();
        var losses = await queries.LossEventsAsync(range.StartUtc, range.EffectiveEnd(now), ids);
        session.Sources.Add("Redis", range.EffectiveEnd(now));
        var events = EventKpis.Overproduction(losses, range.Window);
        var wide = await queries.LossEventsAsync(range.StartUtc, now, ids);

        var rows = new List<object>();
        foreach (var overproduction in events)
        {
            var shift = calendar.ShiftPeriodOf(overproduction.StartUtc);
            var shiftBlock = await blocks.GetAsync(shift, cancellationToken);
            session.Sources.Add(shiftBlock.SourceLabel, shiftBlock.Block.SourceUntilUtc);
            var ratio = PlanFulfillment.From(shiftBlock.Block.Figures(overproduction.StationId)).Ratio;
            var state = await queries.StateAtAsync(overproduction.StationId, overproduction.StartUtc.AddMinutes(1)) ?? OperatingState.Running;
            var light = TrafficLightRules.Evaluate(new StationSignals(state, ratio, HasRunningOrder: state != OperatingState.RunningWithoutOrder)).Light;
            var peak = await queries.PeakAsync("PU-OFEN", MinuteVariables.PlateCount, overproduction.StartUtc, (overproduction.EndUtc ?? now).AddHours(12));
            var after = wide.Where(l => l.Reason == LossReasons.OvenWait && l.StartUtc >= overproduction.StartUtc && l.StartUtc < (overproduction.EndUtc ?? now).AddHours(48)).ToList();
            rows.Add(new
            {
                Station = overproduction.StationId,
                Beginn = Fmt.Stamp(overproduction.StartUtc),
                Ende = overproduction.EndUtc is { } end ? Fmt.Stamp(end) : null,
                Stueck = overproduction.Pieces,
                Ursache = overproduction.Cause,
                Schicht = ranges.ShiftLabel(shift),
                IstSollProzent = Fmt.Percent(ratio),
                AmpelImDashboard = light.Label(),
                Hinweis = light == TrafficLight.Running
                    ? $"Ist/Soll {Fmt.Percent(ratio)} % ist im Dashboard grün, obwohl vor Bedarf produziert wurde."
                    : "Produktion ohne laufenden Auftrag: Warnung.",
                Folgen = new
                {
                    PufferSpitzePlatten = peak is { } p ? (int)p.Value : (int?)null,
                    PufferSpitzeZeit = peak is { } q ? Fmt.Stamp(q.At) : null,
                    WartenAufOfen = after.Select(l => new { Station = l.StationId, Beginn = Fmt.Stamp(l.StartUtc), DauerMin = Fmt.Minutes((l.EndUtc ?? now) - l.StartUtc), l.Cause }),
                },
            });
        }

        return await new Reply("get_overproduction")
            .Add("bereich", resolved.Label)
            .Add("zeitraum", Views.RangeInfo(range, now))
            .Add("anzahl", rows.Count)
            .Add("summe_stueck", events.Sum(e => (long)e.Pieces))
            .Add("ereignisse", rows)
            .BuildAsync(session, cancellationToken);
    }

    // ---- Schichtbericht -------------------------------------------------------------------------------------

    private static object StationLine(Station station, KpiBlock data, DateTimeOffset now, ProductionPeriod shift)
    {
        var figures = data.Figures(station.Id);
        var plan = PlanFulfillment.From(figures);
        var standstill = data.Stations.TryGetValue(station.Id, out var block)
            ? TimeSpan.FromTicks(block.Reasons.Values.Sum(r => r.Duration.Ticks))
            : TimeSpan.Zero;
        return new
        {
            Station = station.Id,
            Ist = plan.Actual,
            Soll = Fmt.Round(plan.Planned, 0),
            IstSollProzent = plan.Planned > 0 ? Fmt.Percent(plan.Ratio) : (int?)null,
            Bewertung = plan.Planned > 0 ? RatePlan(plan.Ratio) : Rating.Info,
            StillstandMin = Fmt.Minutes(standstill),
        };
    }

    private async Task<object> ProjectionAsync(
        ProductionPeriod shift, KpiBlock data, IReadOnlyList<string> presses, StationSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        var elapsed = now - shift.StartUtc;
        var duration = shift.EndUtc - shift.StartUtc;
        var lines = new List<object>();
        foreach (var id in presses)
        {
            var figures = data.Figures(id);
            var plan = PlanFulfillment.From(figures);
            var projected = PlanFulfillment.ProjectToShiftEnd(plan.Actual, elapsed, duration);
            var planned = (double)plan.Planned * duration.TotalSeconds / elapsed.TotalSeconds;
            var loss = await SpeedLossAsync(id, snapshot, shift, now, ct);
            lines.Add(new
            {
                Station = id,
                IstPrognose = Math.Round(projected),
                Plan = Math.Round(planned),
                Prozent = planned > 0 ? Fmt.Percent(projected / planned) : (int?)null,
                LueckeStueck = Math.Round(planned - projected),
                Bewertung = planned > 0 ? RatePlan(projected / planned) : Rating.Info,
                TaktverlustBisSchichtende = loss is null ? null : new { loss.Text, Stueck = Math.Round(loss.Pieces), Seit = Fmt.Time(loss.OnsetUtc) },
            });
        }

        var all = data.Figures(presses);
        var total = PlanFulfillment.From(all);
        var projectedTotal = PlanFulfillment.ProjectToShiftEnd(total.Actual, elapsed, duration);
        var plannedTotal = (double)total.Planned * duration.TotalSeconds / elapsed.TotalSeconds;
        return new
        {
            Bereich = "Pressen",
            IstBisJetzt = total.Actual,
            SollBisJetzt = Fmt.Round(total.Planned, 0),
            IstPrognose = Math.Round(projectedTotal),
            PlanSchichtende = Math.Round(plannedTotal),
            Prozent = plannedTotal > 0 ? Fmt.Percent(projectedTotal / plannedTotal) : (int?)null,
            Text = $"Hochgerechnet {Fmt.Thousands(Math.Round(projectedTotal / 100) * 100)} von {Fmt.Thousands(Math.Round(plannedTotal / 100) * 100)} Stück ({(plannedTotal > 0 ? Fmt.Percent(projectedTotal / plannedTotal) : 0)} %).",
            JeStation = lines,
        };
    }

    /// <summary>Verlust durch verlängerte Taktzeit bis Schichtende, wenn der Takt einer Presse über der Warngrenze liegt.</summary>
    private async Task<SpeedLossForecast?> SpeedLossAsync(string stationId, StationSnapshot snapshot, ProductionPeriod shift, DateTimeOffset now, CancellationToken ct)
    {
        if (snapshot.Find(stationId) is not { CycleSeconds: { } cycle, TargetCycleSeconds: > 0 and var target } state
            || cycle / target - 1 <= TrafficLightThresholds.Default.CycleWarningAbove)
        {
            return null;
        }

        var origin = ranges.FloorToHour(shift.StartUtc);
        var fine = await queries.CycleBucketsAsync(stationId, SeriesNames.CycleTime, shift.StartUtc, now, TimeSpan.FromMinutes(10), origin);
        return SpeedLossForecast.Compute(state, DriftDetector.Detect(fine), shift.EndUtc);
    }

    internal static string ScrapWord(string reason) => reason switch
    {
        nameof(ScrapReason.Weight) => "Gewicht",
        nameof(ScrapReason.Thickness) => "Dicke",
        nameof(ScrapReason.Fabric) => "Gewebe",
        nameof(ScrapReason.Label) => "Etikett",
        nameof(ScrapReason.Breakage) => "Bruch",
        _ => reason,
    };

    // ---- OEE ------------------------------------------------------------------------------------------------

    private static object OeeBlock(ProductionFigures figures)
    {
        var oee = OeeCalculator.From(figures);
        return new
        {
            OeeProzent = OeeResult.Percent(oee.Oee),
            VerfuegbarkeitProzent = OeeResult.Percent(oee.Availability),
            LeistungProzent = OeeResult.Percent(oee.Performance),
            QualitaetProzent = OeeResult.Percent(oee.Quality),
            Bewertung = RateOee(oee.Oee),
        };
    }

    /// <summary>Stationen, die Scheiben fertigen: Rundtische und die Zählerhallen (die Verpackung zählt Kartons).</summary>
    private static IEnumerable<string> DiscIds(ResolvedScope scope) =>
        scope.Stations.Where(s => s.Area == Area.Pressing).Select(s => s.Id).Concat(scope.CounterStations);

    /// <summary>OEE-Zeile. Mischen sich Scheiben und Kartons, stehen Ist, Soll und Ausschuss nur für die Scheiben (<paramref name="discs"/>).</summary>
    private static object OeeRow(string name, ProductionFigures figures, ProductionFigures? discs = null)
    {
        if (figures.PlanningTime <= TimeSpan.Zero) return new { Gruppe = name, Hinweis = "keine Daten im Zeitraum" };

        var oee = OeeCalculator.From(figures);
        var rating = RateOee(oee.Oee);
        return new
        {
            Gruppe = name,
            OeeProzent = OeeResult.Percent(oee.Oee),
            VerfuegbarkeitProzent = OeeResult.Percent(oee.Availability),
            LeistungProzent = OeeResult.Percent(oee.Performance),
            QualitaetProzent = OeeResult.Percent(oee.Quality),
            Mengen = discs is null ? null : "Scheiben (ohne Kartons der Verpackung)",
            Ist = (discs ?? figures).TotalPieces,
            Soll = Fmt.Round((discs ?? figures).PlannedQuantity, 0),
            IstSollProzent = (discs ?? figures).PlannedQuantity > 0 ? Fmt.Percent(PlanFulfillment.From(discs ?? figures).Ratio) : (int?)null,
            AusschussProzent = (discs ?? figures).TotalPieces > 0 ? Fmt.Round(100.0 * (discs ?? figures).ScrapPieces / (discs ?? figures).TotalPieces, 1) : (double?)null,
            Bewertung = rating,
            BewertungText = RatingText.Word(rating),
        };
    }

    private static object OvenRow(string name, OccupancyFigures occupancy)
    {
        var rating = occupancy.Ratio >= TrafficLightThresholds.Default.OvenWarningAt ? Rating.Warn : Rating.Ok;
        return new
        {
            Gruppe = name,
            BelegungProzent = Fmt.Percent(occupancy.Ratio),
            Hinweis = "Öfen werden über die Belegung bewertet (zeitgewichtete Platzbelegung im Zeitraum), nicht über die OEE.",
            Bewertung = rating,
            BewertungText = RatingText.Word(rating),
        };
    }

    private async Task<Dictionary<string, OccupancyFigures>> OccupancyAsync(
        IReadOnlyCollection<string> ovens, ResolvedRange range, DateTimeOffset now, CancellationToken ct)
    {
        var result = new Dictionary<string, OccupancyFigures>();
        if (ovens.Count == 0) return result;

        var values = await queries.OvenOccupancyAsync(ovens, range.StartUtc, range.EffectiveEnd(now));
        foreach (var (id, (occupied, minutes)) in values)
        {
            result[id] = new OccupancyFigures(occupied, (double)plant.Value.OvenPlaces * minutes);
        }

        return result;
    }

    internal static string RateOee(double oee) => oee >= OeeGood ? Rating.Ok : oee >= OeeWarn ? Rating.Warn : Rating.Crit;

    private static string RatePlan(double ratio) =>
        ratio < TrafficLightThresholds.Default.PlanProblemBelow ? Rating.Crit
        : ratio < TrafficLightThresholds.Default.PlanWarningBelow ? Rating.Warn
        : Rating.Ok;

    // ---- Pareto ---------------------------------------------------------------------------------------------

    private static List<object> StationGroups(
        KpiBlock combined, IReadOnlyList<string> stationIds, CostRateTable costRates, string? reasonFilter, bool byCost)
    {
        var rows = new List<(string Station, TimeSpan Duration, int Count, decimal Cost, string Top)>();
        foreach (var id in stationIds.Where(combined.Stations.ContainsKey))
        {
            var reasons = combined.Stations[id].Reasons
                .Where(r => reasonFilter is null || r.Key.Contains(reasonFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (reasons.Count == 0) continue;

            var duration = TimeSpan.FromTicks(reasons.Sum(r => r.Value.Duration.Ticks));
            rows.Add((id, duration, reasons.Sum(r => r.Value.Count), costRates.DowntimeCost(id, duration), reasons.OrderByDescending(r => r.Value.Duration).First().Key));
        }

        var ordered = byCost ? rows.OrderByDescending(r => r.Cost) : rows.OrderByDescending(r => r.Duration);
        return [.. ordered.Select(r => (object)new
        {
            Gruppe = r.Station,
            StillstandMin = Fmt.Minutes(r.Duration),
            StillstandH = Fmt.Hours(r.Duration),
            Anzahl = r.Count,
            KostenEuro = Fmt.Round(r.Cost, 0),
            GroessterGrund = r.Top,
        })];
    }

    private async Task<object> AssemblyGroupsAsync(
        ResolvedRange range, IReadOnlyList<string> stationIds, CostRateTable costRates, DateTimeOffset now, bool byCost,
        IReadOnlyList<ParetoEntry> reasonEntries, CancellationToken ct)
    {
        var rawStart = await kpis.RawStartAsync();
        var losses = range.StartUtc < rawStart
            ? []
            : (await queries.LossEventsAsync(range.StartUtc, range.EffectiveEnd(now), stationIds))
                .Where(l => l.IsStandstill && l.Category is not (LossCategory.Setup or LossCategory.PlannedMaintenance))
                .ToList();
        if (losses.Count == 0)
        {
            session.Notes.Add("Vor Beginn der Rohdaten (KW 41) gibt es nur Tageskennzahlen ohne Baugruppe; die Gruppen zeigen den Grund, der die Baugruppe benennt (z. B. Störung Gewebezuführung).");
            return reasonEntries.Select(e => new { Gruppe = e.Reason, StillstandMin = Fmt.Minutes(e.Duration), Anzahl = e.Count, KostenEuro = Fmt.Round(e.CostEuro, 0) }).ToList();
        }

        var window = range.Window.Intersect(range.StartUtc, range.EffectiveEnd(now));
        var rows = losses.GroupBy(l => l.Assembly).Select(g =>
        {
            var duration = TimeSpan.FromTicks(g.Sum(l => new TimeWindow(l.StartUtc, l.EndUtc ?? now).Intersect(window).Duration.Ticks));
            var cost = g.Sum(l => costRates.DowntimeCost(l.StationId, new TimeWindow(l.StartUtc, l.EndUtc ?? now).Intersect(window).Duration));
            return (Assembly: g.Key, Duration: duration, Count: g.Count(), Cost: cost);
        });
        return (byCost ? rows.OrderByDescending(r => r.Cost) : rows.OrderByDescending(r => r.Duration))
            .Select(r => new { Gruppe = r.Assembly, StillstandMin = Fmt.Minutes(r.Duration), Anzahl = r.Count, KostenEuro = Fmt.Round(r.Cost, 0) }).ToList();
    }

    private async Task AddCostsAsync(
        Reply reply, ResolvedRange range, KpiBlock combined, IReadOnlyList<string> stationIds, CostRateTable costRates, bool counters,
        DateTimeOffset now, CancellationToken ct)
    {
        var costs = CostCalculator.Of(combined, stationIds, costRates);
        var biggest = stationIds.Where(combined.Stations.ContainsKey)
            .SelectMany(id => combined.Stations[id].Reasons.Keys.Select(reason => (Station: id, Reason: reason, Cost: CostCalculator.ReasonCost(combined, id, reason, costRates))))
            .OrderByDescending(x => x.Cost).FirstOrDefault();
        reply.Add("kosten", new
        {
            AusschussEuro = Fmt.Round(costs.ScrapEuro, 0),
            StillstandEuro = Fmt.Round(costs.DowntimeEuro, 0),
            GesamtEuro = Fmt.Round(costs.TotalEuro, 0),
            GroessterPosten = biggest.Station is null ? null : new { biggest.Station, Grund = biggest.Reason, KostenEuro = Fmt.Round(biggest.Cost, 0) },
        });

        if (range.Kind != RangeKind.Week || range.Periods.Count != 1) return;

        var week = range.Periods[0];
        var previous = calendar.WeekPeriod(calendar.ProductionDayOf(week.StartUtc.AddDays(-3)));
        var before = await kpis.GetAsync(ranges.ForPeriod(previous), counters, ct);
        var previousCosts = CostCalculator.Of(before.Combined(), stationIds, costRates);
        if (previousCosts.TotalEuro > 0)
        {
            reply.Add("vorperiode", new
            {
                Bezeichnung = TimeRangeResolver.WeekLabel(previous),
                GesamtEuro = Fmt.Round(previousCosts.TotalEuro, 0),
                VeraenderungProzent = Fmt.Percent((double)((costs.TotalEuro / previousCosts.TotalEuro) - 1)),
            });
        }
    }

    // ---- Vergleich ------------------------------------------------------------------------------------------

    /// <summary>Ausschussquote je Rundtisch für freie Fenster (z. B. 24 h) direkt aus den Takten: Ausschusstakte / alle Takte.</summary>
    private async Task<CallToolResult> ScrapRateByCycleAsync(Reply reply, ResolvedScope scope, ResolvedRange range, DateTimeOffset now, CancellationToken ct)
    {
        var presses = scope.Stations.Where(s => s.Area == Area.Pressing).Select(s => s.Id).ToList();
        var cycles = await queries.CycleAggregatesAsync(range.StartUtc, range.EffectiveEnd(now), presses);
        session.Sources.Add("Redis (Zeitreihen)", range.EffectiveEnd(now));
        var rows = presses.Select(id =>
        {
            var aggregate = cycles.GetValueOrDefault(id);
            var rate = aggregate is null ? (double?)null : Fmt.Round(100 * aggregate.ScrapRate, 1);
            var rating = rate is { } r ? (r >= 5 ? Rating.Crit : r >= 2.5 ? Rating.Warn : Rating.Ok) : Rating.Info;
            return new
            {
                Name = id,
                Wert = rate,
                Einheit = "%",
                Bewertung = rating,
                BewertungText = RatingText.Word(rating),
                Details = new { Takte = aggregate?.Count ?? 0, Ausschuss = aggregate?.Scrap ?? 0 },
            };
        }).ToList();
        return await reply
            .Add("zeilen", rows)
            .Add("hoechster_wert", rows.Where(r => r.Wert is not null).OrderByDescending(r => r.Wert).Select(r => r.Name).FirstOrDefault())
            .BuildAsync(session, ct);
    }

    private sealed record Member(string Label, KpiData Data, IReadOnlyList<string> Stations, Area? Area);

    private async Task<IReadOnlyList<Member>> MembersAsync(
        string dim, ResolvedRange range, ResolvedScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var members = new List<Member>();
        switch (dim)
        {
            case "station" or "rundtisch" or "presse" or "pressen":
                var data = await kpis.GetAsync(range, false, ct);
                var selection = scope.Stations.Where(s => s.Area != Area.Buffer && (dim == "station" || s.Area == Area.Pressing));
                members.AddRange(selection.Select(s => new Member(s.Id, data, [s.Id], s.Area)));
                break;
            case "bereich":
                var areaData = await kpis.GetAsync(range, false, ct);
                foreach (var area in new[] { Area.Pressing, Area.Curing, Area.Packaging }.Where(a => scope.Stations.Any(s => s.Area == a)))
                {
                    members.Add(new Member(Views.AreaWord(area), areaData, [.. scope.Stations.Where(s => s.Area == area).Select(s => s.Id)], area));
                }

                break;
            case "halle":
                var hallData = await kpis.GetAsync(range, true, ct);
                if (scope.HasHallA) members.Add(new Member("Halle A", hallData, [.. scope.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id)], null));
                members.AddRange(scope.CounterStations.Select(c => new Member(c == "HALLE-B" ? "Halle B" : "Halle C", hallData, [c], null)));
                break;
            case "schicht":
                foreach (var kind in Enum.GetValues<Shift>())
                {
                    members.Add(new Member($"{ShiftCalendar.ShiftLabel(kind)}schicht", await kpis.GetByShiftKindAsync(range, kind, ct), [.. scope.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id)], null));
                }

                break;
            case "woche":
                var weeks = range.Periods.Where(p => p.Kind == PeriodKind.Week).ToList();
                if (weeks.Count == 0) weeks = [calendar.WeekPeriodOf(now)];
                foreach (var week in weeks)
                {
                    var weekData = await kpis.GetAsync(ranges.ForPeriod(week), scope.CounterStations.Count > 0, ct);
                    members.Add(new Member(TimeRangeResolver.WeekLabel(week), weekData, [.. scope.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id).Concat(scope.CounterStations)], null));
                }

                break;
            case "tag":
                foreach (var day in ranges.DaysOf(range, now))
                {
                    var dayData = await kpis.GetAsync(day, scope.CounterStations.Count > 0, ct);
                    members.Add(new Member(day.Label, dayData, [.. scope.Stations.Where(s => s.Area != Area.Buffer).Select(s => s.Id).Concat(scope.CounterStations)], null));
                }

                break;
            default:
                throw new McpException($"Unbekannte Dimension '{dim}'. Gültig: Station, Rundtisch, Bereich, Halle, Schicht, Woche, Tag, Auftrag.");
        }

        return members;
    }

    private async Task<(string Name, double? Value, object Row)> ValueAsync(
        Member member, string key, string kpiText, CostRateTable costRates, ResolvedRange range, DateTimeOffset now, CancellationToken ct)
    {
        var oeeIds = member.Stations.Where(id => PlantLayout.IsKnown(id) ? PlantLayout.Get(id).Area is Area.Pressing or Area.Packaging : true).ToList();
        var figures = member.Data.Figures(oeeIds);
        var all = member.Data.Figures(member.Stations);

        if (member.Area == Area.Curing && key is "oee" or "auslastung" or "belegung")
        {
            var occupancy = await OccupancyAsync(member.Stations, range, now, ct);
            var sum = occupancy.Values.Aggregate(OccupancyFigures.Empty, (s, o) => s.Add(o));
            return Row(member.Label, Fmt.Percent(sum.Ratio), "% Belegung", sum.Ratio >= TrafficLightThresholds.Default.OvenWarningAt ? Rating.Warn : Rating.Ok, null);
        }

        switch (key)
        {
            case "oee":
                return OeeValue(member.Label, figures, f => OeeCalculator.From(f).Oee);
            case "verfuegbarkeit":
                return OeeValue(member.Label, figures, f => OeeCalculator.From(f).Availability);
            case "leistung":
                return OeeValue(member.Label, figures, f => OeeCalculator.From(f).Performance);
            case "qualitaet":
                return OeeValue(member.Label, figures, f => OeeCalculator.From(f).Quality);
            case "planerfuellung" or "ist/soll" or "istsoll" or "ist-soll":
                return figures.PlannedQuantity > 0
                    ? Row(member.Label, Fmt.Percent(PlanFulfillment.From(figures).Ratio), "%", RatePlan(PlanFulfillment.From(figures).Ratio), new { Ist = figures.TotalPieces, Soll = Fmt.Round(figures.PlannedQuantity, 0) })
                    : Row(member.Label, null, "%", Rating.Info, null);
            case var q when q.StartsWith("ausschussquote", StringComparison.Ordinal):
                var rateValue = figures.TotalPieces > 0 ? Fmt.Round(100.0 * figures.ScrapPieces / figures.TotalPieces, 1) : (double?)null;
                return Row(member.Label, rateValue, "%", rateValue is { } rv ? (rv >= 5 ? Rating.Crit : rv >= 2.5 ? Rating.Warn : Rating.Ok) : Rating.Info, new { Ausschuss = figures.ScrapPieces, Ist = figures.TotalPieces });
            case "ausschusskosten" or "kosten":
                var costs = CostCalculator.Of(member.Data.Combined(), member.Stations.Where(id => member.Data.Combined().Stations.ContainsKey(id)), costRates);
                var value = key == "kosten" ? costs.TotalEuro : costs.ScrapEuro;
                return Row(member.Label, Fmt.Round(value, 0), "€", Rating.Info,
                    new { AusschussEuro = Fmt.Round(costs.ScrapEuro, 0), StillstandEuro = Fmt.Round(costs.DowntimeEuro, 0), GesamtEuro = Fmt.Round(costs.TotalEuro, 0) });
            case "mtbf" or "mttr":
                var reliability = Reliability.From(all);
                var span = key == "mtbf" ? reliability.Mtbf : reliability.Mttr;
                return Row(member.Label, span is null ? null : Fmt.Round(key == "mtbf" ? span.Value.TotalHours : span.Value.TotalMinutes, 0), key == "mtbf" ? "h" : "min", Rating.Info, new { Stoerungen = reliability.Failures });
            case var s when s.StartsWith("stillstand", StringComparison.Ordinal):
                var token = kpiText.Length > "Stillstand".Length ? kpiText["Stillstand".Length..].Trim() : null;
                var reasons = member.Stations.SelectMany(id => member.Data.Reasons(id))
                    .Where(r => string.IsNullOrEmpty(token) || r.Key.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
                var duration = TimeSpan.FromTicks(reasons.Sum(r => r.Value.Duration.Ticks));
                return Row(member.Label, Fmt.Round(duration.TotalHours, 2), "h", Rating.Info, new { DauerMin = Fmt.Minutes(duration), Anzahl = reasons.Sum(r => r.Value.Count) });
            default:
                throw new McpException($"Unbekannte Kennzahl '{kpiText}'. Gültig: OEE, Verfügbarkeit, Leistung, Qualität, Planerfüllung, Ausschussquote, Ausschusskosten, Kosten, Stillstand, MTBF, MTTR.");
        }
    }

    private static (string Name, double? Value, object Row) OeeValue(string name, ProductionFigures figures, Func<ProductionFigures, double> select)
    {
        if (figures.PlanningTime <= TimeSpan.Zero) return Row(name, null, "%", Rating.Info, null);

        var value = select(figures);
        return Row(name, OeeResult.Percent(value), "%", RateOee(OeeCalculator.From(figures).Oee), null);
    }

    private static (string Name, double? Value, object Row) Row(string name, double? value, string unit, string rating, object? details) =>
        (name, value, new { Name = name, Wert = value, Einheit = unit, Bewertung = rating, BewertungText = RatingText.Word(rating), Details = details });

    // ---- Zuverlässigkeit ------------------------------------------------------------------------------------

    private Task<IReadOnlyList<DailyStationKpi>> ClosedDaysAsync(ResolvedRange range, IReadOnlyCollection<string> stationIds, CancellationToken ct) =>
        kpis.DaysAsync(range, stationIds, closedOnly: true, ct);

    private static object ReliabilityRow(string name, ProductionFigures figures)
    {
        var reliability = Reliability.From(figures);
        return new
        {
            Gruppe = name,
            MtbfH = reliability.Mtbf is { } mtbf ? Fmt.Round(mtbf.TotalHours, 1) : (double?)null,
            MttrMin = reliability.Mttr is { } mttr ? Fmt.Round(mttr.TotalMinutes, 1) : (double?)null,
            Stoerungen = reliability.Failures,
            AnteilUngeplantProzent = Fmt.Percent(reliability.UnplannedShare),
            AnteilGeplantProzent = Fmt.Percent(1 - reliability.UnplannedShare),
            StoerungMin = Fmt.Minutes(figures.BreakdownTime),
            GeplanteWartungMin = Fmt.Minutes(figures.MaintenanceTime),
        };
    }

    private static string MonthLabel(int year, int month, DateOnly lastDay, DateTimeOffset now) =>
        new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified).ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("de-AT"))
        + (lastDay.Month == month && lastDay.Year == year && lastDay.AddDays(7) >= DateOnly.FromDateTime(now.UtcDateTime) ? " (bis " + Fmt.Day(lastDay) + ")" : string.Empty);

    private static Shift? ShiftKind(string? filter)
    {
        var text = Norm(filter);
        if (text.StartsWith("nacht", StringComparison.Ordinal)) return Shift.Night;
        if (text.StartsWith("frueh", StringComparison.Ordinal)) return Shift.Early;
        if (text.StartsWith("spaet", StringComparison.Ordinal)) return Shift.Late;
        return null;
    }

    private static string Norm(string? text) =>
        (text ?? string.Empty).Trim().ToLowerInvariant().Replace("ö", "oe", StringComparison.Ordinal).Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
}
