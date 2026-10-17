using System.Text.Json;
using System.Text.Json.Serialization;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.MasterData;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server;

[Collection(InfrastructureFixture.Name)]
public sealed class MasterDataSeedTests(InfrastructureFixture infrastructure)
{
    private static readonly ShiftCalendar s_calendar = new();
    private static readonly JsonSerializerOptions s_json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly MasterDataSnapshot s_seed = MasterDataSeed.Build();

    private static Dictionary<string, int> CountRows(MasterDataSnapshot data) => new()
    {
        ["article"] = data.Articles.Count,
        ["mixture"] = data.Mixtures.Count,
        ["mixture_batch"] = data.MixtureBatches.Count,
        ["mould"] = data.Moulds.Count,
        ["production_order"] = data.Orders.Count,
        ["cost_rate"] = data.CostRates.Count,
        ["holiday"] = data.Holidays.Count,
        ["calendar_day"] = data.CalendarDays.Count,
        ["maintenance_plan"] = data.MaintenancePlans.Count,
        ["maintenance_history"] = data.MaintenanceHistory.Count,
        ["role"] = data.Roles.Count,
        ["user"] = data.Users.Count,
        ["escalation_rule"] = data.EscalationRules.Count,
    };

    /// <summary>Inhalt unabhängig von der Reihenfolge: je Sammlung die Elemente als JSON, sortiert.</summary>
    private static string Serialize(MasterDataSnapshot data)
    {
        static IEnumerable<string> Items<T>(string name, IEnumerable<T> items) =>
            items.Select(i => name + JsonSerializer.Serialize(i, s_json)).Order(StringComparer.Ordinal);

        IEnumerable<string>[] parts =
        [
            Items("article", data.Articles.Values), Items("mixture", data.Mixtures.Values), Items("batch", data.MixtureBatches.Values),
            Items("mould", data.Moulds), Items("order", data.Orders), Items("cost", data.CostRates),
            Items("plan", data.MaintenancePlans), Items("history", data.MaintenanceHistory), Items("role", data.Roles.Values),
            Items("user", data.Users), Items("rule", data.EscalationRules.Values), Items("holiday", data.Holidays),
            Items("day", data.CalendarDays),
        ];
        return string.Join(Environment.NewLine, parts.SelectMany(p => p));
    }

    [Fact]
    public void Seed_builds_the_identical_snapshot_every_time_with_all_collections_filled()
    {
        var again = MasterDataSeed.Build();

        Assert.Equal(Serialize(s_seed), Serialize(again));
        Assert.All(CountRows(s_seed), pair => Assert.True(pair.Value > 0, $"Sammlung {pair.Key} ist leer."));
    }

    [Fact]
    public async Task Seeding_redis_twice_gives_the_same_master_data()
    {
        await using var services = await infrastructure.CreateServicesAsync();
        var store = services.Get<MasterDataStore>();
        await store.ReseedAsync();
        var first = await store.GetAsync();
        var firstCounts = CountRows(first);

        await store.ReseedAsync();
        var second = await store.GetAsync();

        Assert.Equal(firstCounts, CountRows(second));
        Assert.Equal(Serialize(first), Serialize(second));
        Assert.Equal(Serialize(s_seed), Serialize(second));
        Assert.True(await store.IsSeededAsync());
    }

    [Fact]
    public async Task A_new_process_reads_the_master_data_back_from_redis()
    {
        await using var services = await infrastructure.CreateServicesAsync();
        await services.Get<MasterDataStore>().GetAsync();
        using var freshStore = new MasterDataStore(services.Get<IConnectionMultiplexer>());

        var loaded = await freshStore.GetAsync();

        Assert.Equal(Serialize(s_seed), Serialize(loaded));
    }

    [Fact]
    public async Task Master_data_is_written_again_after_redis_was_flushed()
    {
        await using var services = await infrastructure.CreateServicesAsync();
        var store = services.Get<MasterDataStore>();
        await store.GetAsync();
        await infrastructure.FlushRedisAsync();
        Assert.False(await store.IsSeededAsync());

        await store.ReseedAsync();

        Assert.True(await store.IsSeededAsync());
    }

    [Fact]
    public void Maintenance_plan_contains_all_dates_of_the_specification()
    {
        var plans = s_seed.MaintenancePlans;
        var now = SimulationClock.DemoStart;

        Assert.Equal(24, plans.Count);

        var door = Assert.Single(plans, p => p.StationId == "OF-01" && p.Kind == MaintenanceKind.SafetyInspection);
        Assert.True(door.IsSafetyRelevant);
        Assert.Equal(2, (s_calendar.ToLocal(now).Date - s_calendar.ToLocal(door.DueUtc).Date).Days);

        var mould = Assert.Single(plans, p => p.Id == "MP-RT02-MOULD");
        Assert.Equal("W-3381", mould.WorkOrder);
        Assert.Equal(new DateTime(2026, 10, 15, 11, 30, 0), s_calendar.ToLocal(mould.DueUtc).DateTime);

        Assert.Equal(new DateTime(2026, 10, 16, 14, 0, 0), s_calendar.ToLocal(plans.Single(p => p.Id == "MP-VP01-STRAP").DueUtc).DateTime);
        Assert.Equal(new DateOnly(2026, 10, 19), DateOnly.FromDateTime(s_calendar.ToLocal(plans.Single(p => p.Id == "MP-RT02-OIL").DueUtc).DateTime));
        Assert.Equal(60, plans.Single(p => p.Id == "MP-OF02-SENSORS").DurationMinutes);
        Assert.True(plans.Single(p => p.Id == "MP-RT04-MOULD").IsForecast);
        Assert.Equal(new DateOnly(2026, 11, 2), DateOnly.FromDateTime(s_calendar.ToLocal(plans.Single(p => p.Id == "MP-RT04-HYD").DueUtc).DateTime));
        Assert.Equal(4, plans.Count(p => p.Title.StartsWith("Jährliche Sicherheitsprüfung", StringComparison.Ordinal)));
        Assert.Equal(4, plans.Count(p => p.Title.StartsWith("Waage kalibrieren (quartalsweise)", StringComparison.Ordinal)));
    }

    [Fact]
    public void Plant_shutdown_is_a_maintenance_window_from_24_december_to_6_january()
    {
        var window = s_seed.MaintenancePlans.Where(p => p.WindowEndUtc != null && p.Title.Contains("Betriebsurlaub", StringComparison.Ordinal)).ToList();

        Assert.Equal(["OF-01", "OF-02", "RT-01", "RT-02", "RT-03", "RT-04"], window.Select(p => p.StationId).Order());
        Assert.All(window, p =>
        {
            Assert.Equal(new DateOnly(2026, 12, 24), DateOnly.FromDateTime(s_calendar.ToLocal(p.DueUtc).DateTime));

            // Das Fensterende ist exklusiv: der Neustart ist Do 07.01.2027 um 06:00 Ortszeit.
            Assert.Equal(new DateTime(2027, 1, 7, 6, 0, 0), s_calendar.ToLocal(p.WindowEndUtc!.Value).DateTime);
        });

        var calendar = s_seed.CalendarDays.ToDictionary(d => d.ProductionDay);
        Assert.False(calendar[new DateOnly(2026, 12, 29)].IsProductionDay);
        Assert.False(calendar[new DateOnly(2027, 1, 5)].IsProductionDay);
        Assert.True(calendar[new DateOnly(2027, 1, 7)].IsProductionDay);
        Assert.True(calendar[new DateOnly(2026, 10, 15)].IsProductionDay);
        Assert.False(calendar[new DateOnly(2026, 10, 17)].IsProductionDay);
    }

    [Fact]
    public void Maintenance_history_contains_the_july_scale_calibrations()
    {
        var history = s_seed.MaintenanceHistory.OrderBy(h => h.StationId, StringComparer.Ordinal).ToList();

        Assert.Equal(["RT-01", "RT-02"], history.Select(h => h.StationId));
        Assert.All(history, h =>
        {
            Assert.Equal(7, s_calendar.ToLocal(h.DoneAtUtc).Month);
            Assert.Equal("unauffällig", h.Result);
        });
    }

    [Fact]
    public void Holidays_match_the_shift_calendar_of_the_domain()
    {
        var holidays = s_seed.Holidays.Select(h => h.Date).OrderBy(d => d).ToList();

        Assert.Equal(ShiftCalendar.AustrianHolidays.OrderBy(d => d), holidays);
    }

    [Fact]
    public void Every_role_has_five_default_favorites_with_short_titles()
    {
        var roles = s_seed.Users.ToDictionary(u => u.Id, u => u.RoleId);

        Assert.Equal(3, roles.Count);
        Assert.Equal(["hall_lead", "production_lead", "shift_lead"], roles.Values.Order());
        foreach (var role in roles.Values)
        {
            var defaults = FavoritesService.DefaultsOf(role);
            Assert.Equal(5, defaults.Count);
            Assert.All(defaults, f => Assert.InRange(f.ShortTitle.Length, 1, Favorite.MaxShortTitleLength));
        }

        Assert.Contains(FavoritesService.DefaultsOf("shift_lead"), f => f.ShortTitle == "Schichtübergabe");
        Assert.Contains(FavoritesService.DefaultsOf("production_lead"), f => f.ShortTitle == "Kosten der Woche");
    }

    [Fact]
    public void Roles_users_and_escalation_rules_follow_the_specification()
    {
        var roles = s_seed.Roles;
        Assert.Equal("A", roles["shift_lead"].ScopeHallId);
        Assert.Equal("A", roles["hall_lead"].ScopeHallId);
        Assert.Null(roles["production_lead"].ScopeHallId);
        Assert.Contains("Wartungsfreigabe", roles["hall_lead"].Rights);

        var names = s_seed.Users.Select(u => u.DisplayName).Order().ToList();
        Assert.Equal(["Katharina Gruber", "Martin Huber", "Stefan Wallner"], names);

        var safety = s_seed.EscalationRules["ER-SAFETY-OVERDUE"];
        Assert.True(safety.SafetyRelevantOnly);
        Assert.Equal(24 * 60, safety.ThresholdMinutes);
        Assert.Equal("production_lead", safety.TargetRoleId);
    }

    [Fact]
    public void Orders_articles_moulds_and_mixtures_carry_the_scenario_values()
    {
        var orders = s_seed.Orders.OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
        Assert.Equal(23, orders.Count);
        Assert.Contains(orders, o => o.Id == "4688");
        Assert.Contains(orders, o => o.Id == "4733");

        var done = orders.Single(o => o.Id == "4702");
        Assert.Equal(8400, done.ProducedQuantity);
        Assert.Equal(OrderState.Completed, done.State);
        Assert.Equal(new DateTime(2026, 10, 15, 8, 55, 0), s_calendar.ToLocal(done.FinishedAtUtc!.Value).DateTime);

        var order4711 = orders.Single(o => o.Id == "4711");
        Assert.Equal(76, (int)Math.Round(order4711.ProducedQuantity * 100.0 / order4711.TargetQuantity));
        Assert.Equal(27_697, orders.Single(o => o.Id == "4688").TargetQuantity);

        Assert.Equal(["F-125-07", "F-180-03"], s_seed.Moulds.Select(m => m.Id).Order());
        Assert.Equal(4100, s_seed.Moulds.Single(m => m.Id == "F-180-03").AverageStrokesPerDay);

        var batch = s_seed.MixtureBatches["M-2410-17"];
        Assert.Equal("A60", batch.MixtureId);
        Assert.Equal(1460, batch.BulkDensityGramsPerLiter);
        Assert.Equal(new DateTime(2026, 10, 14, 21, 15, 0), s_calendar.ToLocal(batch.ProducedAtUtc).DateTime);
        Assert.Equal([115, 125, 180, 230], s_seed.Articles.Values.Select(a => a.DiameterMm).Distinct().Order());
    }

    [Fact]
    public void Cost_rates_are_the_calibrated_values_of_the_scenario()
    {
        var rates = s_seed.CostRates;

        Assert.Equal(8, rates.Count(r => r.Kind == CostRateKind.DowntimeMinute));
        Assert.Equal(11, rates.Count(r => r.Kind == CostRateKind.ScrapPiece));
        Assert.All(rates, r =>
        {
            Assert.True(r.IsCalibrated);
            Assert.Null(r.Note);
            Assert.True(r.Euro > 0);
        });
        Assert.Equal(33.7m, rates.Single(r => r.StationId == "RT-03").Euro);
    }

    [Fact]
    public void All_seed_timestamps_are_utc_and_relative_to_the_demo_clock()
    {
        Assert.All(s_seed.MaintenancePlans, p => Assert.Equal(TimeSpan.Zero, p.DueUtc.Offset));
        Assert.All(s_seed.Orders, o => Assert.Equal(TimeSpan.Zero, o.DueUtc.Offset));
        Assert.All(s_seed.Orders.Where(o => o.State == OrderState.Running), o => Assert.True(o.StartedAtUtc <= SimulationClock.DemoStart));
    }
}
