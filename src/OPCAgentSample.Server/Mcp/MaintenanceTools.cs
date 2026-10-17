using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Tools für Wartungsplanung aus der Wartungs-DB: fällige, überfällige, geplante und prognostizierte Wartungen sowie Konflikte und
/// Bündelungsvorschläge. Alles ist deterministisch aus den Stammdaten bis 17.01.2027, den Hubzählern und den Regeln des Servers.
/// </summary>
[McpServerToolType]
public sealed class MaintenanceTools(
    McpSession session,
    McpCaller caller,
    ScopeResolver scopes,
    TimeRangeResolver ranges,
    MaintenanceService maintenance,
    MaterialReachService material,
    OrderService orders,
    ShiftCalendar calendar,
    IOptions<McpOptions> options)
{
    [McpServerTool(Name = "get_maintenance_due", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Listet Wartungen der Wartungs-DB: überfällig (mit Tagen), heute fällig, geplant und prognostiziert (aus Hüben der Formen und "
        + "Betriebsstunden). Je Wartung Station, Art, Fälligkeit, Arbeitsdauer, Maschinenstillstand einzeln und gebündelt, Sicherheitsrelevanz, "
        + "Auftrag und bei Formen der Verschleiß mit Zeitpunkt der Standzeitgrenze. Zusätzlich Empfehlungen: Hat eine Station eine offene Zustandswarnung "
        + "und ihre reguläre Wartung steht erst später an, wird vorziehen vorgeschlagen. Horizont: Schicht, heute, morgen, 48 h, Woche, KW 43 "
        + "oder von/bis (Datum). Nächster Schritt: check_maintenance_conflicts für Konflikte und Bündelung, get_condition_warnings für den Anlass.")]
    public async Task<CallToolResult> GetMaintenanceDue(
        [Description("Bereich: Halle A, Werk, Bereich oder Station(en). Standard: Halle A.")] JsonElement? scope = null,
        [Description("Horizont: Schicht, heute, morgen, 48 h, Woche, KW 43. Standard: Woche.")] string? horizont = null,
        [Description("Beginn (Datum 2026-10-19 oder Zeitpunkt), alternativ zum Horizont.")] string? von = null,
        [Description("Ende (Datum oder Zeitpunkt), alternativ zum Horizont.")] string? bis = null,
        [Description("true = auch prognostizierte Wartungen (Standard).")] bool prognose = true,
        [Description("Nur Wartungen dieser Baugruppe, z. B. Hydraulik, Waage, Form.")] string? baugruppe = null,
        [Description("true = zusätzlich die erledigten Wartungen (Historie) im Bereich.")] bool historie = false,
        [Description("'überfällig' = nur überfällige Wartungen.")] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var (from, to, label) = Horizon(horizont, von, bis, now, "Woche");
        var overdueOnly = Norm(filter).StartsWith("ueberfaellig", StringComparison.Ordinal);
        var ids = resolved.HallAIds.ToList();
        var items = await maintenance.ItemsAsync(ids, from, to, now, prognose, baugruppe, overdueOnly, cancellationToken);
        var moulds = await maintenance.MouldsAsync(now, cancellationToken);
        session.Sources.Add("Wartungs-DB", now);

        var recommendations = overdueOnly ? [] : await maintenance.RecommendationsAsync(ids, items, to, now, cancellationToken);
        var reply = new Reply("get_maintenance_due")
            .Add("bereich", resolved.Label)
            .Add("zeitraum", new { Bezeichnung = label, Von = Fmt.Stamp(from), Bis = Fmt.Stamp(to) })
            .Add("anzahl", items.Count)
            .Add("ueberfaellig", items.Count(i => i.Overdue))
            .Add("wartungen", items.Take(options.Value.MaxRows).Select(i => ItemRow(i, moulds, now)))
            .Add("stillstand", new
            {
                EinzelnMin = items.Sum(i => i.StopMinutes),
                GebuendeltMin = items.Sum(i => i.StopMinutesBundled),
            })
            .Add("empfehlungen", recommendations.Select(r => new
            {
                r.StationId,
                Wartung = r.Title,
                GeplantFuer = r.PlannedText,
                Empfehlung = $"{r.Title} an {r.StationId} vorziehen: {r.Suggestion}",
                Begruendung = r.Reason,
                Bewertung = Rating.Crit,
            }));

        if (items.Count > options.Value.MaxRows) session.Notes.Add($"Wartungen gekürzt: {options.Value.MaxRows} von {items.Count} gezeigt.");
        if (historie)
        {
            session.Sources.Add("Wartungs-DB (Historie)", now);
            var history = await maintenance.HistoryAsync(ids, baugruppe, cancellationToken);
            reply.Add("historie", history.Take(options.Value.MaxRows).Select(h => new
            {
                Station = h.StationId,
                h.Title,
                ErledigtAm = Fmt.Stamp(h.DoneAtUtc),
                h.Result,
            }));
        }

        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "check_maintenance_conflicts", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Prüft Wartungstermine auf Konflikte mit laufenden Aufträgen und schlägt Bündelungen vor: Endet der laufende Auftrag einer Station vor "
        + "dem Wartungstermin, gibt es keinen Konflikt; passt der Termin in das Rüstfenster nach Auftragsende (oder zum Mischungswechsel), entfällt "
        + "der zusätzliche Maschinenstillstand. Ergebnis: Konflikte, Bündelungsvorschläge je Station, Stillstand einzeln gegenüber gebündelt "
        + "(Summe und je Station) und der Verschleiß der Form zum Rüstzeitpunkt. Nur für Hallenleiterin und Produktionsleiter (Wartungsplanung). "
        + "Nächster Schritt: get_maintenance_due für die Termine, get_order_progress für die Aufträge.")]
    public async Task<CallToolResult> CheckMaintenanceConflicts(
        [Description("Halle: A. Standard: Halle A.")] string? hallId = null,
        [Description("Zeitraum: heute, heute-morgen, 48 h, Woche, KW 43. Standard: heute.")] string? zeitraum = null,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanPlanMaintenance)
        {
            var role = (await caller.GetRoleAsync(cancellationToken)).Title;
            throw caller.Deny($"Keine Berechtigung: Die Wartungsplanung (Konflikte und Bündelung) ist Hallenleiterin und Produktionsleiter vorbehalten, nicht der Rolle {role}. "
                + "get_maintenance_due zeigt die fälligen Wartungen.");
        }

        var resolved = await scopes.ResolveAsync(hallId, cancellationToken);
        if (!resolved.HasHallA) throw new McpException("Wartungen gibt es für Halle A.");

        var now = await session.NowAsync(cancellationToken);
        var (from, to, label) = Horizon(zeitraum, null, null, now, "heute");
        var ids = resolved.HallAIds.ToList();
        var items = await maintenance.ItemsAsync(ids, from, to, now, includeForecast: true, assembly: null, onlyOverdue: false, cancellationToken);
        var moulds = await maintenance.MouldsAsync(now, cancellationToken);
        var setups = await maintenance.SetupWindowsAsync(ids, now, cancellationToken);
        var running = (await orders.GetAsync(now, ids, [], null, cancellationToken)).Where(o => o.Order.State == OrderState.Running).ToDictionary(o => o.Order.StationId!);
        var reach = await material.GetAsync(resolved.Stations, now);
        session.Sources.Add("Wartungs-DB", now);
        session.Sources.Add("Auftragsdaten", now);

        var conflicts = new List<object>();
        var proposals = new List<object>();
        foreach (var item in items)
        {
            var station = item.Plan.StationId;
            running.TryGetValue(station, out var order);
            if (order?.ExpectedEndUtc is { } end && !item.AllDay && !item.Overdue && item.StartUtc < end)
            {
                conflicts.Add(new
                {
                    Station = station,
                    Wartung = item.Plan.Title,
                    Termin = Fmt.Stamp(item.StartUtc),
                    Auftrag = order.Order.Id,
                    AuftragFertigCa = Fmt.Stamp(end),
                    Text = $"Auftrag {order.Order.Id} läuft bis ca. {Fmt.Time(end)}, die Wartung beginnt um {Fmt.Time(item.StartUtc)}: Konflikt.",
                    Bewertung = Rating.Warn,
                });
            }

            if (!item.Bundleable) continue;

            setups.TryGetValue(station, out var setup);
            var fits = setup is not null && item.StartUtc >= setup.OrderEndUtc.AddMinutes(-5) && item.StartUtc <= setup.SetupEndUtc;
            moulds.TryGetValue(station, out var mould);
            double? wearAtSetup = mould is not null && item.Plan.MouldId == mould.MouldId
                ? MaintenanceService.Wear(mould, now, setup?.OrderEndUtc.AddMinutes(10) ?? item.StartUtc).WearAt
                : null;
            var mixture = reach.FirstOrDefault(r => r.StationId == station && r.Material.StartsWith("Mischung", StringComparison.Ordinal));
            proposals.Add(new
            {
                Station = station,
                Wartung = item.Plan.Title,
                BuendelnMit = item.BundleWith,
                Zeitpunkt = item.AllDay ? Fmt.Day(item.DueDay) : Fmt.Stamp(item.StartUtc),
                Ruestfenster = setup is null || item.BundleWith?.StartsWith("Rüsten", StringComparison.Ordinal) != true ? null : new
                {
                    Auftrag = setup.RunningOrderId,
                    AuftragFertigCa = Fmt.Time(setup.OrderEndUtc),
                    RuestenBisCa = Fmt.Time(setup.SetupEndUtc),
                    Folgeauftrag = setup.NextOrderId,
                    Artikel = setup.NextArticle,
                },
                PasstInRuestfenster = fits,
                MischungVerarbeitbarBis = mixture?.ProcessableUntilUtc is { } until ? Fmt.Time(until) : null,
                VerschleissZumRuestenProzent = wearAtSetup is { } w ? Fmt.Percent(w) : (int?)null,
                ZusaetzlicherStillstandEinzelnMin = item.StopMinutes,
                ZusaetzlicherStillstandGebuendeltMin = item.StopMinutesBundled,
                Text = fits
                    ? $"{item.Plan.Title} passt in das Rüsten nach Auftrag {setup!.RunningOrderId} (ca. {Fmt.Time(setup.OrderEndUtc)} bis {Fmt.Time(setup.SetupEndUtc)}): kein zusätzlicher Stillstand."
                    : $"{item.Plan.Title} lässt sich mit '{item.BundleWith}' bündeln: kein zusätzlicher Stillstand.",
            });
        }

        var perStation = items.GroupBy(i => i.Plan.StationId).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new
        {
            Station = g.Key,
            EinzelnMin = g.Sum(i => i.StopMinutes),
            GebuendeltMin = g.Sum(i => i.StopMinutesBundled),
        });
        var einzeln = items.Sum(i => i.StopMinutes);
        var gebuendelt = items.Sum(i => i.StopMinutesBundled);
        return await new Reply("check_maintenance_conflicts")
            .Add("bereich", resolved.Label)
            .Add("zeitraum", new { Bezeichnung = label, Von = Fmt.Stamp(from), Bis = Fmt.Stamp(to) })
            .Add("wartungen", items.Count)
            .Add("ueberfaellig", items.Count(i => i.Overdue))
            .Add("konflikte", conflicts)
            .Add("keine_konflikte", conflicts.Count == 0)
            .Add("buendelungsvorschlaege", proposals)
            .Add("stillstand", new
            {
                EinzelnMin = einzeln,
                GebuendeltMin = gebuendelt,
                ErsparnisMin = einzeln - gebuendelt,
                JeTag = items.GroupBy(i => i.DueDay).OrderBy(g => g.Key).Select(g => new { Tag = Fmt.Day(g.Key), EinzelnMin = g.Sum(i => i.StopMinutes), GebuendeltMin = g.Sum(i => i.StopMinutesBundled) }),
                Text = $"Zusätzlicher Stillstand durch Wartungen: einzeln {einzeln} min, gebündelt {gebuendelt} min.",
                JeStation = perStation,
            })
            .BuildAsync(session, cancellationToken);
    }

    private object ItemRow(MaintenanceItem item, IReadOnlyDictionary<string, MouldReading> moulds, DateTimeOffset now)
    {
        var plan = item.Plan;
        object? form = null;
        if (plan.MouldId is { } mouldId && moulds.TryGetValue(plan.StationId, out var mould) && mould.MouldId == mouldId)
        {
            var (wearNow, wearAtDue, limit) = MaintenanceService.Wear(mould, now, item.StartUtc);
            var forecast = plan.IsForecast ? maintenance.ForecastDay(mould, now) : null;
            // Die Hochrechnung mit dem aktuellen Hubtempo gilt nur innerhalb des laufenden Produktionstags (danach stehen Pausen und Wochenenden).
            var sameDay = !plan.IsForecast && item.StartUtc < calendar.DayPeriodOf(now).EndUtc;
            form = new
            {
                Form = mould.MouldId,
                Huebe = mould.Strokes,
                Standzeitgrenze = mould.StrokeLimit,
                VerschleissProzent = Fmt.Percent(wearNow),
                VerschleissZurFaelligkeitProzent = sameDay && item.StartUtc > now ? Fmt.Percent(wearAtDue) : (int?)null,
                GrenzeErreichtCa = sameDay && limit is { } l ? Fmt.Stamp(l) : null,
                PrognoseGrenzeTag = forecast is { } day ? Fmt.Day(day) : null,
                HuebeProTag = plan.IsForecast ? mould.AverageStrokesPerDay : (int?)null,
            };
        }

        var status = item.Overdue ? $"überfällig seit {item.OverdueDays} Tag{(item.OverdueDays == 1 ? string.Empty : "en")}"
            : plan.IsForecast ? "Prognose"
            : plan.WindowEndUtc is not null ? "Wartungsfenster"
            : item.DueDay == calendar.ProductionDayOf(now) ? "heute fällig"
            : "geplant";
        return new
        {
            plan.Id,
            Station = plan.StationId,
            Titel = plan.Title,
            Art = KindWord(plan.Kind),
            Faellig = plan.WindowEndUtc is { } windowEnd ? $"{Fmt.Day(item.DueDay)} bis {Fmt.Day(windowEnd.AddMinutes(-1))}" : item.AllDay ? $"{Fmt.Day(item.DueDay)} (ganztägig)" : Fmt.Stamp(item.StartUtc),
            Status = status,
            Ueberfaellig = item.Overdue,
            UeberfaelligTage = item.Overdue ? item.OverdueDays : (int?)null,
            Sicherheitsrelevant = plan.IsSafetyRelevant,
            ArbeitsdauerMin = plan.DurationMinutes,
            StillstandEinzelnMin = item.StopMinutes,
            StillstandGebuendeltMin = item.StopMinutesBundled,
            BuendelbarMit = item.BundleWith,
            Auftrag = plan.WorkOrder,
            Hinweis = plan.Hint,
            Form = form,
            Bewertung = item.Overdue ? Rating.Crit : plan.IsForecast ? Rating.Warn : Rating.Info,
            BewertungText = RatingText.Word(item.Overdue ? Rating.Crit : plan.IsForecast ? Rating.Warn : Rating.Info),
        };
    }

    private (DateTimeOffset From, DateTimeOffset To, string Label) Horizon(string? horizont, string? von, string? bis, DateTimeOffset now, string defaultText)
    {
        var range = ranges.Resolve(horizont, now, von, bis, defaultText, forward: true);
        if (!string.IsNullOrWhiteSpace(von) || !string.IsNullOrWhiteSpace(bis)) return (range.StartUtc, range.EndUtc, range.Label);

        // Ein Horizont, der die Gegenwart enthält, beginnt jetzt: Vergangenes ist erledigt oder überfällig.
        var from = range.StartUtc < now && range.EndUtc > now ? now : range.StartUtc;
        return (from, range.EndUtc, range.Label);
    }

    private static string KindWord(MaintenanceKind kind) => kind switch
    {
        MaintenanceKind.SafetyInspection => "Sicherheitsprüfung",
        MaintenanceKind.StrokeCounter => "Hubzähler",
        MaintenanceKind.ConditionBased => "zustandsbasiert",
        MaintenanceKind.MeasuringEquipment => "Prüfmittel",
        MaintenanceKind.Scheduled => "planmäßig",
        MaintenanceKind.TimeBased => "zeitbasiert",
        MaintenanceKind.Forecast => "Prognose",
        MaintenanceKind.Inspection => "Prüfung",
        MaintenanceKind.Revision => "Revision",
        _ => kind.ToString(),
    };

    private static string Norm(string? text) =>
        (text ?? string.Empty).Trim().ToLowerInvariant().Replace("ü", "ue", StringComparison.Ordinal).Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ö", "oe", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal);
}
