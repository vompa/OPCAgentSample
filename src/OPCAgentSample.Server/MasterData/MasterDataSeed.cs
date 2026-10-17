using OPCAgentSample.Domain;

namespace OPCAgentSample.Server.MasterData;

/// <summary>
/// Baut die Stammdaten aus den eingebetteten JSON-Dateien (<c>Seed/*.json</c>). Relative Termine beziehen sich auf die feste
/// Demo-Uhr <see cref="SimulationClock.DemoStart"/>, nicht auf die laufende Uhr. Das Ergebnis ist bei jedem Aufruf dasselbe.
/// </summary>
public static class MasterDataSeed
{
    public static MasterDataSnapshot Build()
    {
        var calendarFile = SeedFiles.Read<CalendarFile>("calendar.json");
        var shiftCalendar = new ShiftCalendar(calendarFile.Holidays.Select(h => SeedTime.ParseDay(h.Date)).ToList());
        var time = new SeedTime(shiftCalendar, SimulationClock.DemoStart);

        var articles = SeedFiles.Read<ArticlesFile>("articles.json");
        var production = SeedFiles.Read<ProductionFile>("production.json");
        var users = SeedFiles.Read<UsersFile>("users.json");
        var maintenance = SeedFiles.Read<MaintenanceFile>("maintenance.json");
        var (holidays, days) = Calendar(calendarFile, shiftCalendar);

        return new MasterDataSnapshot
        {
            Articles = articles.Articles.Select(a => new Article
            {
                Id = a.Id,
                Name = a.Name,
                DiameterMm = a.DiameterMm,
                Grit = a.Grit,
                ThicknessMm = a.ThicknessMm,
                ArticleGroup = a.ArticleGroup ?? $"Ø {a.DiameterMm} {a.Grit}",
                PiecesPerCarton = a.PiecesPerCarton,
                FillWeightTargetGrams = a.FillWeightTargetGrams,
                FillWeightToleranceGrams = a.FillWeightToleranceGrams,
            }).ToDictionary(a => a.Id),
            Mixtures = articles.Mixtures.Select(m => new Mixture { Id = m.Id, Name = m.Name, Grit = m.Grit }).ToDictionary(m => m.Id),
            MixtureBatches = articles.MixtureBatches.Select(b => new MixtureBatch
            {
                Id = b.Id,
                MixtureId = b.MixtureId,
                BulkDensityGramsPerLiter = b.BulkDensityGramsPerLiter,
                ProducedAtUtc = time.Parse(b.ProducedAt),
                ProcessableUntilUtc = time.ParseOptional(b.ProcessableUntil),
                Remark = b.Remark,
            }).ToDictionary(b => b.Id),
            Moulds = [.. production.Moulds.Select(m => new Mould
            {
                Id = m.Id,
                StationId = m.StationId,
                DiameterMm = m.DiameterMm,
                StrokesSinceChange = m.StrokesSinceChange,
                StrokeLimit = m.StrokeLimit,
                AverageStrokesPerDay = m.AverageStrokesPerDay,
                Nests = m.Nests,
                MountedAtUtc = time.Parse(m.MountedAt),
            })],
            Orders = [.. production.Orders.Select(o => new ProductionOrder
            {
                Id = o.Id,
                ArticleId = o.ArticleId,
                HallId = o.HallId,
                StationId = o.StationId,
                TargetQuantity = o.TargetQuantity,
                ProducedQuantity = o.ProducedQuantity,
                State = o.State,
                StartedAtUtc = time.ParseOptional(o.StartedAt),
                FinishedAtUtc = time.ParseOptional(o.FinishedAt),
                DueUtc = time.Parse(o.Due),
                Remark = o.Remark,
            })],
            CostRates = CostRates(SeedFiles.Read<CostRatesFile>("cost-rates.json")),
            MaintenancePlans = [.. maintenance.Plans.Select(p => new MaintenancePlan
            {
                Id = p.Id,
                StationId = p.StationId,
                Title = p.Title,
                Kind = p.Kind,
                DueUtc = time.Parse(p.Due),
                WindowEndUtc = time.ParseOptional(p.WindowEnd),
                DurationMinutes = p.DurationMinutes,
                IsSafetyRelevant = p.SafetyRelevant,
                IsForecast = p.Forecast,
                WorkOrder = p.WorkOrder,
                MouldId = p.MouldId,
                Hint = p.Hint,
            })],
            MaintenanceHistory = [.. maintenance.History.Select(h => new MaintenanceHistory
            {
                Id = h.Id,
                StationId = h.StationId,
                Title = h.Title,
                Kind = h.Kind,
                DoneAtUtc = time.Parse(h.DoneAt),
                Result = h.Result,
            })],
            Roles = users.Roles.Select(r => new AppRole
            {
                Id = r.Id,
                Title = r.Title,
                ScopeHallId = r.ScopeHallId,
                ScopeDescription = r.ScopeDescription,
                Devices = r.Devices,
                Rights = [.. r.Rights],
                MessageFilter = r.MessageFilter,
            }).ToDictionary(r => r.Id),
            Users = [.. users.Users.Select(u => new AppUser { Id = u.Id, DisplayName = u.DisplayName, RoleId = u.RoleId })],
            EscalationRules = users.EscalationRules.Select(r => new EscalationRule
            {
                Id = r.Id,
                Name = r.Name,
                Subject = r.Subject,
                SafetyRelevantOnly = r.SafetyRelevantOnly,
                ThresholdMinutes = r.ThresholdMinutes,
                TargetRoleId = r.TargetRoleId,
            }).ToDictionary(r => r.Id),
            Holidays = holidays,
            CalendarDays = days,
        };
    }

    private static List<CostRate> CostRates(CostRatesFile file)
    {
        var note = file.IsCalibrated ? null : file.Status;
        var downtime = file.DowntimeMinuteByStation.Select(r => new CostRate
        {
            Id = $"downtime-minute:{r.Key}",
            Kind = CostRateKind.DowntimeMinute,
            StationId = r.Key,
            Euro = r.Value,
            IsCalibrated = file.IsCalibrated,
            Note = note,
        });
        var scrap = file.ScrapPieceByArticle.Select(r => new CostRate
        {
            Id = $"scrap-piece:{r.Key}",
            Kind = CostRateKind.ScrapPiece,
            ArticleId = r.Key,
            Euro = r.Value,
            IsCalibrated = file.IsCalibrated,
            Note = note,
        });
        return [.. downtime, .. scrap];
    }

    private static (List<Holiday> Holidays, List<CalendarDay> Days) Calendar(CalendarFile file, ShiftCalendar shiftCalendar)
    {
        var holidays = file.Holidays.Select(h => new Holiday { Date = SeedTime.ParseDay(h.Date), Name = h.Name }).OrderBy(h => h.Date).ToList();
        var closures = file.Closures.Select(c => (From: SeedTime.ParseDay(c.From), Until: SeedTime.ParseDay(c.Until), c.Name)).ToList();
        var days = new List<CalendarDay>();
        for (var day = SeedTime.ParseDay(file.FirstDay); day <= SeedTime.ParseDay(file.LastDay); day = day.AddDays(1))
        {
            var closure = closures.FirstOrDefault(c => day >= c.From && day <= c.Until);
            var holiday = holidays.FirstOrDefault(h => h.Date == day);
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            days.Add(new CalendarDay
            {
                ProductionDay = day,
                IsProductionDay = closure.Name is null && shiftCalendar.IsProductionDay(day),
                Remark = closure.Name ?? holiday?.Name ?? (isWeekend ? "Wochenende" : null),
            });
        }

        return (holidays, days);
    }
}
