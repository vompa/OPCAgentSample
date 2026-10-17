using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Live;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Tools für den aktuellen Zustand der Anlage: Hallenübersicht, Maschinenstatus, Alarme, Materialreichweite, Aufträge und
/// Prozessverläufe. Alle Tools sind nur lesend; Kennzahlen, Ampeln und Reichweiten rechnet der Server, nie das Modell.
/// </summary>
[McpServerToolType]
public sealed class LiveTools(
    McpSession session,
    ScopeResolver scopes,
    TimeRangeResolver ranges,
    StationStateStore states,
    PlantQueries queries,
    MaterialReachService material,
    OrderService orders,
    KpiAccess kpis,
    MasterDataStore masterData,
    Microsoft.Extensions.Options.IOptions<McpOptions> options)
{
    [McpServerTool(Name = "get_hall_overview", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Liefert den aktuellen Zustand aller Stationen einer Halle: Betriebszustand und Dauer, Auftrag, Kennzahl (Ist/Soll bzw. "
        + "Belegung), Ampel (Läuft, Warnung, Problem) mit Befunden und Kurztext, dazu das Hallenklima und – wenn mehrere Stationen "
        + "zusammenhängen – die Ursachenkette (z. B. RT-04 → OF-02 → VP-02: nur das Problem am Anfang der Kette ist eigenständig). "
        + "Das ist der richtige erste Schritt für 'Wie ist die Lage?'. Mit hallId 'Werk' (nur Produktionsleiter) kommen die Hallen A bis C "
        + "mit ihrer OEE der Kalenderwoche. Nächster Schritt: get_machine_status für Details einer Station, get_active_alarms für Meldungen.")]
    public async Task<CallToolResult> GetHallOverview(
        [Description("Halle: A, B, C oder Werk. Standard: Halle A (Produktionsleiter: Werk).")] string? hallId = null,
        [Description("Nur Stationen dieses Typs: Rundtisch, Ofen, Verpackung oder Puffer.")] string? stationType = null,
        CancellationToken cancellationToken = default)
    {
        var scope = await scopes.ResolveAsync(hallId, cancellationToken);
        var area = ScopeResolver.ParseArea(stationType);
        var now = await session.NowAsync(cancellationToken);
        var reply = new Reply("get_hall_overview").Add("bereich", scope.Label);

        if (scope.HasHallA)
        {
            var result = await states.GetAsync(cancellationToken);
            session.Sources.Add(result.SourceLabel, result.Snapshot.SourceUntilUtc);
            var wanted = scope.Stations.Where(s => area is null || s.Area == area).ToDictionary(s => s.Id);
            var selected = result.Snapshot.Stations.Where(s => wanted.ContainsKey(s.StationId)).ToList();
            var production = selected.Where(s => s.Area != Area.Buffer).ToList();
            var problems = production.Count(s => s.Light == TrafficLight.Problem);
            var warnings = production.Count(s => s.Light == TrafficLight.Warning);
            var attention = production.Where(s => s.Light != TrafficLight.Running).OrderByDescending(s => s.Light).Select(s => s.StationId).ToList();
            reply
                .Add("zusammenfassung", new
                {
                    Stationen = production.Count,
                    Problem = problems,
                    Warnung = warnings,
                    Laeuft = production.Count - problems - warnings,
                    Text = production.Count == 0
                        ? "Keine Stationen im gewählten Bereich."
                        : $"{problems} Problem und {warnings} Warnung bei {production.Count} Stationen"
                          + (problems > 0 ? $"; kritisch nur: {string.Join(", ", production.Where(s => s.Light == TrafficLight.Problem).Select(s => s.StationId))}." : "."),
                    Aufmerksamkeit = attention,
                })
                .Add("stationen", selected.Select(s => Views.StationRow(s, wanted[s.StationId], now)))
                .Add("ursachenketten", Views.Chains(result.Snapshot.Stations))
                .Add("klima", Climate(result.Snapshot.Climate));
        }

        if (scope.CounterStations.Count > 0 || scope.IsWholePlant)
        {
            reply.Add("hallen", await HallsAsync(scope, now, cancellationToken));
        }

        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_machine_status", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Liefert alles zu einer Station: Zustand und Dauer, Auftrag und Charge, Prozesswerte mit Soll, Toleranz und Bewertung "
        + "(Taktzeit, Presskraftaufbau, Füllgewicht, Formstandzeit), Materialreichweiten, bei Öfen Belegung und Zuordnungsregel, "
        + "aktive Alarme und die Störungen der laufenden Schicht. Beantwortet 'Warum hängt/steht Station X?'. "
        + "Nächster Schritt: get_process_trend für den Verlauf eines Prozesswerts, get_maintenance_due für Wartung der Station.")]
    public async Task<CallToolResult> GetMachineStatus(
        [Description("Station: RT-01 bis RT-04, OF-01, OF-02, PU-OFEN, VP-01 oder VP-02.")] string machineId,
        CancellationToken cancellationToken = default)
    {
        var scope = await scopes.ResolveAsync(machineId, cancellationToken);
        if (!scope.IsSingleStation) throw new McpException("machineId muss genau eine Station benennen (z. B. RT-04). Für mehrere Stationen: get_hall_overview.");

        var station = scope.Stations[0];
        var now = await session.NowAsync(cancellationToken);
        var snapshot = await states.GetAsync(cancellationToken);
        session.Sources.Add(snapshot.SourceLabel, snapshot.Snapshot.SourceUntilUtc);
        var state = snapshot.Snapshot.Find(station.Id) ?? throw new McpException($"Zu {station.Id} liegen noch keine Daten vor.");
        var shift = Calendar(now);

        var alarms = (await queries.AlarmsAsync(now.AddDays(-1), now.AddMinutes(1), [station.Id]))
            .Where(a => a.ClearedUtc is null).ToList();
        var losses = (await queries.LossEventsAsync(shift.StartUtc, now, [station.Id]))
            .Where(l => l.Category is LossCategory.Breakdown or LossCategory.MinorStop or LossCategory.MaterialWait or LossCategory.UpstreamWait).ToList();
        session.Sources.Add("Redis");

        var reply = new Reply("get_machine_status")
            .Add("station", Views.StationRow(state, station, now))
            .Add("prozesswerte", await ProcessValuesAsync(state, station, now, cancellationToken))
            .Add("aktive_alarme", alarms.Select(a => AlarmRow(a, now)))
            .Add("stoerungen_schicht", new
            {
                Anzahl = losses.Count,
                StillstandMin = Fmt.Minutes(TimeSpan.FromTicks(losses.Where(l => l.IsStandstill).Sum(l => (Min(l.EndUtc ?? now, now) - Max(l.StartUtc, shift.StartUtc)).Ticks))),
                Eintraege = losses.Select(l => new
                {
                    Von = Fmt.Time(l.StartUtc),
                    Bis = l.EndUtc is { } end ? Fmt.Time(end) : "offen",
                    l.Reason,
                    l.Cause,
                    l.Assembly,
                }),
            });

        if (station.Type is StationType.RotaryTable or StationType.Packaging)
        {
            var reach = await material.GetAsync([station], now);
            var running = (await orders.GetAsync(now, [station.Id], [], null, cancellationToken)).FirstOrDefault(o => o.Order.State == OrderState.Running);
            reply.Add("material", reach.Select(MaterialRow))
                .Add("auftrag_laufend", running is null ? null : OrderRow(running));
        }

        if (station.Type == StationType.Oven) reply.Add("ofen", await OvenAsync(state, station, now, cancellationToken));
        if (station.Type == StationType.Buffer) reply.Add("puffer", new { Platten = state.Plates, MittlereWartezeitMin = state.MeanWaitMinutes is { } w ? Fmt.Round(w, 0) : (double?)null });
        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_active_alarms", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Listet Alarme und Meldungen: ohne zeitraum nur die gerade aktiven (Störung, Zustandswarnung, Prozesswarnung), "
        + "mit zeitraum alle, die in diesem Zeitraum aktiv waren, und zusätzlich die Liste 'Störungen und Alarme' mit Anzahl und "
        + "Stillstand in Minuten. Die Rolle bestimmt den Meldungsfilter (Schichtleiter alle, Hallenleiterin ab Warnung, Produktionsleiter "
        + "nur Eskalationen). Nächster Schritt: get_machine_status der betroffenen Station oder get_downtime_pareto für den Stillstand.")]
    public async Task<CallToolResult> GetActiveAlarms(
        [Description("Bereich: Halle A, Bereich oder Station. Standard: Halle A.")] string? scope = null,
        [Description("Mindestschwere: info, niedrig, mittel oder hoch. Standard: alle.")] string? minSeverity = null,
        [Description("Zeitraum, z. B. 'seit 06:00', 'aktuelle Schicht', 'heute', 'gestern'. Ohne Angabe: nur aktive Alarme.")] string? zeitraum = null,
        [Description("true = auch quittierte Alarme. Standard: false.")] bool inklusiveQuittiert = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var minimum = ParseSeverity(minSeverity);
        var rank = await session.Caller.MinimumMessageRankAsync(cancellationToken);
        var ids = resolved.HallAIds.ToList();
        if (ids.Count == 0) throw new McpException("Alarme gibt es nur in Halle A (die Hallen B und C liefern nur Zähler).");

        ResolvedRange? range = string.IsNullOrWhiteSpace(zeitraum) ? null : ranges.Resolve(zeitraum, now);
        var from = range?.StartUtc ?? now.AddDays(-7);
        var to = range?.EffectiveEnd(now) ?? now;
        var all = await queries.AlarmsAsync(from, to.AddMinutes(1), ids);
        session.Sources.Add("Redis");

        var visible = all
            .Where(a => range is not null || a.ClearedUtc is null)
            .Where(a => Views.AlarmRank(a.Kind) >= rank)
            .Where(a => minimum is null || a.Severity >= minimum)
            .Where(a => inklusiveQuittiert || a.AcknowledgedUtc is null)
            .OrderByDescending(a => a.Severity).ThenBy(a => a.ActiveSinceUtc)
            .ToList();
        var shown = Limit(visible, "Alarme");

        var reply = new Reply("get_active_alarms").Add("bereich", resolved.Label);
        if (range is not null) reply.Add("zeitraum", Views.RangeInfo(range, now));
        reply.Add("meldungsfilter", (await session.Caller.GetRoleAsync(cancellationToken)).MessageFilter)
            .Add("anzahl", visible.Count)
            .Add("alarme", shown.Select(a => AlarmRow(a, now)));

        if (range is not null)
        {
            var losses = await queries.LossEventsAsync(range.StartUtc, to, ids);
            var entries = EventKpis.Disruptions(losses, all, new TimeWindow(range.StartUtc, range.EndUtc), now);
            reply.Add("stoerungen_und_alarme", new
            {
                Anzahl = entries.Count,
                StillstandMin = Fmt.Minutes(TimeSpan.FromTicks(entries.Sum(e => e.StandstillInWindow.Ticks))),
                OffeneWarnungen = entries.Count(e => e.IsAlarmOnly && e.EndUtc is null),
                Eintraege = entries.Select(e => new
                {
                    Station = e.StationId,
                    Beginn = Fmt.Stamp(e.StartUtc),
                    Ende = e.EndUtc is { } end ? Fmt.Stamp(end) : null,
                    Text = e.Text,
                    StillstandMin = Fmt.Minutes(e.StandstillInWindow),
                    NurAlarm = e.IsAlarmOnly,
                    Offen = e.EndUtc is null,
                }),
            });
        }

        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_material_reach", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Berechnet, wie lange das Material an den Stationen reicht: Mischung (Füllstand und Verarbeitbarkeit), Gewebe, Etiketten, "
        + "Kartons. Die Reichweite ist die kleinere von Füllstand/Verbrauch und – bei Mischungen – der Zeit bis 'verarbeitbar bis'; "
        + "ein reines Füllstandsdashboard würde die begrenzende Verarbeitbarkeit übersehen. Ergebnis: je Material Füllstand, Verbrauch je Minute, "
        + "beide Reichweiten, Bewertung (< 60 min Warnung, < 15 min Problem) und die kürzeste Reichweite je Station. "
        + "Nächster Schritt: get_order_progress, um zu sehen, welcher Auftrag betroffen ist.")]
    public async Task<CallToolResult> GetMaterialReach(
        [Description("Bereich: Halle A, Pressen, Verpackung oder Station. Standard: Halle A.")] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var rows = await material.GetAsync(resolved.Stations, now);
        session.Sources.Add("Redis (Minutenwerte)", now);
        var shortest = MaterialReachService.Shortest(rows).Values.OrderBy(r => r.ReachMinutes).ToList();
        return await new Reply("get_material_reach")
            .Add("bereich", resolved.Label)
            .Add("zuerst", shortest.Count == 0 ? null : MaterialRow(shortest[0]))
            .Add("kuerzeste_je_station", shortest.Select(MaterialRow))
            .Add("materialien", rows.OrderBy(r => r.StationId, StringComparer.Ordinal).ThenBy(r => r.ReachMinutes ?? double.MaxValue).Select(MaterialRow))
            .BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_order_progress", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Liefert den Fortschritt von Fertigungsaufträgen: gefertigte Menge und Prozent, Tempo, voraussichtliches Ende, Termin, "
        + "verfügbare gegenüber benötigter Produktionszeit und eine Bewertung (im Plan, knapp, gefährdet) mit Begründung; Aufträge ohne "
        + "Station (Halle B/C) teilen sich die Hallenkapazität und werden gemeinsam bewertet. Je Station außerdem Charge und Folgeauftrag. "
        + "Mit stufe 'Ofen → Verpackung' kommt der Materialfluss (Puffer, Ofenbelegung mit Zuordnungsregel, Verpackung). "
        + "Nächster Schritt: get_oee oder compare, um die Ursache zu klären.")]
    public async Task<CallToolResult> GetOrderProgress(
        [Description("Station oder Liste von Stationen, z. B. RT-03 oder [\"RT-01\",\"RT-02\"].")] JsonElement? machineId = null,
        [Description("Auftragsnummer, z. B. 4711.")] string? orderId = null,
        [Description("Bereich: Halle A, Halle B, Werk oder Station. Standard: die Sicht der Rolle.")] JsonElement? scope = null,
        [Description("'gefährdet' = nur Aufträge mit Warnung oder kritischer Bewertung.")] string? filter = null,
        [Description("'Ofen → Verpackung' oder 'Pressen → Ofen' liefert zusätzlich den Materialfluss zwischen den Stufen.")] string? stufe = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(machineId is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } ? machineId : scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var counterHalls = resolved.CounterStations.Select(id => id == "HALLE-B" ? PlantLayout.HallB : PlantLayout.HallC).ToList();
        var views = await orders.GetAsync(now, [.. resolved.HallAIds], counterHalls, orderId, cancellationToken);
        session.Sources.Add("Auftragsdaten", now);

        var risky = filter is not null && Normalize(filter).StartsWith("gefaehrd", StringComparison.Ordinal);
        var selected = views.Where(v => !risky || v.Rating != Rating.Ok).ToList();
        var reply = new Reply("get_order_progress")
            .Add("bereich", resolved.Label)
            .Add("zusammenfassung", new
            {
                Gefaehrdet = views.Count(v => v.Rating == Rating.Crit),
                Knapp = views.Count(v => v.Rating == Rating.Warn),
                ImPlan = views.Count(v => v.Rating == Rating.Ok),
            })
            .Add("auftraege", Limit(selected, "Aufträge").Select(OrderRow));

        var stations = resolved.Stations.Where(s => s.Type == StationType.RotaryTable).Select(s => s.Id).ToList();
        if (stations.Count > 0 && resolved.Stations.Count <= 4)
        {
            var batches = await queries.LatestBatchesAsync(stations, now);
            session.Sources.Add("Redis");
            reply.Add("chargen", batches.Select(b => new
            {
                Station = b.Key,
                Charge = b.Value.Batch,
                Koernung = b.Value.Grit,
                Folgeauftrag = views.Where(v => v.Order.StationId == b.Key && v.Order.State == OrderState.Planned).Select(v => v.Order.Id).FirstOrDefault(),
            }));
        }

        if (stufe is not null)
        {
            reply.Add("materialfluss", await FlowAsync(now, cancellationToken));
        }

        return await reply.BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_process_trend", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Liefert den verdichteten Verlauf eines Prozesswerts (Taktzeit, Presskraft, Presskraftaufbau, Füllgewicht, Dicke bei Rundtischen; "
        + "Belegung, Temperatur oder Temperaturprofil bei Öfen; Füllstand, Öltemperatur u. a.) mit Mittelwert, Minimum und Maximum je Abschnitt, "
        + "dem letzten Wert (Mittel der letzten 5 Minuten) und einer Driftbewertung: ob der Wert dauerhaft vom Ausgangswert abweicht, ab wann "
        + "(Beginn ca.) und wie stark. Bei Takt und Presskraftaufbau auch den Verlust in Stück bis Schichtende. Mehrere Stationen sind möglich, "
        + "z. B. um eine gemeinsame Ursache zu prüfen. Nächster Schritt: get_condition_warnings oder get_maintenance_due.")]
    public async Task<CallToolResult> GetProcessTrend(
        [Description("Station oder Liste von Stationen, z. B. RT-04 oder [\"RT-01\",\"RT-02\"].")] JsonElement machineId,
        [Description("Variable: Taktzeit, Presskraft, Presskraftaufbau, Füllgewicht, Dicke, Belegung, Temperatur, Temperaturprofil, Füllstand, Öltemperatur ...")] string variable,
        [Description("Zeitraum, z. B. '06:00-jetzt', 'aktuelle Schicht', 'heute', '24 h'. Standard: aktuelle Schicht.")] string? zeitraum = null,
        [Description("Größe der Abschnitte in Minuten (5 bis 1440). Standard: stündlich bis 48 h.")] int? bucketMinutes = null,
        CancellationToken cancellationToken = default)
    {
        var now = await session.NowAsync(cancellationToken);
        var range = ranges.Resolve(zeitraum, now, defaultText: "aktuelle Schicht");
        if (machineId.ValueKind == JsonValueKind.String && Normalize(machineId.GetString() ?? string.Empty) is "klima" or "hallenklima" or "a-klima")
        {
            return await ClimateTrendAsync(variable, range, bucketMinutes, now, cancellationToken);
        }

        var resolved = await scopes.ResolveAsync(machineId, cancellationToken);
        var from = range.StartUtc;
        var to = range.EffectiveEnd(now);
        if (to <= from) throw new McpException("Der Zeitraum liegt noch nicht begonnen oder ist leer.");

        var bucket = TimeSpan.FromMinutes(Math.Clamp(bucketMinutes ?? DefaultBucketMinutes(to - from), 5, 1440));
        var trends = new List<object>();
        var normalized = Normalize(variable);
        foreach (var station in resolved.Stations)
        {
            trends.Add(await TrendAsync(station, variable, normalized, from, to, bucket, now, cancellationToken));
        }

        if (trends.Count == 0) throw new McpException("Für Hallen B und C gibt es keine Prozesswerte.");
        session.Sources.Add("Redis (Zeitreihen)", now);
        return await new Reply("get_process_trend")
            .Add("zeitraum", Views.RangeInfo(range, now))
            .Add("abschnitt_min", bucket.TotalMinutes)
            .Add("verlaeufe", trends)
            .BuildAsync(session, cancellationToken);
    }

    [McpServerTool(Name = "get_condition_warnings", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Listet zustandsbasierte Auffälligkeiten, die noch keinen Stillstand verursachen, aber Handlungsbedarf zeigen: offene Zustandswarnungen "
        + "(z. B. Presskraftaufbau an RT-04), Formstandzeit ab 90 %, Füllgewicht außerhalb der Warngrenze, Taktzeit über Soll, knappes Material, "
        + "volle Öfen. Je Eintrag Station, Art, Wert, Bewertung und seit wann. Nächster Schritt: get_maintenance_due für passende Wartungstermine "
        + "oder get_process_trend für den Verlauf.")]
    public async Task<CallToolResult> GetConditionWarnings(
        [Description("Bereich: Halle A, Bereich oder Station. Standard: Halle A.")] string? scope = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await scopes.ResolveAsync(scope, cancellationToken);
        var now = await session.NowAsync(cancellationToken);
        var snapshot = await states.GetAsync(cancellationToken);
        session.Sources.Add(snapshot.SourceLabel, snapshot.Snapshot.SourceUntilUtc);
        var ids = resolved.HallAIds.ToList();
        var alarms = (await queries.AlarmsAsync(now.AddDays(-1), now.AddMinutes(1), ids))
            .Where(a => a.Kind is AlarmKind.ConditionWarning or AlarmKind.ProcessWarning && a.ClearedUtc is null).ToList();
        session.Sources.Add("Redis");

        var entries = new List<(string Station, int Order, object Row)>();
        foreach (var alarm in alarms)
        {
            entries.Add((alarm.StationId, 0, new
            {
                Station = alarm.StationId,
                Art = Views.AlarmKindWord(alarm.Kind),
                Baugruppe = alarm.Assembly,
                alarm.Text,
                Seit = Fmt.Stamp(alarm.ActiveSinceUtc),
                Bewertung = Views.AlarmRating(alarm.Kind),
                BewertungText = RatingText.Word(Views.AlarmRating(alarm.Kind)),
            }));
        }

        foreach (var state in snapshot.Snapshot.Stations.Where(s => ids.Contains(s.StationId)))
        {
            foreach (var finding in state.Findings.Where(f => f.Light != TrafficLight.Running && !f.Text.StartsWith("Ist/Soll", StringComparison.Ordinal)))
            {
                entries.Add((state.StationId, 1, new
                {
                    Station = state.StationId,
                    Art = finding.Text,
                    Text = FindingText(state, finding),
                    Seit = (string?)null,
                    Bewertung = RatingText.From(finding.Light),
                    BewertungText = RatingText.Word(RatingText.From(finding.Light)),
                }));
            }
        }

        return await new Reply("get_condition_warnings")
            .Add("bereich", resolved.Label)
            .Add("anzahl", entries.Count)
            .Add("auffaelligkeiten", Limit(entries.OrderBy(e => e.Station, StringComparer.Ordinal).ThenBy(e => e.Order).Select(e => e.Row).ToList(), "Auffälligkeiten"))
            .BuildAsync(session, cancellationToken);
    }

    // ---- Hilfen ---------------------------------------------------------------------------------------------

    /// <summary>Verlauf des Hallenklimas (Gegenhypothese bei Qualitätsabweichungen): Temperatur oder Luftfeuchte in Halle A.</summary>
    private async Task<CallToolResult> ClimateTrendAsync(string variable, ResolvedRange range, int? bucketMinutes, DateTimeOffset now, CancellationToken ct)
    {
        var humidity = Normalize(variable).StartsWith("luftfeuchte", StringComparison.Ordinal);
        var name = humidity ? MinuteVariables.Humidity : MinuteVariables.Temperature;
        var from = range.StartUtc;
        var to = range.EffectiveEnd(now);
        var bucket = TimeSpan.FromMinutes(Math.Clamp(bucketMinutes ?? DefaultBucketMinutes(to - from), 5, 1440));
        var buckets = await queries.MinuteBucketsAsync(MinuteVariables.HallAClimateSource, name, from, to, bucket, ranges.FloorToHour(from));
        session.Sources.Add("Redis (Zeitreihen)", to);
        return await new Reply("get_process_trend")
            .Add("zeitraum", Views.RangeInfo(range, now))
            .Add("abschnitt_min", bucket.TotalMinutes)
            .Add("verlaeufe", new[]
            {
                new
                {
                    Station = "Hallenklima A",
                    Variable = humidity ? "Luftfeuchte" : "Temperatur",
                    Einheit = humidity ? "% rF" : "°C",
                    Verlauf = buckets.Select(b => BucketRow(b, from, 1, 1)).ToList(),
                    Min = buckets.Count > 0 ? Fmt.Round(buckets.Min(b => b.Min), 1) : (double?)null,
                    Max = buckets.Count > 0 ? Fmt.Round(buckets.Max(b => b.Max), 1) : (double?)null,
                },
            })
            .BuildAsync(session, ct);
    }

    private static string FindingText(StationState state, StationFinding finding) => finding.Text switch
    {
        "Formstandzeit" => $"Form {state.MouldId} bei {Fmt.Percent(state.MouldWear ?? 0)} % der Standzeit"
            + (state.MouldLimitUtc is { } limit ? $", Grenze ca. {Fmt.Stamp(limit)}" : string.Empty),
        "Füllgewicht außerhalb Warngrenze" =>
            $"Füllgewicht Ø {Views.Num(state.FillWeightMeanGrams ?? 0)} g (Soll {Views.Num(state.FillWeightTargetGrams ?? 0, 0)} g)",
        "Taktzeit über Soll" =>
            $"Taktzeit Ø {Views.Num(state.CycleSeconds ?? 0)} s (Soll {Views.Num(state.TargetCycleSeconds ?? 0)} s)",
        "Materialreichweite" => $"{state.ReachMaterial} noch {Fmt.Round(state.ReachMinutes ?? 0, 0)} min",
        "Ofen voll belegt" => $"Belegung {Fmt.Percent(state.Occupancy ?? 0)} %",
        _ => finding.Text,
    };

    private async Task<object> HallsAsync(ResolvedScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var range = ranges.Resolve("diese Woche", now);
        var data = await kpis.GetAsync(range, withCounters: true, ct);
        var halls = new List<object>();
        if (scope.HasHallA)
        {
            var oee = data.Oee(PlantLayout.HallAProductionStations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id));
            halls.Add(new { Halle = "A", OeeProzent = OeeResult.Percent(oee.Oee), Bezeichnung = $"OEE {TimeRangeResolver.WeekLabel(range.Periods[0])}" });
        }

        foreach (var (hall, station) in scope.CounterStations.Select(id => (Hall: id == "HALLE-B" ? "B" : "C", Station: id)))
        {
            var oee = data.Oee([station]);
            halls.Add(new
            {
                Halle = hall,
                OeeProzent = data.Counters.ContainsKey(station) ? OeeResult.Percent(oee.Oee) : (int?)null,
                Bezeichnung = $"OEE {TimeRangeResolver.WeekLabel(range.Periods[0])}",
                Hinweis = "Hallen B und C liefern nur Zähler (Tageskennzahlen).",
            });
        }

        return halls;
    }

    private async Task<object?> ProcessValuesAsync(StationState state, Station station, DateTimeOffset now, CancellationToken ct)
    {
        if (station.Type != StationType.RotaryTable) return null;

        var article = state.ArticleId is null ? null : (await masterData.GetAsync(ct)).Articles.GetValueOrDefault(state.ArticleId);
        var limits = TrafficLightThresholds.Default;
        var values = new List<object>();
        if (state.CycleSeconds is { } cycle && state.TargetCycleSeconds is { } target)
        {
            var deviation = (cycle / target) - 1;
            values.Add(new
            {
                Name = "Taktzeit (Mittel 5 min)",
                Wert = Fmt.Round(cycle, 2),
                Einheit = "s",
                Soll = Fmt.Round(target, 2),
                AbweichungProzent = Fmt.Round(deviation * 100, 1),
                Bewertung = deviation > limits.CycleProblemAbove ? Rating.Crit : deviation > limits.CycleWarningAbove ? Rating.Warn : Rating.Ok,
            });
        }

        if (state.BuildUpSeconds is { } buildUp)
        {
            var buildUpTarget = options.Value.ProcessLimits.BuildUpTargetSeconds;
            values.Add(new
            {
                Name = "Presskraftaufbau (Mittel 5 min)",
                Wert = Fmt.Round(buildUp, 2),
                Einheit = "s",
                Soll = buildUpTarget,
                AbweichungProzent = Fmt.Round((buildUp / buildUpTarget - 1) * 100, 1),
                Bewertung = buildUp >= buildUpTarget * options.Value.ProcessLimits.BuildUpWarningRatio ? Rating.Warn : Rating.Ok,
            });
        }

        if (state.FillWeightMeanGrams is { } fill && state.FillWeightTargetGrams is { } fillTarget)
        {
            var deviation = Math.Abs(fill - fillTarget);
            values.Add(new
            {
                Name = "Füllgewicht (Mittel 15 min)",
                Wert = Fmt.Round(fill, 1),
                Einheit = "g",
                Soll = Fmt.Round(fillTarget, 1),
                Toleranz = article is null ? (double?)null : Fmt.Round(article.FillWeightToleranceGrams, 1),
                AbweichungG = Fmt.Round(fill - fillTarget, 1),
                Bewertung = deviation >= limits.FillWeightProblemGrams ? Rating.Crit : deviation >= limits.FillWeightWarningGrams ? Rating.Warn : Rating.Ok,
            });
        }

        if (state.MouldId is not null)
        {
            values.Add(new
            {
                Name = $"Form {state.MouldId} Standzeit",
                Wert = Fmt.Percent(state.MouldWear ?? 0),
                Einheit = "%",
                Soll = 100,
                GrenzeErreichtCa = state.MouldLimitUtc is { } limit && (state.MouldWear ?? 0) >= limits.MouldWarningAt ? Fmt.Stamp(limit) : null,
                Bewertung = (state.MouldWear ?? 0) >= limits.MouldProblemAt ? Rating.Crit : (state.MouldWear ?? 0) >= limits.MouldWarningAt ? Rating.Warn : Rating.Ok,
            });
        }

        return values;
    }

    private async Task<object> OvenAsync(StationState state, Station station, DateTimeOffset now, CancellationToken ct)
    {
        var charges = await queries.OvenChargesAsync(now.AddHours(-24), now.AddDays(1), station.Id, null);
        var diameters = charges.Select(c => c.DiameterMm).Distinct().Order().ToList();
        var next = charges.Where(c => c.UnloadedUtc > now).OrderBy(c => c.UnloadedUtc).FirstOrDefault();
        return new
        {
            BelegungProzent = Fmt.Percent(state.Occupancy ?? 0),
            BelegtePlaetze = state.OccupiedPlaces,
            Plaetze = state.Places,
            Zone2Temperatur = state.ZoneTemperature is { } t ? Fmt.Round(t, 0) : (double?)null,
            NaechsteFreigabe = next is null ? null : Fmt.Time(next.UnloadedUtc),
            Zuordnung = diameters.Count switch
            {
                0 => "keine Chargen in den letzten 24 h",
                1 => $"alle Ø {diameters[0]} (Chargen der letzten 24 h)",
                _ => $"Ø {string.Join(", Ø ", diameters)} (Chargen der letzten 24 h)",
            },
            Hinweis = "Die Zuordnung ist eine Planungsregel, keine technische Grenze: Das Temperaturprofil ist für beide Öfen gleich.",
        };
    }

    private async Task<object> FlowAsync(DateTimeOffset now, CancellationToken ct)
    {
        var snapshot = await states.GetAsync(ct);
        session.Sources.Add(snapshot.SourceLabel, snapshot.Snapshot.SourceUntilUtc);
        var stations = snapshot.Snapshot.Stations;
        var ovens = new List<object>();
        foreach (var oven in stations.Where(s => s.Area == Area.Curing))
        {
            var charges = await queries.OvenChargesAsync(now.AddHours(-24), now.AddDays(1), oven.StationId, null);
            var diameters = charges.Select(c => c.DiameterMm).Distinct().Order().ToList();
            var next = charges.Where(c => c.UnloadedUtc > now).OrderBy(c => c.UnloadedUtc).FirstOrDefault();
            ovens.Add(new
            {
                Id = oven.StationId,
                BelegungProzent = Fmt.Percent(oven.Occupancy ?? 0),
                BelegtePlaetze = oven.OccupiedPlaces,
                Plaetze = oven.Places,
                NaechsteFreigabe = next is null ? null : Fmt.Time(next.UnloadedUtc),
                Zuordnung = diameters.Count == 1 ? $"alle Ø {diameters[0]}" : diameters.Count == 0 ? null : $"Ø {string.Join(", Ø ", diameters)}",
                Ampel = Views.LightWord(oven.Light),
            });
        }

        var buffer = stations.FirstOrDefault(s => s.Area == Area.Buffer);
        return new
        {
            Puffer = buffer is null ? null : new { Platten = buffer.Plates, MittlereWartezeitMin = buffer.MeanWaitMinutes is { } w ? Fmt.Round(w, 0) : (double?)null },
            Oefen = ovens,
            Verpackung = stations.Where(s => s.Area == Area.Packaging).Select(s => new
            {
                Id = s.StationId,
                Zustand = Views.StateWord(s.State),
                Seit = Fmt.Stamp(s.SinceUtc),
                Artikel = s.ArticleLabel,
                IstSollProzent = s.PlanRatio is { } r ? Fmt.Percent(r) : (int?)null,
                Ampel = Views.LightWord(s.Light),
            }),
            Ursachenketten = Views.Chains(stations),
        };
    }

    private async Task<object> TrendAsync(
        Station station, string variable, string normalized, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset now, CancellationToken ct)
    {
        var origin = FloorToHour(from);
        var fine = TimeSpan.FromMinutes(10);
        if (CycleColumns.Of(normalized) is { } column && station.Type == StationType.RotaryTable)
        {
            var buckets = await queries.CycleBucketsAsync(station.Id, column, from, to, bucket, origin);
            var fineBuckets = await queries.CycleBucketsAsync(station.Id, column, from, to, fine, origin);
            var last = (await queries.CycleBucketsAsync(station.Id, column, to.AddMinutes(-5), to, TimeSpan.FromMinutes(5))) is { Count: > 0 } latest ? latest[^1] : null;
            var snapshot = await states.GetAsync(ct);
            var state = snapshot.Snapshot.Find(station.Id);
            return await CycleTrendAsync(station, variable, normalized, buckets, fineBuckets, last, state, from, to, ct);
        }

        var minuteVariable = MinuteVariableOf(normalized, station);
        if (minuteVariable is null)
        {
            throw new McpException($"Die Variable '{variable}' gibt es für {station.Id} nicht. Rundtische: Taktzeit, Presskraft, Presskraftaufbau, "
                + "Füllgewicht, Dicke. Öfen: Belegung, Temperatur, Temperaturprofil. Außerdem Füllstand, Öltemperatur, Systemdruck.");
        }

        var minuteBuckets = await queries.MinuteBucketsAsync(station.Id, minuteVariable.Value.Variable, from, to, bucket, origin);
        var fineMinute = await queries.MinuteBucketsAsync(station.Id, minuteVariable.Value.Variable, from, to, fine, origin);
        var drift = DriftDetector.Detect(fineMinute);
        var scale = minuteVariable.Value.Scale;
        return new
        {
            Station = station.Id,
            Variable = minuteVariable.Value.Label,
            Einheit = minuteVariable.Value.Unit,
            Verlauf = minuteBuckets.Select(b => BucketRow(b, from, scale, 2)),
            Aktuell = minuteBuckets.Count > 0 ? Fmt.Round(minuteBuckets[^1].Mean * scale, 2) : (double?)null,
            Drift = drift is null ? null : DriftRow(drift, scale, 2),
        };
    }

    private async Task<object> CycleTrendAsync(
        Station station, string variable, string normalized, IReadOnlyList<SeriesBucket> buckets, IReadOnlyList<SeriesBucket> fine,
        SeriesBucket? last, StationState? state, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var (unit, target, tolerance, decimals) = await CycleMetaAsync(normalized, state, ct);
        var drift = DriftDetector.Detect(fine);
        var latest = last is null ? (double?)null : Fmt.Round(last.Mean, decimals + 1);
        var forecast = state is null ? null : SpeedLossForecast.Compute(state, normalized is "taktzeit" or "takt" or "presskraftaufbau" or "aufbauzeit" ? drift : null, Calendar(to).EndUtc);
        object? loss = forecast is null ? null : new
        {
            forecast.Text,
            Stueck = Math.Round(forecast.Pieces),
            Seit = Fmt.Time(forecast.OnsetUtc),
            BisSchichtende = Fmt.Time(forecast.ShiftEndUtc),
        };

        return new
        {
            Station = station.Id,
            Variable = variable,
            Einheit = unit,
            Soll = target,
            Toleranz = tolerance,
            Verlauf = buckets.Select(b => BucketRow(b, from, 1, decimals)),
            Aktuell = latest,
            Drift = drift is null ? null : DriftRow(drift, 1, decimals),
            VerlustBisSchichtende = loss,
        };
    }

    private async Task<(string Unit, double? Target, double? Tolerance, int Decimals)> CycleMetaAsync(string normalized, StationState? state, CancellationToken ct)
    {
        switch (normalized)
        {
            case "taktzeit" or "takt" or "zykluszeit":
                return ("s", state?.TargetCycleSeconds is { } t ? Fmt.Round(t, 2) : null, null, 2);
            case "presskraft":
                return ("kN", null, null, 1);
            case "presskraftaufbau" or "presskraftaufbauzeit" or "aufbauzeit" or "kraftaufbau":
                return ("s", options.Value.ProcessLimits.BuildUpTargetSeconds, null, 2);
            case "fuellgewicht" or "gewicht":
                if (state?.ArticleId is { } id)
                {
                    var article = (await masterData.GetAsync(ct)).Articles.GetValueOrDefault(id);
                    return ("g", state.FillWeightTargetGrams is { } g ? Fmt.Round(g, 1) : null, article is null ? null : Fmt.Round(article.FillWeightToleranceGrams, 1), 1);
                }

                return ("g", null, null, 1);
            default:
                return ("mm", null, null, 2);
        }
    }

    private static object BucketRow(SeriesBucket bucket, DateTimeOffset from, double scale, int decimals) => new
    {
        Von = Fmt.Time(bucket.StartUtc < from ? from : bucket.StartUtc),
        Bis = Fmt.Time(bucket.EndUtc),
        Mittelwert = Fmt.Round(bucket.Mean * scale, decimals),
        Min = Fmt.Round(bucket.Min * scale, decimals),
        Max = Fmt.Round(bucket.Max * scale, decimals),
        Anzahl = bucket.Count,
    };

    private static object DriftRow(DriftResult drift, double scale, int decimals) => new
    {
        Erkannt = drift.Detected,
        Richtung = drift.Direction,
        Ausgangswert = Fmt.Round(drift.Baseline * scale, decimals),
        LetzterWert = Fmt.Round(drift.Latest * scale, decimals),
        AenderungProzent = Fmt.Round(drift.ChangePercent, 1),
        BeginnCa = drift.OnsetUtc is { } onset ? Fmt.Time(onset) : null,
        Bewertung = drift.Detected ? Rating.Warn : Rating.Ok,
    };

    private static (string Variable, string Label, string Unit, double Scale)? MinuteVariableOf(string normalized, Station station) => normalized switch
    {
        "belegung" or "ofenbelegung" or "belegteplaetze" => (MinuteVariables.OccupiedPlaces, "Belegte Plätze", "Plätze", 1),
        "temperatur" or "zone2" or "temperaturprofil" or "ofentemperatur" when station.Type == StationType.Oven => (MinuteVariables.Zone2Temperature, "Temperatur Zone 2", "°C", 1),
        "zone1" => (MinuteVariables.Zone1Temperature, "Temperatur Zone 1", "°C", 1),
        "zone3" => (MinuteVariables.Zone3Temperature, "Temperatur Zone 3", "°C", 1),
        "fuellstand" or "mischung" or "mischungsfuellstand" => (MinuteVariables.MixtureLevelKg, "Mischungsfüllstand", "kg", 1),
        "gewebemagazin" or "gewebe" => (MinuteVariables.FabricMagazine, "Gewebemagazin", "Stück", 1),
        "etikettenmagazin" or "etiketten" => (MinuteVariables.LabelMagazine, "Etikettenmagazin", "Stück", 1),
        "kartonmagazin" or "kartons" => (MinuteVariables.CartonMagazine, "Kartonmagazin", "Stück", 1),
        "oeltemperatur" => (MinuteVariables.OilTemperature, "Öltemperatur", "°C", 1),
        "systemdruck" => (MinuteVariables.SystemPressure, "Systemdruck", "bar", 1),
        "formtemperatur" => (MinuteVariables.MouldTemperature, "Formtemperatur", "°C", 1),
        "platten" or "anzahlplatten" => (MinuteVariables.PlateCount, "Anzahl Platten", "Stück", 1),
        "wartezeit" or "mittlerewartezeit" => (MinuteVariables.MeanWaitMinutes, "Mittlere Wartezeit", "min", 1),
        _ => null,
    };

    private ProductionPeriod Calendar(DateTimeOffset at) => ranges.ShiftOf(at);

    private static int DefaultBucketMinutes(TimeSpan span) =>
        span <= TimeSpan.FromHours(48) ? 60 : span <= TimeSpan.FromDays(14) ? 480 : 1440;

    private DateTimeOffset FloorToHour(DateTimeOffset at) => ranges.FloorToHour(at);

    private static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace("ö", "oe", StringComparison.Ordinal).Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ü", "ue", StringComparison.Ordinal).Replace("ß", "ss", StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static AlarmSeverity? ParseSeverity(string? text) => string.IsNullOrWhiteSpace(text)
        ? null
        : Normalize(text) switch
        {
            "info" => AlarmSeverity.Info,
            "niedrig" or "low" or "gering" => AlarmSeverity.Low,
            "mittel" or "medium" => AlarmSeverity.Medium,
            "hoch" or "high" or "kritisch" => AlarmSeverity.High,
            _ => throw new McpException($"Unbekannte Schwere '{text}'. Gültig: info, niedrig, mittel, hoch."),
        };

    private List<T> Limit<T>(List<T> items, string what)
    {
        if (items.Count <= options.Value.MaxRows) return items;

        session.Notes.Add($"{what} gekürzt: {options.Value.MaxRows} von {items.Count} gezeigt.");
        return items[..options.Value.MaxRows];
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static object? Climate(ClimateState? climate) => climate is null
        ? null
        : new
        {
            Temperatur = Fmt.Round(climate.Celsius, 1),
            Luftfeuchte = Fmt.Round(climate.Humidity, 0),
            ImSollbereich = climate.InRange,
            Veraenderung = Fmt.Round(climate.Celsius - climate.CelsiusAtShiftStart, 1),
            Hinweis = climate.InRange ? "Hallenklima im Sollbereich (18-26 °C, 40-60 % rF)" : "Hallenklima außerhalb des Sollbereichs",
        };

    private static object AlarmRow(AlarmRecord alarm, DateTimeOffset now) => new
    {
        alarm.Id,
        Station = alarm.StationId,
        Art = Views.AlarmKindWord(alarm.Kind),
        Schwere = Views.SeverityWord(alarm.Severity),
        Baugruppe = alarm.Assembly,
        alarm.Text,
        Seit = Fmt.Stamp(alarm.ActiveSinceUtc),
        DauerMin = Fmt.Minutes((alarm.ClearedUtc ?? now) - alarm.ActiveSinceUtc),
        Quittiert = alarm.AcknowledgedUtc is not null,
        Beendet = alarm.ClearedUtc is { } end ? Fmt.Stamp(end) : null,
        Aktiv = alarm.ClearedUtc is null,
        Bewertung = Views.AlarmRating(alarm.Kind),
    };

    private static object MaterialRow(MaterialReachRow row) => new
    {
        Station = row.StationId,
        row.Material,
        Fuellstand = Fmt.Round(row.Level, 0),
        row.Unit,
        VerbrauchProMin = Fmt.Round(row.ConsumptionPerMinute, 2),
        ReichweiteMin = row.ReachMinutes is { } r ? Fmt.Round(r, 0) : (double?)null,
        ReichweiteFuellstandMin = row.Result is { } res && res.ByLevel != TimeSpan.MaxValue ? Fmt.Round(res.ByLevel.TotalMinutes, 0) : (double?)null,
        VerarbeitbarBis = row.ProcessableUntilUtc is { } until ? Fmt.Time(until) : null,
        ReichweiteVerarbeitbarkeitMin = row.Result?.ByProcessability is { } p ? Fmt.Round(p.TotalMinutes, 0) : (double?)null,
        BegrenztDurch = row.Result is null ? null : row.Result.LimitedByProcessability ? "Verarbeitbarkeit der Mischung" : "Füllstand",
        Bewertung = row.RatingCode,
        BewertungText = RatingText.Word(row.RatingCode),
        Text = row.ReachMinutes is { } minutes ? $"{row.Material} reicht noch {Fmt.Duration(TimeSpan.FromMinutes(minutes))}" : $"{row.Material}: kein Verbrauch",
    };

    private static object OrderRow(OrderView view) => new
    {
        Auftrag = view.Order.Id,
        Artikel = view.ArticleLabel,
        Station = view.Order.StationId,
        Halle = view.Order.HallId,
        Zustand = view.Order.State switch { OrderState.Running => "läuft", OrderState.Planned => "geplant", _ => "abgeschlossen" },
        SollMenge = view.Order.TargetQuantity,
        Gefertigt = view.Produced,
        FortschrittProzent = Fmt.Percent(view.Progress),
        RestStueck = view.Remaining,
        TempoStueckProH = Fmt.Round(view.RatePerHour, 0),
        VoraussichtlichFertig = view.ExpectedEndUtc is { } end ? Fmt.Stamp(end) : null,
        Termin = Fmt.Stamp(view.Order.DueUtc),
        StundenBenoetigt = Fmt.Round(view.HoursNeeded, 1),
        StundenVerfuegbar = Fmt.Round(view.HoursAvailable, 1),
        Bewertung = view.Rating,
        BewertungText = RatingText.Word(view.Rating),
        Begruendung = view.Reason,
        Bemerkung = view.Order.Remark,
    };
}
