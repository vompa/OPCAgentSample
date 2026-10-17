using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Kpis;
using OPCAgentSample.Server.Live;
using OPCAgentSample.Server.Mcp;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Demo;

/// <summary>
/// Postfach aus den Eskalationsregeln (Spezifikation Abschnitt 8): Die Fälle entstehen deterministisch, ohne KI, aus den
/// Stationszuständen, Alarmen, der Materialreichweite, der Wartungs-DB, den Aufträgen und den Tageskennzahlen. Welche Rolle einen
/// Fall bekommt, steht in den Regeln der Stammdaten (<c>escalation_rule</c>); die übrigen Regeln sind im Server festgelegt.
/// <list type="bullet">
/// <item>Schichtleiter: Rückstand einer Station, Mischung läuft aus (Vorlauf im Mischraum), offene Zustandswarnung.</item>
/// <item>Hallenleiterin: Qualitätsabweichung einer Charge, überfällige Wartung, ungleiche Ofenbelegung, Wartungsfreigabe (Bündeln), Störung offen.</item>
/// <item>Produktionsleiter: sicherheitsrelevante Wartung länger als 24 h überfällig, gefährdete Aufträge, auffällige MTBF.</item>
/// </list>
/// „Übernehmen“ wird nur im Speicher gehalten (je Besitzer) und geht beim Neustart verloren.
/// </summary>
public sealed class RuleInboxSource(
    MasterDataStore masterData,
    StationStateStore states,
    MaintenanceService maintenance,
    MaterialReachService material,
    OrderService orders,
    PlantQueries queries,
    RollupReader reader,
    IDemoClock clock,
    ShiftCalendar calendar,
    IOptions<McpOptions> options,
    TimeProvider timeProvider) : IInboxSource, IDisposable
{
    private const string RuleSafetyOverdue = "ER-SAFETY-OVERDUE";
    private const string RuleMaintenanceOverdue = "ER-MAINT-OVERDUE";
    private const string RuleMixtureExpiring = "ER-MIXTURE-EXPIRING";
    private const string RuleFaultOpen = "ER-FAULT-OPEN";
    private static readonly TimeSpan s_cacheLifetime = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string OwnerKey, string ItemId), (string By, string At)> _taken = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset Now, DateTimeOffset Built, IReadOnlyDictionary<string, IReadOnlyList<InboxItem>> Items)? _cache;

    public void Dispose() => _gate.Dispose();

    public async Task<IReadOnlyList<InboxItem>> GetAsync(string roleId, string ownerKey, CancellationToken cancellationToken = default)
    {
        var all = await BuildCachedAsync(cancellationToken);
        return all.TryGetValue(roleId, out var items) ? [.. items.Select(i => WithTaken(i, ownerKey))] : [];
    }

    public async Task<InboxItem?> TakeAsync(
        string roleId, string ownerKey, string itemId, string takenBy, string takenAtText, CancellationToken cancellationToken = default)
    {
        var all = await BuildCachedAsync(cancellationToken);
        var item = all.TryGetValue(roleId, out var items) ? items.FirstOrDefault(i => i.Id == itemId) : null;
        if (item is null) return null;

        _taken.TryAdd((ownerKey, itemId), (takenBy, takenAtText));
        return WithTaken(item, ownerKey);
    }

    private InboxItem WithTaken(InboxItem item, string ownerKey) =>
        _taken.TryGetValue((ownerKey, item.Id), out var taken) ? item with { TakenBy = taken.By, TakenAtText = taken.At } : item;

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<InboxItem>>> BuildCachedAsync(CancellationToken ct)
    {
        var now = await reader.ReadOverallWatermarkAsync() ?? clock.UtcNow;
        await _gate.WaitAsync(ct);
        try
        {
            var real = timeProvider.GetUtcNow();
            if (_cache is { } cached && cached.Now == now && real - cached.Built < s_cacheLifetime) return cached.Items;

            var items = await BuildAsync(now, ct);
            _cache = (now, real, items);
            return items;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<InboxItem>>> BuildAsync(DateTimeOffset now, CancellationToken ct)
    {
        var rules = (await masterData.GetAsync(ct)).EscalationRules;
        var snapshot = (await states.GetAsync(ct)).Snapshot;
        var hallA = PlantLayout.HallAStations.ToList();
        var ids = hallA.Select(s => s.Id).ToList();
        var presses = hallA.Where(s => s.Area == Area.Pressing).Select(s => s.Id).ToList();

        var alarms = (await queries.AlarmsAsync(now.AddDays(-1), now.AddMinutes(1), ids)).Where(a => a.ClearedUtc is null).ToList();
        var reach = await material.GetAsync(hallA, now);
        var maintenanceItems = await maintenance.ItemsAsync(ids, now, now.AddDays(2), now, includeForecast: false, assembly: null, onlyOverdue: false, ct);
        var setups = await maintenance.SetupWindowsAsync(ids, now, ct);

        var result = new Dictionary<string, List<InboxItem>>
        {
            [McpRoles.ShiftLead] = [],
            [McpRoles.HallLead] = [],
            [McpRoles.ProductionLead] = [],
        };

        void Add(string? role, InboxItem item)
        {
            if (role is not null && result.TryGetValue(role, out var list)) list.Add(item);
        }

        // Schichtleiter -------------------------------------------------------------------------------------
        foreach (var state in snapshot.Stations.Where(s => s.Area == Area.Pressing && s.Light == TrafficLight.Problem && s.PlanRatio is < 0.75
                     && s.State is not (OperatingState.WaitingForMaterial or OperatingState.WaitingForUpstream)))
        {
            var gap = Math.Round(state.PiecesPlanned - state.PiecesActual);
            var other = setups.Values.Where(w => w.StationId != state.StationId).OrderBy(w => w.OrderEndUtc).FirstOrDefault();
            Add(McpRoles.ShiftLead, Item(
                $"backlog-{state.StationId}", Rating.Crit,
                $"Rückstand {state.StationId}: Umverteilung entscheiden",
                $"Ist/Soll {Fmt.Percent(state.PlanRatio ?? 0)} % in dieser Schicht, ca. {gap:#,0} Stück unter Plan"
                + (state.OrderId is null ? "." : $" (Auftrag {state.OrderId}).")
                + (other is null ? string.Empty : $" {other.StationId} hat ab ca. {Fmt.Time(other.OrderEndUtc)} Kapazität."),
                other is null ? "bis Schichtende" : $"Entscheidung bis {Fmt.Time(other.OrderEndUtc)}",
                "Schaffen wir das Schichtziel?"));
        }

        var mixtureRule = rules.GetValueOrDefault(RuleMixtureExpiring);
        foreach (var row in reach.Where(r => r.Material.StartsWith("Mischung", StringComparison.Ordinal) && r.ReachMinutes is { } minutes
                     && minutes < (mixtureRule?.ThresholdMinutes ?? 60)))
        {
            var lead = options.Value.ProcessLimits.MixtureLeadMinutes;
            var minutes = row.ReachMinutes!.Value;
            var order = now.AddMinutes(minutes - lead);
            Add(mixtureRule?.TargetRoleId ?? McpRoles.ShiftLead, Item(
                $"mixture-{row.StationId}", minutes < 15 ? Rating.Crit : Rating.Warn,
                $"{row.Material} für {row.StationId} anfordern",
                (row.Result?.LimitedByProcessability == true && row.ProcessableUntilUtc is { } until
                    ? $"Verarbeitbarkeit endet {Fmt.Time(until)}"
                    : $"Füllstand reicht noch ca. {Math.Round(minutes):0} min")
                + $", Vorlauf im Mischraum ca. {lead} min.",
                order > now ? $"bis {Fmt.Time(order)}" : "sofort",
                "Wie lange reicht das Material an allen Stationen?"));
        }

        foreach (var alarm in alarms.Where(a => a.Kind == AlarmKind.ConditionWarning))
        {
            var state = snapshot.Find(alarm.StationId);
            var cycle = state is { CycleSeconds: { } c, TargetCycleSeconds: > 0 and var t } && c / t - 1 > TrafficLightThresholds.Default.CycleWarningAbove
                ? $", Taktzeit +{Fmt.Percent(c / t - 1)} %" : string.Empty;
            var assembly = alarm.Assembly is "Station" or "" ? "Zustandswarnung" : alarm.Assembly;
            Add(McpRoles.ShiftLead, Item(
                $"condition-{alarm.Id}", Rating.Warn,
                $"{assembly} {alarm.StationId} prüfen lassen",
                $"{alarm.Text}, seit {Fmt.Time(alarm.ActiveSinceUtc)}{cycle}.",
                "heute",
                cycle.Length > 0 ? $"Warum hängt {alarm.StationId} hinterher?" : $"Was ist an {alarm.StationId} auffällig?"));
        }

        // Hallenleiterin ------------------------------------------------------------------------------------
        await AddQualityAsync(snapshot, presses, now, Add, ct);

        var overdueRule = rules.GetValueOrDefault(RuleMaintenanceOverdue);
        foreach (var item in maintenanceItems.Where(i => i.Overdue))
        {
            var safety = item.Plan.IsSafetyRelevant;
            Add(overdueRule?.TargetRoleId ?? McpRoles.HallLead, Item(
                $"overdue-{item.Plan.Id}", safety ? Rating.Crit : Rating.Warn,
                $"{(safety ? "Sicherheitsprüfung" : "Wartung")} {item.Plan.StationId} überfällig",
                $"{item.Plan.Title.Split(" (", 2)[0]}, seit {item.OverdueDays} Tag{(item.OverdueDays == 1 ? string.Empty : "en")} überfällig. Termin mit Instandhaltung festlegen.",
                "heute",
                "Wie entwickelt sich der Anteil ungeplanter Stillstände seit Juli?"));
        }

        var faultRule = rules.GetValueOrDefault(RuleFaultOpen);
        foreach (var alarm in alarms.Where(a => a.Kind == AlarmKind.Fault && now - a.ActiveSinceUtc > TimeSpan.FromMinutes(faultRule?.ThresholdMinutes ?? 60)))
        {
            Add(faultRule?.TargetRoleId ?? McpRoles.HallLead, Item(
                $"fault-{alarm.Id}", Rating.Crit, $"Störung {alarm.StationId} seit {Fmt.Duration(now - alarm.ActiveSinceUtc)} offen",
                $"{alarm.Text}, seit {Fmt.Time(alarm.ActiveSinceUtc)}.", "sofort", $"Warum steht {alarm.StationId}?"));
        }

        AddOvenImbalance(snapshot, Add);

        foreach (var item in maintenanceItems.Where(i => i.Bundleable && !i.Overdue && !i.AllDay && i.StartUtc > now && i.StartUtc < now.AddHours(8)))
        {
            Add(McpRoles.HallLead, Item(
                $"bundle-{item.Plan.Id}", Rating.Warn,
                $"Freigabe: {(item.Plan.MouldId is not null ? "Formwartung " + item.Plan.StationId : ShortTitle(item.Plan.Title))} mit {item.BundleWith!.Split(' ', 2)[0]} bündeln",
                $"Wartungsauftrag {item.Plan.WorkOrder ?? item.Plan.Id} auf {Fmt.Time(item.StartUtc)} legen. Kein zusätzlicher Stillstand.",
                $"bis {Fmt.Time(item.StartUtc.AddMinutes(-30))}",
                $"Können wir die Formwartung an {item.Plan.StationId} mit dem Rüsten bündeln?"));
        }

        // Produktionsleiter ---------------------------------------------------------------------------------
        var safetyRule = rules.GetValueOrDefault(RuleSafetyOverdue);
        foreach (var item in maintenanceItems.Where(i => i.Overdue && i.Plan.IsSafetyRelevant
                     && now - i.StartUtc > TimeSpan.FromMinutes(safetyRule?.ThresholdMinutes ?? 1440)))
        {
            Add(safetyRule?.TargetRoleId ?? McpRoles.ProductionLead, Item(
                $"escalated-{item.Plan.Id}", Rating.Crit,
                $"Sicherheitsprüfung {item.Plan.StationId} überfällig",
                "Eskaliert, weil sicherheitsrelevant und länger als 24 h überfällig.",
                "heute",
                "Wie entwickelt sich der Anteil ungeplanter Stillstände seit Juli?"));
        }

        await AddOrdersAtRiskAsync(now, Add, ct);
        await AddReliabilityAsync(presses, now, Add, ct);

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<InboxItem>)[.. pair.Value.OrderByDescending(i => Rating.Rank(i.Level)).ThenBy(i => i.Id, StringComparer.Ordinal)]);
    }

    private async Task AddQualityAsync(StationSnapshot snapshot, IReadOnlyList<string> presses, DateTimeOffset now, Action<string?, InboxItem> add, CancellationToken ct)
    {
        var drifting = snapshot.Stations
            .Where(s => s.Area == Area.Pressing && s.FillWeightMeanGrams is { } mean && s.FillWeightTargetGrams is { } target
                && Math.Abs(mean - target) >= TrafficLightThresholds.Default.FillWeightWarningGrams)
            .Select(s => s.StationId)
            .ToList();
        if (drifting.Count == 0) return;

        var batches = await queries.LatestBatchesAsync(drifting, now);
        var cycles = await queries.CycleAggregatesAsync(now.AddHours(-24), now, drifting);
        foreach (var group in drifting.Where(batches.ContainsKey).GroupBy(id => batches[id].Batch))
        {
            var stations = group.ToList();
            var count = stations.Sum(id => cycles.TryGetValue(id, out var c) ? c.Count : 0);
            var scrap = stations.Sum(id => cycles.TryGetValue(id, out var c) ? c.Scrap : 0);
            var rate = count > 0 ? 100.0 * scrap / count : 0;
            add(McpRoles.HallLead, Item(
                $"quality-{group.Key}", Rating.Crit,
                $"Qualitätsabweichung Charge {group.Key}",
                $"Füllgewichtsdrift an {string.Join(" und ", stations)}, Ausschuss bei rund {Math.Round(rate):0} % in den letzten 24 h.",
                "sofort",
                "Wie ist die Qualitätslage in der Halle?"));
        }
    }

    private static void AddOvenImbalance(StationSnapshot snapshot, Action<string?, InboxItem> add)
    {
        var ovens = snapshot.Stations.Where(s => s.Area == Area.Curing && s.Occupancy is not null).OrderByDescending(s => s.Occupancy).ToList();
        if (ovens.Count < 2) return;

        var full = ovens[0];
        var free = ovens[^1];
        if (full.Occupancy < TrafficLightThresholds.Default.OvenWarningAt || full.Occupancy - free.Occupancy < 0.15) return;

        var buffer = snapshot.Stations.FirstOrDefault(s => s.Area == Area.Buffer);
        var waiting = snapshot.Stations.Where(s => s.Area == Area.Packaging && s.State == OperatingState.WaitingForUpstream).Select(s => s.StationId).ToList();
        add(McpRoles.HallLead, Item(
            "ovens-imbalance", Rating.Warn,
            "Ofenbelegung ungleich verteilt",
            $"{full.StationId} bei {Fmt.Percent(full.Occupancy ?? 0)} %, {free.StationId} bei {Fmt.Percent(free.Occupancy ?? 0)} %."
            + (buffer?.MeanWaitMinutes is { } wait ? $" Ø {Math.Round(wait):0} min Wartezeit vor den Öfen" : string.Empty)
            + (waiting.Count > 0 ? $", {string.Join(" und ", waiting)} wartet." : "."),
            "heute",
            "Sind die Öfen heute der Engpass?"));
    }

    private async Task AddOrdersAtRiskAsync(DateTimeOffset now, Action<string?, InboxItem> add, CancellationToken ct)
    {
        var views = await orders.GetAsync(now, PlantLayout.HallAStations.Select(s => s.Id).ToList(), [PlantLayout.HallB, PlantLayout.HallC], null, ct);
        var risky = views.Where(v => v.Rating == Rating.Crit).ToList();
        if (risky.Count == 0) return;

        var diameters = risky.Select(v => v.Article?.DiameterMm).Distinct().ToList();
        var due = risky.Min(v => v.Order.DueUtc);
        var count = risky.Count == 2 ? "Zwei" : risky.Count.ToString(CultureInfo.InvariantCulture);
        var friday = calendar.ProductionDayOf(now);
        while (friday.DayOfWeek != DayOfWeek.Friday) friday = friday.AddDays(1);
        add(McpRoles.ProductionLead, Item(
            "orders-at-risk", Rating.Crit,
            diameters.Count == 1 && diameters[0] is { } d ? $"{count} Ø-{d}-Aufträge gefährdet" : $"{count} Aufträge gefährdet",
            $"Liefertermin {Fmt.Day(due).TrimEnd('.')}. Eine Zusatzschicht am Samstag in Halle {risky[0].Order.HallId} wäre eine Option.",
            $"Entscheidung bis {WeekdayName(friday)}",
            "Fass mir die Woche zusammen."));
    }

    private async Task AddReliabilityAsync(IReadOnlyList<string> presses, DateTimeOffset now, Action<string?, InboxItem> add, CancellationToken ct)
    {
        var first = calendar.ProductionDayOf(now.AddDays(-90));
        var last = calendar.ProductionDayOf(now.AddDays(-1));
        var days = await reader.ReadClosedDaysAsync(first, last, presses, calendar);
        if (days.Count == 0) return;

        var mtbf = presses.ToDictionary(
            id => id,
            id => Reliability.From(Reliability.Sum(days.Where(d => d.StationId == id))).Mtbf?.TotalHours);
        foreach (var id in presses.Where(id => mtbf[id] is not null))
        {
            var others = presses.Where(o => o != id && mtbf[o] is not null).Select(o => mtbf[o]!.Value).ToList();
            if (others.Count == 0 || mtbf[id]!.Value >= 0.5 * others.Average()) continue;

            var reason = days.Where(d => d.StationId == id).SelectMany(d => d.Reasons)
                .Where(p => p.Key.StartsWith("Störung ", StringComparison.Ordinal))
                .GroupBy(p => p.Key).OrderByDescending(g => g.Sum(p => p.Value.Count)).Select(g => g.Key).FirstOrDefault();
            add(McpRoles.ProductionLead, Item(
                $"reliability-{id}", Rating.Warn,
                $"Investition: Teilüberholung {(reason?[8..] ?? "Baugruppe")} {id}",
                $"MTBF {Math.Round(mtbf[id]!.Value):0} h gegenüber {Math.Round(others.Average()):0} h bei den anderen Rundtischen. Angebot einholen?",
                "nächste Investitionsrunde",
                $"Lohnt sich eine Überholung von {id}?"));
        }
    }

    private static InboxItem Item(string id, string level, string title, string detail, string due, string? question) => new(
        id, level, level == Rating.Crit ? "urgent" : "soon", level == Rating.Crit ? "Dringend" : "Zeitnah", title, detail, due, question);

    private static string ShortTitle(string title) =>
        title.StartsWith("Form ", StringComparison.Ordinal) ? "Formwartung " + title.Split(' ')[1] : title.Split(" (", 2)[0];

    private static string WeekdayName(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday => "Montag",
        DayOfWeek.Tuesday => "Dienstag",
        DayOfWeek.Wednesday => "Mittwoch",
        DayOfWeek.Thursday => "Donnerstag",
        DayOfWeek.Friday => "Freitag",
        DayOfWeek.Saturday => "Samstag",
        _ => "Sonntag",
    };
}
