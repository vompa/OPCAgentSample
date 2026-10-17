using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.MasterData;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Ein Wartungstermin der Wartungs-DB mit Zeitfenster, Status und Maschinenstillstand. <see cref="AllDay"/>: Der Termin hat
/// nur ein Datum und gilt für den ganzen Produktionstag.
/// </summary>
public sealed record MaintenanceItem(
    MaintenancePlan Plan,
    bool AllDay,
    DateOnly DueDay,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool Overdue,
    int OverdueDays,
    int StopMinutes,
    int StopMinutesBundled,
    string? BundleWith)
{
    public bool Bundleable => BundleWith is not null;
}

/// <summary>Wartungsempfehlung aus einer Zustandswarnung (z. B. Hydraulik RT-04 vorziehen).</summary>
public sealed record MaintenanceRecommendation(
    string StationId, string Title, string PlannedText, string Suggestion, string Reason, MaintenancePlan Plan);

/// <summary>Voraussichtliches Ende des laufenden Auftrags einer Station und das folgende Rüsten.</summary>
public sealed record SetupWindow(
    string StationId, string RunningOrderId, DateTimeOffset OrderEndUtc, DateTimeOffset SetupEndUtc, string? NextOrderId, string? NextArticle);

/// <summary>
/// Wartungsplanung aus der Wartungs-DB (Stammdaten bis 17.01.2027), den Hubzählern der Formen und den Regeln des Servers:
/// fällig, überfällig, geplant, prognostiziert; Maschinenstillstand einzeln und gebündelt; Konflikte mit laufenden Aufträgen.
/// Alles ist deterministisch (keine KI).
/// </summary>
public sealed class MaintenanceService(
    MasterDataStore masterData,
    PlantQueries queries,
    OrderService orders,
    ShiftCalendar calendar,
    IOptions<McpOptions> options)
{
    private const string BundleMarker = "bündelbar mit ";

    public async Task<IReadOnlyList<MaintenanceItem>> ItemsAsync(
        IReadOnlyCollection<string> stationIds, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now,
        bool includeForecast, string? assembly, bool onlyOverdue, CancellationToken ct)
    {
        var plans = (await masterData.GetAsync(ct)).MaintenancePlans.Where(p => stationIds.Contains(p.StationId));
        var items = plans
            .Where(p => includeForecast || !p.IsForecast)
            .Where(p => assembly is null || Matches(p, assembly))
            .Select(p => ToItem(p, now))
            .Where(i => onlyOverdue ? i.Overdue : (i.Overdue && from <= now) || Overlaps(i, from, to))
            .OrderByDescending(i => i.Overdue)
            .ThenBy(i => i.StartUtc)
            .ThenBy(i => i.Plan.Id, StringComparer.Ordinal)
            .ToList();
        return items;
    }

    public async Task<IReadOnlyList<MaintenanceHistory>> HistoryAsync(IReadOnlyCollection<string> stationIds, string? assembly, CancellationToken ct)
    {
        var rows = (await masterData.GetAsync(ct)).MaintenanceHistory.Where(h => stationIds.Contains(h.StationId)).OrderByDescending(h => h.DoneAtUtc);
        return [.. rows.Where(h => assembly is null || h.Title.Contains(assembly, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Hubzähler und Standzeit der Formen, je Station die aktuell eingebaute.</summary>
    public async Task<IReadOnlyDictionary<string, MouldReading>> MouldsAsync(DateTimeOffset now, CancellationToken ct) =>
        (await queries.MouldsAsync(now)).ToDictionary(m => m.StationId, StringComparer.Ordinal);

    /// <summary>Verschleiß der Form zu einem Zeitpunkt (bei gleichbleibendem Hubtempo) und der Zeitpunkt der Standzeitgrenze.</summary>
    public static (double WearNow, double WearAt, DateTimeOffset? LimitUtc) Wear(MouldReading mould, DateTimeOffset now, DateTimeOffset at)
    {
        var current = MouldWear.Compute(mould.Strokes, mould.StrokeLimit, mould.StrokesPerMinute, now);
        return (current.Wear, MouldWear.WearAt(current, mould.StrokesPerMinute, at > now ? at - now : TimeSpan.Zero), current.LimitReachedUtc);
    }

    /// <summary>Prognose aus Hüben: Produktionstag, an dem die Standzeitgrenze beim Tempo „Hübe je Produktionstag“ erreicht wird.</summary>
    public DateOnly? ForecastDay(MouldReading mould, DateTimeOffset now) =>
        mould.AverageStrokesPerDay > 0
            ? MouldWear.ForecastLimitDay(mould.Strokes, mould.StrokeLimit, mould.AverageStrokesPerDay, calendar.ProductionDayOf(now), calendar)
            : null;

    /// <summary>
    /// Empfehlungen: Zeigt eine Station eine offene Zustandswarnung an einer Baugruppe (z. B. Hydraulik) und steht deren reguläre
    /// Wartung erst nach dem Horizont an, wird vorgeschlagen, sie auf den nächsten ohnehin geplanten Stillstand der Station zu legen.
    /// </summary>
    public async Task<IReadOnlyList<MaintenanceRecommendation>> RecommendationsAsync(
        IReadOnlyCollection<string> stationIds, IReadOnlyList<MaintenanceItem> inHorizon, DateTimeOffset to, DateTimeOffset now, CancellationToken ct)
    {
        var alarms = await queries.AlarmsAsync(now.AddDays(-1), now.AddMinutes(1), stationIds);
        var warnings = alarms.Where(a => a.Kind == AlarmKind.ConditionWarning && a.ClearedUtc is null).ToList();
        if (warnings.Count == 0) return [];

        var later = (await masterData.GetAsync(ct)).MaintenancePlans.Where(p => stationIds.Contains(p.StationId) && p.DueUtc >= to).ToList();
        var result = new List<MaintenanceRecommendation>();
        foreach (var warning in warnings)
        {
            var assembly = warning.Assembly;
            var regular = later
                .Where(p => p.StationId == warning.StationId && p.Title.Contains(assembly, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.DueUtc)
                .FirstOrDefault();
            var carrier = inHorizon
                .Where(i => i.Plan.StationId == warning.StationId && i.Bundleable && !i.Overdue)
                .OrderBy(i => i.StartUtc)
                .FirstOrDefault();
            if (regular is null) continue;

            var suggestion = carrier is null
                ? "auf den nächsten Stillstand der Station vorziehen"
                : $"auf {Fmt.Stamp(carrier.StartUtc)} vorziehen, zusammen mit '{carrier.Plan.Title}' ({carrier.BundleWith}): dann entsteht kein zusätzlicher Stillstand";
            result.Add(new MaintenanceRecommendation(
                warning.StationId, regular.Title, Fmt.Day(regular.DueUtc), suggestion,
                $"Zustandswarnung seit {Fmt.Stamp(warning.ActiveSinceUtc)}: {warning.Text}", regular));
        }

        return result;
    }

    /// <summary>Voraussichtliches Ende des laufenden Auftrags je Station und das Rüsten auf den Folgeauftrag.</summary>
    public async Task<IReadOnlyDictionary<string, SetupWindow>> SetupWindowsAsync(
        IReadOnlyCollection<string> stationIds, DateTimeOffset now, CancellationToken ct)
    {
        var views = await orders.GetAsync(now, stationIds, [], null, ct);
        var result = new Dictionary<string, SetupWindow>();
        foreach (var running in views.Where(v => v.Order.State == OrderState.Running && v.ExpectedEndUtc is not null && v.Order.StationId is not null))
        {
            var station = running.Order.StationId!;
            var next = views.Where(v => v.Order.StationId == station && v.Order.State == OrderState.Planned).OrderBy(v => v.Order.DueUtc).FirstOrDefault();
            if (next is null || result.ContainsKey(station)) continue;

            var end = running.ExpectedEndUtc!.Value;
            result[station] = new SetupWindow(
                station, running.Order.Id, end, end.AddMinutes(options.Value.Maintenance.SetupMinutes), next.Order.Id, next.ArticleLabel);
        }

        return result;
    }

    private MaintenanceItem ToItem(MaintenancePlan plan, DateTimeOffset now)
    {
        var dueDay = calendar.ProductionDayOf(plan.DueUtc);
        var dayPeriod = calendar.DayPeriod(dueDay);
        var dateOnly = plan.DueUtc == dayPeriod.StartUtc;
        var duration = TimeSpan.FromMinutes(plan.DurationMinutes ?? 0);
        var start = dateOnly ? dayPeriod.StartUtc : plan.DueUtc;
        var end = plan.WindowEndUtc ?? (dateOnly ? dayPeriod.EndUtc : plan.DueUtc + (duration > TimeSpan.Zero ? duration : TimeSpan.FromMinutes(1)));
        var today = calendar.ProductionDayOf(now);
        var overdue = !plan.IsForecast && plan.WindowEndUtc is null && (dateOnly ? dueDay < today : plan.DueUtc < now);
        var overdueDays = overdue ? Math.Max(0, today.DayNumber - dueDay.DayNumber) : 0;
        var bundle = BundleTarget(plan.Hint);
        var stop = options.Value.Maintenance.MachineStopMinutes.TryGetValue(plan.Id, out var configured) ? configured : plan.DurationMinutes ?? 0;
        return new MaintenanceItem(plan, dateOnly, dueDay, start, end, overdue, overdueDays, stop, bundle is null ? stop : 0, bundle);
    }

    private static bool Overlaps(MaintenanceItem item, DateTimeOffset from, DateTimeOffset to) => item.StartUtc < to && item.EndUtc > from;

    private static bool Matches(MaintenancePlan plan, string assembly) =>
        plan.Title.Contains(assembly, StringComparison.OrdinalIgnoreCase)
        || (plan.Hint?.Contains(assembly, StringComparison.OrdinalIgnoreCase) ?? false)
        || (assembly.Equals("Form", StringComparison.OrdinalIgnoreCase) && plan.MouldId is not null);

    /// <summary>„bündelbar mit Rüsten auf Ø 115“ → „Rüsten auf Ø 115“.</summary>
    private static string? BundleTarget(string? hint)
    {
        if (hint is null) return null;

        var index = hint.IndexOf(BundleMarker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : hint[(index + BundleMarker.Length)..].Trim();
    }
}
