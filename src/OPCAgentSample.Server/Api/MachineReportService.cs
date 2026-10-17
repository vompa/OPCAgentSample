using System.Globalization;
using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Configuration;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Mcp;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// Maschinen-Report: Kennzahlen der acht Stationen der Halle A für einen Zeitraum und dessen Vorperiode. Gerechnet wird mit denselben
/// Funktionen wie in den MCP-Tools (<see cref="KpiAccess"/>, Kennzahlen-Bibliothek, Kostensätze, <see cref="MaintenanceService"/>); hier
/// werden die Ergebnisse nur für die Oberfläche angeordnet. Die Antwort enthält keine Personendaten.
/// </summary>
public sealed class MachineReportService(
    McpSession session,
    KpiAccess kpis,
    TimeRangeResolver ranges,
    PlantCostRates rates,
    PlantQueries queries,
    MachineReportQueries trendQueries,
    MaintenanceService maintenance,
    ShiftCalendar calendar,
    IOptions<PlantOptions> plant,
    IOptions<McpOptions> mcp)
{
    /// <summary>Soll-Temperatur in der Haltephase der Öfen und erlaubte Abweichung in °C; kältere Werte (Bereitschaft, kalt) zählen nicht.</summary>
    internal const double OvenHoldCelsius = 180;
    internal const double OvenToleranceCelsius = 5;

    private const double OutsideWarnPercent = 5;
    private const double OutsideCritPercent = 15;
    private const double ScrapWarnPercent = 2.5;
    private const double ScrapCritPercent = 5;
    private const int MaxPareto = 6;
    private const int MaxFindings = 8;
    private const int MaxMaintenance = 3;
    private const string ReactionMarker = "Reaktionszeit Bereitschaft ";

    private static readonly Shift[] s_shifts = [Shift.Early, Shift.Late, Shift.Night];

    public async Task<MachineReportResponse> BuildAsync(string roleId, string range, bool compare, CancellationToken ct)
    {
        var now = await session.NowAsync(ct);
        var plan = Resolve(range, now);
        var costRates = await rates.GetAsync(ct);
        var rawStart = await kpis.RawStartAsync();

        var stations = PlantLayout.HallAProductionStations;
        var ids = stations.Select(s => s.Id).ToList();
        var oeeIds = stations.Where(s => s.Area is Area.Pressing or Area.Packaging).Select(s => s.Id).ToList();
        var pressIds = stations.Where(s => s.Area == Area.Pressing).Select(s => s.Id).ToList();

        var data = await kpis.GetAsync(plan.Current, false, ct);
        var previousData = compare ? await kpis.GetAsync(plan.Previous, false, ct) : null;
        var detailStart = plan.Current.StartUtc > rawStart ? plan.Current.StartUtc : rawStart;
        var end = plan.Current.EffectiveEnd(now);
        var detailAvailable = detailStart < end;

        var shiftRange = ShiftRangeOf(range, plan.Current, rawStart, now);
        var byShift = new Dictionary<Shift, KpiData>();
        foreach (var kind in s_shifts) byShift[kind] = await kpis.GetByShiftKindAsync(shiftRange, kind, ct);

        var losses = detailAvailable ? await queries.LossEventsAsync(detailStart, end, ids) : [];
        var scrapReasons = detailAvailable && pressIds.Count > 0 ? await queries.CycleAggregatesAsync(detailStart, end, pressIds) : new Dictionary<string, CycleAggregate>();
        var moulds = await maintenance.MouldsAsync(now, ct);

        var built = new List<MachineStation>();
        foreach (var station in stations)
        {
            built.Add(await BuildStationAsync(
                station, plan, data, previousData, byShift, losses, scrapReasons, moulds, costRates, detailStart, end, now, range, ct));
        }

        var costs = BuildCosts(data, previousData, ids, costRates);
        var summary = BuildSummary(data, previousData, oeeIds, pressIds, ids, costRates);
        var shiftComparison = BuildShiftComparison(range, shiftRange, byShift, oeeIds, ids, losses, costRates);
        var findings = BuildFindings(range, built, shiftComparison, costs);
        var notes = BuildNotes(plan, compare, rawStart);
        if (compare && summary.Previous is null) notes.Add($"Für die Vorperiode ({plan.PreviousLabel}) liegen keine Kennzahlen vor; es gibt keinen Vergleich.");

        var until = session.Sources.SourceUntilUtc ?? now;
        return new MachineReportResponse(
            roleId, range, MachineRanges.DefaultOf(roleId), compare, plan.Label, compare ? plan.PreviousLabel : null,
            plan.Current.StartUtc, plan.Current.EndUtc, now, Fmt.Stamp(until),
            session.Sources.Labels.Count == 0 ? "Berechnung" : string.Join(" + ", session.Sources.Labels),
            notes, summary, built, shiftComparison, costs, findings);
    }

    // ---- Zeiträume ------------------------------------------------------------------------------------------

    private sealed record RangePlan(ResolvedRange Current, ResolvedRange Previous, string Label, string PreviousLabel);

    private RangePlan Resolve(string range, DateTimeOffset now)
    {
        switch (range)
        {
            case MachineRanges.Shift:
            {
                var current = calendar.ShiftPeriodOf(now);
                var previous = ranges.PreviousShift(current);
                return new RangePlan(
                    ranges.ForPeriod(current), ranges.ForPeriod(previous),
                    $"Laufende Schicht · {ranges.ShiftLabel(current)}", ranges.ShiftLabel(previous));
            }

            case MachineRanges.Yesterday:
            {
                var day = ranges.PreviousProductionDay(calendar.ProductionDayOf(now));
                var before = ranges.PreviousProductionDay(day);
                return new RangePlan(
                    ranges.ForPeriod(calendar.DayPeriod(day)), ranges.ForPeriod(calendar.DayPeriod(before)),
                    $"Gestern · {Fmt.Day(day)}", Fmt.Day(before));
            }

            case MachineRanges.Week:
            {
                var current = calendar.WeekPeriodOf(now);
                var previous = PreviousWeek(current);
                return new RangePlan(
                    ranges.ForPeriod(current), ranges.ForPeriod(previous),
                    $"Diese Woche · {TimeRangeResolver.WeekLabel(current)}", TimeRangeResolver.WeekLabel(previous));
            }

            case MachineRanges.FourWeeks:
            {
                var weeks = new List<ProductionPeriod> { calendar.WeekPeriodOf(now) };
                for (var i = 1; i < 8; i++) weeks.Add(PreviousWeek(weeks[^1]));
                var current = WeekSpan(weeks.Take(4));
                var previous = WeekSpan(weeks.Skip(4));
                return new RangePlan(current, previous, $"Letzte 4 Wochen · {current.Label}", previous.Label);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(range), range, "Unbekannter Zeitraum.");
        }
    }

    private ProductionPeriod PreviousWeek(ProductionPeriod week) =>
        calendar.WeekPeriod(calendar.ProductionDayOf(week.StartUtc.AddDays(-3)));

    private static ResolvedRange WeekSpan(IEnumerable<ProductionPeriod> descending)
    {
        var weeks = descending.OrderBy(w => w.StartUtc).ToList();
        return new ResolvedRange(
            $"{TimeRangeResolver.WeekLabel(weeks[0])} bis {TimeRangeResolver.WeekLabel(weeks[^1])}",
            weeks[0].StartUtc, weeks[^1].EndUtc, RangeKind.Custom, weeks);
    }

    /// <summary>Zeitraum des Schichtvergleichs: bei der laufenden Schicht der ganze Tag, sonst der Zeitraum ab Beginn der Rohdaten.</summary>
    private ResolvedRange ShiftRangeOf(string range, ResolvedRange current, DateTimeOffset rawStart, DateTimeOffset now)
    {
        if (range == MachineRanges.Shift) return ranges.ForPeriod(calendar.DayPeriodOf(now));
        return current.StartUtc >= rawStart ? current : current with { StartUtc = rawStart };
    }

    private static string ShiftBasis(string range, ResolvedRange shiftRange) => range switch
    {
        MachineRanges.Shift => $"Alle Schichten von {shiftRange.Label} bis jetzt, weil eine einzelne Schicht keinen Vergleich ergibt.",
        MachineRanges.Yesterday => $"Früh-, Spät- und Nachtschicht von {shiftRange.Label}.",
        _ => "Alle Früh-, Spät- und Nachtschichten im Zeitraum (ab Beginn der Rohdaten in KW 41, davor gibt es keine Schichtwerte).",
    };

    private static List<string> BuildNotes(RangePlan plan, bool compare, DateTimeOffset rawStart)
    {
        var notes = new List<string>();
        if (plan.Current.StartUtc < rawStart)
        {
            notes.Add("Prozesswerte, Ausschussgründe, Schichtvergleich und Anlaufverluste liegen erst ab KW 41 vor; frühere Wochen gibt es nur als Tageskennzahlen.");
        }

        if (compare) notes.Add("Verglichen werden Quoten (OEE, Ausschuss, Stillstandsanteil), weil die laufende Periode noch nicht abgeschlossen sein kann.");
        return notes;
    }

    // ---- Stationen ------------------------------------------------------------------------------------------

    private async Task<MachineStation> BuildStationAsync(
        Station station, RangePlan plan, KpiData data, KpiData? previousData, Dictionary<Shift, KpiData> byShift,
        IReadOnlyList<LossEvent> losses, IReadOnlyDictionary<string, CycleAggregate> scrapReasons, IReadOnlyDictionary<string, MouldReading> moulds,
        CostRateTable costRates, DateTimeOffset detailStart, DateTimeOffset end, DateTimeOffset now, string range, CancellationToken ct)
    {
        var id = station.Id;
        var figures = data.Figures(id);
        var isOven = station.Area == Area.Curing;
        var pareto = DowntimePareto.Build(data.Block, [id], costRates);
        var downtime = TimeSpan.FromTicks(pareto.Sum(p => p.Duration.Ticks));
        var cost = CostCalculator.Of(data.Block, [id], costRates);
        var plan1 = PlanFulfillment.From(figures);

        MachineOee? oee = null;
        int? occupancy = null;
        string rating;
        if (isOven)
        {
            var occupied = await OccupancyAsync(id, plan.Current, now, ct);
            occupancy = Fmt.Percent(occupied.Ratio);
            rating = occupied.CapacityPlaceMinutes <= 0 ? Rating.Info
                : occupied.Ratio >= TrafficLightThresholds.Default.OvenWarningAt ? Rating.Warn : Rating.Ok;
        }
        else
        {
            oee = OeeOf(figures);
            rating = oee?.Rating ?? Rating.Info;
        }

        var previous = previousData is null ? null : await PreviousOfAsync(station, previousData, plan.Previous, now, costRates, ct);
        if (previous is { OeePercent: null, OccupancyPercent: null }) previous = null;
        var press = station.Area == Area.Pressing;
        var shiftOee = s_shifts
            .Select(kind => new MachineShiftOee(ShiftCalendar.ShiftLabel(kind), isOven ? null : OeeOf(byShift[kind].Figures(id))?.OeePercent))
            .ToList();

        return new MachineStation(
            id, station.Name, TypeOf(station), rating, oee, occupancy,
            isOven ? null : press ? "Scheiben" : "Kartons",
            isOven ? 0 : plan1.Actual,
            isOven ? 0 : (long)Math.Round(plan1.Planned, MidpointRounding.AwayFromZero),
            !isOven && plan1.Planned > 0 ? Fmt.Percent(plan1.Ratio) : null,
            !isOven && figures.TotalPieces > 0 ? Fmt.Round(100.0 * figures.ScrapPieces / figures.TotalPieces, 1) : null,
            Fmt.Minutes(downtime),
            SharePercent(downtime, figures.PlanningTime),
            Euro(cost.DowntimeEuro),
            Euro(cost.ScrapEuro),
            previous,
            [.. pareto.Take(MaxPareto).Select(p => new MachinePareto(p.Reason, Fmt.Minutes(p.Duration), p.Count, Euro(p.CostEuro)))],
            press ? ScrapOf(scrapReasons.GetValueOrDefault(id)) : [],
            detailStart < end ? await TrendsAsync(station, figures, range, detailStart, end, ct) : [],
            ReliabilityOf(figures, id, byShift, losses),
            await MaintenanceOfAsync(id, moulds, now, ct),
            shiftOee);
    }

    private async Task<MachinePrevious> PreviousOfAsync(
        Station station, KpiData previousData, ResolvedRange previousRange, DateTimeOffset now, CostRateTable costRates, CancellationToken ct)
    {
        var figures = previousData.Figures(station.Id);
        var downtime = TimeSpan.FromTicks(DowntimePareto.Build(previousData.Block, [station.Id], costRates).Sum(p => p.Duration.Ticks));
        if (station.Area == Area.Curing)
        {
            var occupied = await OccupancyAsync(station.Id, previousRange, now, ct);
            return new MachinePrevious(null, null, SharePercent(downtime, figures.PlanningTime), occupied.CapacityPlaceMinutes > 0 ? Fmt.Percent(occupied.Ratio) : null);
        }

        return new MachinePrevious(
            OeeOf(figures)?.OeePercent,
            figures.TotalPieces > 0 ? Fmt.Round(100.0 * figures.ScrapPieces / figures.TotalPieces, 1) : null,
            SharePercent(downtime, figures.PlanningTime),
            null);
    }

    private async Task<OccupancyFigures> OccupancyAsync(string ovenId, ResolvedRange range, DateTimeOffset now, CancellationToken ct)
    {
        var values = await queries.OvenOccupancyAsync([ovenId], range.StartUtc, range.EffectiveEnd(now));
        return values.TryGetValue(ovenId, out var value)
            ? new OccupancyFigures(value.Occupied, (double)plant.Value.OvenPlaces * value.Minutes)
            : OccupancyFigures.Empty;
    }

    private static MachineOee? OeeOf(ProductionFigures figures)
    {
        if (figures.PlanningTime <= TimeSpan.Zero) return null;

        var oee = OeeCalculator.From(figures);
        return new MachineOee(
            OeeResult.Percent(oee.Oee), OeeResult.Percent(oee.Availability), OeeResult.Percent(oee.Performance), OeeResult.Percent(oee.Quality),
            KpiTools.RateOee(oee.Oee));
    }

    private static string TypeOf(Station station) => station.Area switch
    {
        Area.Pressing => "press",
        Area.Curing => "oven",
        _ => "packaging",
    };

    private static double SharePercent(TimeSpan part, TimeSpan whole) =>
        whole > TimeSpan.Zero ? Fmt.Round(100.0 * part / whole, 1) : 0;

    private static int Euro(decimal value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static List<MachineScrapReason> ScrapOf(CycleAggregate? aggregate)
    {
        if (aggregate is null || aggregate.Scrap == 0) return [];

        return [.. aggregate.ScrapByReason
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new MachineScrapReason(KpiTools.ScrapWord(p.Key), p.Value, Fmt.Round(100.0 * p.Value / aggregate.Scrap, 1)))];
    }

    // ---- Prozesswerte ---------------------------------------------------------------------------------------

    private static TimeSpan BucketOf(string range) => range switch
    {
        MachineRanges.Shift => TimeSpan.FromMinutes(15),
        MachineRanges.Yesterday => TimeSpan.FromHours(1),
        MachineRanges.Week => TimeSpan.FromHours(4),
        _ => TimeSpan.FromHours(12),
    };

    private static string LabelOf(DateTimeOffset start, string range) =>
        range is MachineRanges.Shift or MachineRanges.Yesterday ? Fmt.Time(start) : Fmt.Stamp(start);

    private async Task<List<MachineTrend>> TrendsAsync(
        Station station, ProductionFigures figures, string range, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var bucket = BucketOf(range);
        var origin = ranges.FloorToHour(from);
        var trends = new List<MachineTrend>();
        switch (station.Area)
        {
            case Area.Pressing:
            {
                var tolerance = TrafficLightThresholds.Default.CycleWarningAbove * 100;
                var cycle = await trendQueries.PressBucketsAsync(station.Id, PressValue.CycleDeviationPercent, from, to, bucket, origin);
                AddBand(trends, "cycle", "Taktzeit gegen Soll", "% Abweichung", 1, 0, -tolerance, tolerance, "Takte", cycle, range);

                var limits = mcp.Value.ProcessLimits;
                var target = limits.BuildUpTargetSeconds;
                var buildUp = await trendQueries.PressBucketsAsync(station.Id, PressValue.BuildUpSeconds, from, to, bucket, origin);
                AddBand(trends, "buildup", "Presskraftaufbau", "s", 2, target, target * (2 - limits.BuildUpWarningRatio), target * limits.BuildUpWarningRatio, "Takte", buildUp, range);
                break;
            }

            case Area.Curing:
            {
                var temperature = await trendQueries.OvenBucketsAsync(station.Id, from, to, bucket, origin);
                AddBand(trends, "temperature", "Temperatur Zone 2 (Haltephase)", "°C", 1, OvenHoldCelsius,
                    OvenHoldCelsius - OvenToleranceCelsius, OvenHoldCelsius + OvenToleranceCelsius, "Minutenwerte", temperature, range);
                break;
            }

            default:
                await AddCartonsAsync(trends, station, figures, range, from, to, bucket, origin, ct);
                break;
        }

        return trends;
    }

    private static void AddBand(
        List<MachineTrend> trends, string key, string label, string unit, int decimals, double target, double low, double high, string basis,
        IReadOnlyList<BandBucket> buckets, string range)
    {
        var usable = buckets.Where(b => b.Count > 0).ToList();
        if (usable.Count == 0) return;

        var total = usable.Sum(b => b.Count);
        var outside = total > 0 ? Fmt.Round(100.0 * usable.Sum(b => b.Outside) / total, 1) : 0;
        trends.Add(new MachineTrend(
            key, label, unit, decimals, Fmt.Round(target, decimals), Fmt.Round(low, decimals), Fmt.Round(high, decimals), outside, basis,
            RateOutside(outside), [.. usable.Select(b => LabelOf(b.StartUtc, range))], [.. usable.Select(b => Fmt.Round(b.Mean, decimals))]));
    }

    private async Task AddCartonsAsync(
        List<MachineTrend> trends, Station station, ProductionFigures figures, string range, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket,
        DateTimeOffset origin, CancellationToken ct)
    {
        // Soll-Rate = Planmenge je Planbelegungsstunde (die Planung rechnet mit der üblichen Auslastung, nicht mit dem Soll-Takt allein).
        if (figures.PlanningTime <= TimeSpan.Zero || figures.PlannedQuantity <= 0) return;

        var target = (double)figures.PlannedQuantity / figures.PlanningTime.TotalHours;
        var buckets = (await trendQueries.UnitBucketsAsync(station.Id, from, to, bucket, origin)).Where(b => b.Minutes > 0).ToList();
        if (buckets.Count == 0) return;

        var floor = TrafficLightThresholds.Default.PlanWarningBelow;
        var rates = new List<(DateTimeOffset Start, double Rate)>();
        foreach (var b in buckets)
        {
            var end = b.StartUtc + bucket < to ? b.StartUtc + bucket : to;
            rates.Add((b.StartUtc, b.Pieces / Math.Max((end - b.StartUtc).TotalHours, 1.0 / 60)));
        }

        var outside = Fmt.Round(100.0 * rates.Count(r => r.Rate < floor * target) / rates.Count, 1);
        trends.Add(new MachineTrend(
            "cartons", "Kartons je Stunde", "Kartons/h", 0, Fmt.Round(target, 0), Fmt.Round(target * floor, 0), null, outside,
            "Zeitabschnitte unter 90 % der geplanten Rate", RateOutside(outside), [.. rates.Select(r => LabelOf(r.Start, range))],
            [.. rates.Select(r => Fmt.Round(r.Rate, 0))]));
    }

    private static string RateOutside(double percent) =>
        percent >= OutsideCritPercent ? Rating.Crit : percent >= OutsideWarnPercent ? Rating.Warn : Rating.Ok;

    // ---- Zuverlässigkeit und Wartung --------------------------------------------------------------------------

    private MachineReliability ReliabilityOf(
        ProductionFigures figures, string stationId, Dictionary<Shift, KpiData> byShift, IReadOnlyList<LossEvent> losses)
    {
        var reliability = Reliability.From(figures);
        var perShift = new List<MachineShiftReliability>();
        foreach (var kind in s_shifts)
        {
            var shiftReliability = Reliability.From(byShift[kind].Figures(stationId));
            var reactions = ReactionTimes(losses.Where(l => l.StationId == stationId), kind);
            perShift.Add(new MachineShiftReliability(
                ShiftCalendar.ShiftLabel(kind),
                shiftReliability.Mttr is { } mttr ? Fmt.Round(mttr.TotalMinutes, 1) : null,
                shiftReliability.Failures,
                reactions.Count > 0 ? Fmt.Round(reactions.Average(), 1) : null,
                reactions.Count));
        }

        return new MachineReliability(
            reliability.Mtbf is { } mtbf ? Fmt.Round(mtbf.TotalHours, 1) : null,
            reliability.Mttr is { } mttr2 ? Fmt.Round(mttr2.TotalMinutes, 1) : null,
            reliability.Failures,
            Fmt.Percent(reliability.UnplannedShare),
            perShift);
    }

    /// <summary>Reaktionszeiten der Bereitschaft aus der Notiz der Störungen einer Schichtart (nachts erfasst, z. B. „Reaktionszeit Bereitschaft 14 min“).</summary>
    private List<double> ReactionTimes(IEnumerable<LossEvent> losses, Shift kind)
    {
        var result = new List<double>();
        foreach (var loss in losses.Where(l => l.Category == LossCategory.Breakdown && calendar.ShiftOf(l.StartUtc) == kind))
        {
            var index = loss.Note?.IndexOf(ReactionMarker, StringComparison.Ordinal) ?? -1;
            if (index < 0) continue;

            var digits = new string([.. loss.Note![(index + ReactionMarker.Length)..].TakeWhile(char.IsAsciiDigit)]);
            if (double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) result.Add(minutes);
        }

        return result;
    }

    private async Task<MachineMaintenance> MaintenanceOfAsync(
        string stationId, IReadOnlyDictionary<string, MouldReading> moulds, DateTimeOffset now, CancellationToken ct)
    {
        MachineMould? mould = null;
        if (moulds.TryGetValue(stationId, out var reading))
        {
            var wear = reading.StrokeLimit > 0 ? (double)reading.Strokes / reading.StrokeLimit : 0;
            var limitDay = maintenance.ForecastDay(reading, now);
            mould = new MachineMould(
                reading.MouldId, Fmt.Percent(wear), reading.Strokes, reading.StrokeLimit, Math.Max(0, reading.StrokeLimit - reading.Strokes),
                limitDay is { } day ? Fmt.Day(day) : null,
                wear >= TrafficLightThresholds.Default.MouldProblemAt ? Rating.Crit : wear >= TrafficLightThresholds.Default.MouldWarningAt ? Rating.Warn : Rating.Ok);
        }

        var items = await maintenance.ItemsAsync([stationId], now, now.AddDays(14), now, false, null, false, ct);
        return new MachineMaintenance(
            mould,
            [.. items.Take(MaxMaintenance).Select(i => new MachineMaintenanceItem(
                i.Plan.Id, i.Plan.Title,
                i.Overdue ? $"überfällig seit {i.OverdueDays} Tagen" : i.AllDay ? Fmt.Day(i.DueDay) : Fmt.Stamp(i.StartUtc),
                i.Overdue, i.StopMinutes))]);
    }

    // ---- Halle: Kennzahlen, Kosten, Schichten -----------------------------------------------------------------

    private static MachineSummary BuildSummary(
        KpiData data, KpiData? previous, IReadOnlyList<string> oeeIds, IReadOnlyList<string> pressIds, IReadOnlyList<string> ids, CostRateTable costRates)
    {
        MachineSummary Part(KpiData d) => new(
            OeeOf(d.Figures(oeeIds)), PlanPercentOf(d.Figures(pressIds)), ScrapOf(d.Figures(pressIds)),
            Fmt.Minutes(TimeSpan.FromTicks(DowntimePareto.Build(d.Block, ids, costRates).Sum(p => p.Duration.Ticks))), null);

        var current = Part(data);
        if (previous is null) return current;

        var before = Part(previous);
        var planning = previous.Figures(ids).PlanningTime;
        if (planning <= TimeSpan.Zero) return current;

        return current with
        {
            Previous = new MachinePrevious(
                before.Oee?.OeePercent, before.ScrapPercent, SharePercent(TimeSpan.FromMinutes(before.DowntimeMinutes), planning), null),
        };
    }

    private static int? PlanPercentOf(ProductionFigures figures)
    {
        var plan = PlanFulfillment.From(figures);
        return plan.Planned > 0 ? Fmt.Percent(plan.Ratio) : null;
    }

    private static double? ScrapOf(ProductionFigures figures) =>
        figures.TotalPieces > 0 ? Fmt.Round(100.0 * figures.ScrapPieces / figures.TotalPieces, 1) : null;

    private static MachineCosts BuildCosts(KpiData data, KpiData? previous, IReadOnlyList<string> ids, CostRateTable costRates)
    {
        var costs = CostCalculator.Of(data.Block, ids, costRates);
        var items = ids
            .Where(data.Block.Stations.ContainsKey)
            .SelectMany(id => data.Block.Stations[id].Reasons.Select(r => (Station: id, Reason: r.Key, r.Value.Duration, Cost: CostCalculator.ReasonCost(data.Block, id, r.Key, costRates))))
            .OrderByDescending(x => x.Cost)
            .ThenBy(x => x.Station, StringComparer.Ordinal)
            .ThenBy(x => x.Reason, StringComparer.Ordinal)
            .Take(MaxPareto)
            .Select(x => new MachineCostItem(x.Station, x.Reason, Fmt.Minutes(x.Duration), Euro(x.Cost)))
            .ToList();
        var hours = data.Figures(ids).PlanningTime.TotalHours;
        var perHour = hours > 0 ? Euro(costs.TotalEuro / (decimal)hours) : 0;
        if (previous is null || previous.Figures(ids).PlanningTime <= TimeSpan.Zero)
        {
            return new MachineCosts(Euro(costs.DowntimeEuro), Euro(costs.ScrapEuro), Euro(costs.TotalEuro), null, perHour, null, items);
        }

        var before = CostCalculator.Of(previous.Block, ids, costRates);
        var beforeHours = previous.Figures(ids).PlanningTime.TotalHours;
        return new MachineCosts(
            Euro(costs.DowntimeEuro), Euro(costs.ScrapEuro), Euro(costs.TotalEuro), Euro(before.TotalEuro), perHour,
            beforeHours > 0 ? Euro(before.TotalEuro / (decimal)beforeHours) : null, items);
    }

    private MachineShiftComparison BuildShiftComparison(
        string range, ResolvedRange shiftRange, Dictionary<Shift, KpiData> byShift, IReadOnlyList<string> oeeIds, IReadOnlyList<string> ids,
        IReadOnlyList<LossEvent> losses, CostRateTable costRates)
    {
        var rows = new List<MachineShift>();
        foreach (var kind in s_shifts)
        {
            var data = byShift[kind];
            var startups = losses
                .Where(l => l.Reason == LossReasons.StartupLoss && calendar.ShiftOf(l.StartUtc) == kind && l.StartUtc >= shiftRange.StartUtc)
                .ToList();
            var reactions = ReactionTimes(losses, kind);
            var reliability = Reliability.From(data.Figures(ids));
            rows.Add(new MachineShift(
                ShiftCalendar.ShiftLabel(kind),
                OeeOf(data.Figures(oeeIds)),
                Fmt.Minutes(TimeSpan.FromTicks(DowntimePareto.Build(data.Block, ids, costRates).Sum(p => p.Duration.Ticks))),
                startups.Sum(l => l.AffectedPieces),
                startups.Count,
                Fmt.Round(startups.Sum(l => ((l.EndUtc ?? l.StartUtc) - l.StartUtc).TotalMinutes), 0),
                reliability.Mttr is { } mttr ? Fmt.Round(mttr.TotalMinutes, 1) : null,
                reactions.Count > 0 ? Fmt.Round(reactions.Average(), 1) : null,
                reliability.Failures));
        }

        return new MachineShiftComparison(ShiftBasis(range, shiftRange), rows);
    }

    // ---- Auffälligkeiten ---------------------------------------------------------------------------------------

    private static string PhraseOf(string range) => range switch
    {
        MachineRanges.Shift => "in der laufenden Schicht",
        MachineRanges.Yesterday => "gestern",
        MachineRanges.Week => "diese Woche",
        _ => "in den letzten 4 Wochen",
    };

    private static List<MachineFinding> BuildFindings(
        string range, IReadOnlyList<MachineStation> stations, MachineShiftComparison shifts, MachineCosts costs)
    {
        var phrase = PhraseOf(range);
        var findings = new List<MachineFinding>();

        // Kritische Stationen immer, sonst nur die mit der niedrigsten OEE: fünf gelbe Karten nebeneinander wären kein Hinweis mehr.
        var weakest = stations.Where(s => s.Oee is { Rating: not Rating.Ok }).OrderBy(s => s.Oee!.OeePercent).ToList();
        foreach (var s in weakest.Where(s => s.Oee!.Rating == Rating.Crit || s.Id == weakest[0].Id))
        {
            var oee = s.Oee!;
            var (factor, value) = new[] { ("Verfügbarkeit", oee.AvailabilityPercent), ("Leistung", oee.PerformancePercent), ("Qualität", oee.QualityPercent) }
                .OrderBy(f => f.Item2).First();
            findings.Add(new MachineFinding(
                $"oee-{s.Id}", s.Id, oee.Rating, $"{s.Id}: OEE {oee.OeePercent} %",
                $"Schwächster Faktor: {factor} {value} %.",
                $"Warum liegt die OEE von {s.Id} {phrase} bei {oee.OeePercent} %?"));
        }

        var top = costs.TopItems.Count > 0 ? costs.TopItems[0] : null;
        if (top is not null && top.Euro > 0)
        {
            findings.Add(new MachineFinding(
                "loss-top", top.Station, Rating.Warn, $"Größter Verlust: {top.Reason} ({top.Station})",
                $"{Fmt.Duration(TimeSpan.FromMinutes(top.Minutes))} Stillstand, {Fmt.Thousands(top.Euro)} €.",
                $"Was steckt hinter „{top.Reason}“ an {top.Station} {phrase} und was kostet es?"));
        }

        foreach (var s in stations.Where(s => s.ScrapPercent >= ScrapWarnPercent))
        {
            var reason = s.ScrapByReason.Count > 0 ? s.ScrapByReason[0] : null;
            findings.Add(new MachineFinding(
                $"scrap-{s.Id}", s.Id, s.ScrapPercent >= ScrapCritPercent ? Rating.Crit : Rating.Warn,
                $"{s.Id}: Ausschuss {Fmt.Round(s.ScrapPercent!.Value, 1).ToString("0.0", CultureInfo.GetCultureInfo("de-AT"))} %",
                reason is null ? "Ausschussquote über dem Richtwert von 2,5 %." : $"Häufigster Grund: {reason.Reason} ({reason.Percent.ToString("0", CultureInfo.GetCultureInfo("de-AT"))} %).",
                $"Woher kommt der Ausschuss an {s.Id} {phrase}?"));
        }

        foreach (var s in stations)
        {
            foreach (var trend in s.Trends.Where(t => t.Rating != Rating.Ok))
            {
                findings.Add(new MachineFinding(
                    $"process-{s.Id}-{trend.Key}", s.Id, trend.Rating, $"{s.Id}: {trend.Label} außerhalb der Toleranz",
                    $"{trend.OutsidePercent?.ToString("0.#", CultureInfo.GetCultureInfo("de-AT"))} % der Werte außerhalb des Bands.",
                    $"Warum liegt {trend.Label} an {s.Id} {phrase} so oft außerhalb der Toleranz?"));
            }

            if (s.Maintenance.Mould is { Rating: not Rating.Ok } mould)
            {
                findings.Add(new MachineFinding(
                    $"mould-{s.Id}", s.Id, mould.Rating, $"{s.Id}: Form bei {mould.WearPercent} % der Standzeit",
                    mould.LimitDay is null ? $"Noch {mould.RemainingStrokes} Hübe." : $"Grenze voraussichtlich {mould.LimitDay}",
                    $"Wann muss die Form an {s.Id} gewechselt werden und lässt sich das mit anderen Wartungen bündeln?"));
            }

            foreach (var item in s.Maintenance.Items.Where(i => i.Overdue))
            {
                findings.Add(new MachineFinding(
                    $"maintenance-{item.Id}", s.Id, Rating.Warn, $"{s.Id}: Wartung überfällig", item.Title,
                    $"Welche Wartung ist an {s.Id} überfällig und was bedeutet das?"));
            }
        }

        var rated = shifts.Shifts.Where(r => r.Oee is not null).ToList();
        if (rated.Count >= 2)
        {
            var worst = rated.OrderBy(r => r.Oee!.OeePercent).First();
            var best = rated.OrderByDescending(r => r.Oee!.OeePercent).First();
            if (best.Oee!.OeePercent - worst.Oee!.OeePercent >= 3)
            {
                findings.Add(new MachineFinding(
                    "shift-worst", null, Rating.Warn, $"{worst.Shift}schicht schwächer: OEE {worst.Oee.OeePercent} %",
                    $"{best.Shift}schicht {best.Oee.OeePercent} %, Anlaufverlust {Fmt.Thousands(worst.StartupLossPieces)} Stück nach den Wechseln.",
                    $"Warum ist die OEE in der {worst.Shift}schicht {phrase} niedriger als in der {best.Shift}schicht?"));
            }
        }

        return [.. findings
            .Select((f, index) => (f, index))
            .OrderByDescending(x => Rating.Rank(x.f.Rating))
            .ThenBy(x => x.index)
            .Take(MaxFindings)
            .Select(x => x.f)];
    }
}
