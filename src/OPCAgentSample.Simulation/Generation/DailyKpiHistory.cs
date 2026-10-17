using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Simulation.Scenario;

namespace OPCAgentSample.Simulation.Generation;

/// <summary>
/// Tageskennzahlen je Station (Historie B) vom Quartalsbeginn bis gestern.
/// Tage im Rohdatenfenster werden mit der Kennzahlen-Bibliothek aus den Rohdaten gerechnet; die Tage davor sind nur
/// verdichtet vorhanden und werden so synthetisiert, dass MTBF/MTTR der letzten 90 Tage und der Anteil ungeplanter
/// Stillstände je Monat die Zielwerte der Stammdaten treffen (die Rohdaten-Tage gehen dabei mit ein).
/// </summary>
public static class DailyKpiHistory
{
    private const int WindowDays = 90;
    private const double DurationJitter = 0.3;

    public static IReadOnlyList<DailyStationKpi> Build(HistoryGenerator generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var raw = RawDays(generator);
        var synthetic = new SyntheticDays(generator.Scenario, generator.Seed, raw).Build();
        return [.. synthetic.Concat(raw).OrderBy(d => d.Day).ThenBy(d => d.StationId, StringComparer.Ordinal)];
    }

    /// <summary>Stationen mit Tageskennzahlen: Pressen, Öfen, Verpackung.</summary>
    public static IReadOnlyList<string> Stations(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return [.. scenario.ProductionStations.Select(s => s.StationId).Concat(scenario.Ovens.Select(o => o.StationId))];
    }

    private static List<DailyStationKpi> RawDays(HistoryGenerator generator)
    {
        var scenario = generator.Scenario;
        var calendar = scenario.Calendar;
        var firstDay = calendar.ProductionDayOf(scenario.HistoryStartUtc + TimeSpan.FromHours(12));
        var demoDay = calendar.ProductionDayOf(scenario.DemoTimeUtc);
        var builders = new List<KpiBlockBuilder>();
        for (var day = firstDay; day < demoDay; day = day.AddDays(1))
        {
            builders.Add(new KpiBlockBuilder(calendar.DayPeriod(day), scenario.CostRates(), Stations(scenario)));
        }

        var end = builders[^1].Period.EndUtc;
        foreach (var record in generator.Generate(scenario.HistoryStartUtc, end))
        {
            foreach (var builder in builders.Where(b => record is LossEvent || b.Period.Contains(record.TimestampUtc)))
            {
                builder.Add(record);
            }
        }

        return [.. builders.SelectMany(builder =>
        {
            var block = builder.Snapshot(builder.Period.EndUtc);
            var day = DateOnly.Parse(block.Key, System.Globalization.CultureInfo.InvariantCulture);
            return Stations(scenario).Select(station => new DailyStationKpi(
                station,
                builder.Period.EndUtc,
                day,
                block.Figures(station),
                block.Stations.TryGetValue(station, out var s) ? s.Reasons : new Dictionary<string, ReasonFigures>()));
        })];
    }

    /// <summary>Synthetische Tage vor dem Rohdatenfenster, kalibriert auf die Zielwerte.</summary>
    private sealed class SyntheticDays(ScenarioDefinition scenario, int seed, IReadOnlyList<DailyStationKpi> raw)
    {
        private readonly ShiftCalendar _calendar = scenario.Calendar;
        private readonly DateOnly _rawStart = raw.Min(d => d.Day);
        private readonly DateOnly _windowStart = scenario.Calendar.ProductionDayOf(scenario.DemoTimeUtc).AddDays(-WindowDays);

        public List<DailyStationKpi> Build()
        {
            var days = new List<DailyStationKpi>();
            foreach (var target in scenario.ReliabilityTargets)
            {
                days.AddRange(StationDays(target));
            }

            return AddMaintenance(days);
        }

        private IEnumerable<DailyStationKpi> StationDays(ReliabilityTarget target)
        {
            var profile = StationProfile.For(scenario, target.StationId);
            var allDays = Days(scenario.DailyHistoryStart, _rawStart);
            var windowDays = allDays.Where(d => d >= _windowStart && _calendar.IsProductionDay(d)).ToList();
            var preDays = allDays.Where(d => d < _windowStart && _calendar.IsProductionDay(d)).ToList();
            var rawWindow = ProductionFigures.Sum(raw.Where(d => d.StationId == target.StationId && d.Day >= _windowStart).Select(d => d.Figures));

            var mtbf = target.MtbfHours * 60;
            var mttr = target.MttrMinutes;
            var baseRun = windowDays.Count * profile.BaseRunMinutes;
            var failures = (int)Math.Round((baseRun + rawWindow.BreakdownTime.TotalMinutes + rawWindow.RunTime.TotalMinutes) / (mtbf + mttr));
            var syntheticFailures = Math.Max(0, failures - rawWindow.BreakdownCount);
            var syntheticRepair = Math.Max(0, (mttr * failures) - rawWindow.BreakdownTime.TotalMinutes);
            var runCorrection = (baseRun - syntheticRepair + rawWindow.RunTime.TotalMinutes) - (mtbf * failures);

            var preFailures = windowDays.Count == 0 ? 0 : (int)Math.Round(syntheticFailures * (double)preDays.Count / windowDays.Count);
            var fabricShare = FabricShare(target, failures, syntheticFailures);

            var random = DeterministicRandom.For(seed, "kpi_day/" + target.StationId, 0);
            var windowRows = Distribute(windowDays, syntheticFailures, syntheticRepair, runCorrection, profile, fabricShare, random);
            var preRows = Distribute(preDays, preFailures, preFailures * mttr, 0, profile, fabricShare, random);
            var weekendRows = allDays.Where(d => !_calendar.IsProductionDay(d))
                .Select(d => Row(target.StationId, d, ProductionFigures.Empty, new Dictionary<string, ReasonFigures>()));
            return windowRows.Concat(preRows).Concat(weekendRows);
        }

        /// <summary>Anteil Gewebezuführung an den synthetischen Störungen, sodass er über 90 Tage den Zielwert trifft.</summary>
        private double FabricShare(ReliabilityTarget target, int windowFailures, int syntheticFailures)
        {
            var rawFabric = raw.Where(d => d.StationId == target.StationId && d.Day >= _windowStart)
                .Sum(d => d.Reasons.TryGetValue(LossReasons.FabricFeedFault, out var r) ? r.Count : 0);
            var wanted = (target.FabricFeedShare * windowFailures) - rawFabric;
            return syntheticFailures == 0 ? 0 : Math.Clamp(wanted / syntheticFailures, 0, 1);
        }

        private IEnumerable<DailyStationKpi> Distribute(
            List<DateOnly> days, int failures, double repairMinutes, double runCorrection,
            StationProfile profile, double fabricShare, Random random)
        {
            if (days.Count == 0) yield break;
            var weights = Enumerable.Range(0, failures).Select(_ => 1 + (DurationJitter * ((2 * random.NextDouble()) - 1))).ToArray();
            var weightSum = weights.Sum();
            var fabricFailures = (int)Math.Round(fabricShare * failures);
            var assigned = 0;
            for (var i = 0; i < days.Count; i++)
            {
                var count = (int)((long)(i + 1) * failures / days.Count) - (int)((long)i * failures / days.Count);
                var durations = Enumerable.Range(assigned, count).Select(k => repairMinutes * weights[k] / weightSum).ToList();
                var fabric = Enumerable.Range(assigned, count).Count(k => k < fabricFailures);
                assigned += count;
                yield return profile.Day(days[i], durations, fabric, runCorrection / days.Count, Row);
            }
        }

        /// <summary>Geplante Wartung je Monat (am Wochenende), sodass der Anteil ungeplanter Stillstände den Zielwert trifft.</summary>
        private List<DailyStationKpi> AddMaintenance(List<DailyStationKpi> synthetic)
        {
            var result = new List<DailyStationKpi>(synthetic);
            foreach (var target in scenario.UnplannedShares)
            {
                var all = synthetic.Concat(raw).Where(d => d.Day.Month == target.Month).ToList();
                var breakdown = all.Sum(d => d.Figures.BreakdownTime.TotalMinutes);
                var maintenance = all.Sum(d => d.Figures.MaintenanceTime.TotalMinutes);
                var missing = (breakdown * (1 - target.Share) / target.Share) - maintenance;
                var slots = result.Where(d => d.Day.Month == target.Month && d.Day.DayOfWeek == DayOfWeek.Saturday).ToList();
                if (missing <= 0 || slots.Count == 0) continue;
                var perSlot = TimeSpan.FromSeconds(Math.Round(missing * 60 / slots.Count));
                foreach (var slot in slots)
                {
                    result[result.IndexOf(slot)] = slot with { Figures = slot.Figures with { MaintenanceTime = slot.Figures.MaintenanceTime + perSlot } };
                }
            }

            return result;
        }

        private DailyStationKpi Row(string stationId, DateOnly day, ProductionFigures figures, IReadOnlyDictionary<string, ReasonFigures> reasons) =>
            new(stationId, _calendar.DayPeriod(day).EndUtc, day, figures, reasons);

        private static List<DateOnly> Days(DateOnly from, DateOnly toExclusive)
        {
            var days = new List<DateOnly>();
            for (var day = from; day < toExclusive; day = day.AddDays(1)) days.Add(day);
            return days;
        }
    }

    /// <summary>Typischer Produktionstag einer Station (Planzeit, Verluste, Leistung) für die synthetische Historie.</summary>
    private sealed record StationProfile(
        string StationId,
        double ScheduledMinutes,
        double PlannedStopMinutes,
        double OtherLossMinutes,
        double Performance,
        double ScrapRate,
        double TargetCycleSeconds,
        decimal ScrapCostEuro,
        int PlannedQuantity,
        string BreakdownReason,
        string WaitReason)
    {
        private const double MinutesPerDay = 1440;
        private const double SecondsPerMinute = 60;
        private const double PressSetupMinutes = 45;
        private const double PressOtherLossMinutes = 50;
        private const double PressPerformance = 0.82;
        private const double PressScrapRate = 0.009;
        private const double PackagingSetupMinutes = 30;
        private const double PackagingOtherLossMinutes = 40;
        private const double PackagingPerformance = 0.84;
        private const double PackagingScrapRate = 0.002;
        private const double OvenOtherLossMinutes = 10;
        private const double MinorStopMinutesPerDay = 6;
        private const string PressBreakdownReason = "Störung Presse/Hydraulik";

        public double BaseRunMinutes => ScheduledMinutes - PlannedStopMinutes - OtherLossMinutes;

        public static StationProfile For(ScenarioDefinition scenario, string stationId)
        {
            var station = scenario.ProductionStations.FirstOrDefault(s => s.StationId == stationId);
            if (station is null)
            {
                return new(stationId, MinutesPerDay, 0, OvenOtherLossMinutes, 0, 0, 0, 0, 0, LossReasons.OvenTemperatureFault, LossReasons.MinorStop);
            }

            var articleId = scenario.OrderAssignments.First(a => a.StationId == stationId).ArticleId;
            var plan = station.PlanEarly + station.PlanLate + station.PlanNight;
            if (station.IsPress)
            {
                var article = scenario.Article(articleId);
                return new(stationId, MinutesPerDay, PressSetupMinutes, PressOtherLossMinutes, PressPerformance, PressScrapRate,
                    article.TargetCycleSeconds, article.ScrapCostEuro, plan, LossReasons.FabricFeedFault, LossReasons.MixtureWait);
            }

            var carton = scenario.PackagingArticle(articleId);
            return new(stationId, MinutesPerDay, PackagingSetupMinutes, PackagingOtherLossMinutes, PackagingPerformance, PackagingScrapRate,
                carton.TargetCycleSeconds, carton.ScrapCostEuro, plan, LossReasons.StrapperFault, LossReasons.OvenWait);
        }

        public DailyStationKpi Day(
            DateOnly day, List<double> failureMinutes, int fabricFailures, double runCorrectionMinutes,
            Func<string, DateOnly, ProductionFigures, IReadOnlyDictionary<string, ReasonFigures>, DailyStationKpi> row)
        {
            var breakdown = TimeSpan.FromSeconds(Math.Round(failureMinutes.Sum() * SecondsPerMinute));
            var other = TimeSpan.FromSeconds(Math.Round(Math.Max(0, OtherLossMinutes + runCorrectionMinutes) * SecondsPerMinute));
            var scheduled = TimeSpan.FromMinutes(ScheduledMinutes);
            var plannedStop = TimeSpan.FromMinutes(PlannedStopMinutes);
            var run = scheduled - plannedStop - other - breakdown;
            var pieces = TargetCycleSeconds > 0 ? (long)Math.Round(run.TotalSeconds * Performance / TargetCycleSeconds) : 0;
            var scrap = (long)Math.Round(pieces * ScrapRate);
            var target = (decimal)TargetCycleSeconds;
            var figures = new ProductionFigures(
                scheduled, plannedStop, other + breakdown, TimeSpan.FromMinutes(MinorStopMinutesPerDay), pieces - scrap, scrap,
                pieces * target, (pieces - scrap) * target, PlannedQuantity, failureMinutes.Count, breakdown, TimeSpan.Zero,
                scrap * ScrapCostEuro, 0);

            var reasons = new Dictionary<string, ReasonFigures>();
            var fabricMinutes = failureMinutes.Take(fabricFailures).Sum();
            if (fabricFailures > 0) reasons[LossReasons.FabricFeedFault] = new(TimeSpan.FromSeconds(Math.Round(fabricMinutes * SecondsPerMinute)), fabricFailures);
            var otherFailures = failureMinutes.Count - fabricFailures;
            var otherReason = BreakdownReason == LossReasons.FabricFeedFault ? PressBreakdownReason : BreakdownReason;
            if (otherFailures > 0) reasons[otherReason] = new(breakdown - TimeSpan.FromSeconds(Math.Round(fabricMinutes * SecondsPerMinute)), otherFailures);
            if (other > TimeSpan.Zero) reasons[WaitReason] = new(other, 1);
            return row(StationId, day, figures, reasons);
        }
    }
}
