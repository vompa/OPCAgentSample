using System.Security.Claims;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Server.Live;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// HTTP-API der Weboberfläche unter <c>/api</c> (JSON, camelCase). Alle Endpunkte nehmen die Rolle als Query-Parameter
/// <c>role</c> (<c>shift_lead</c>, <c>hall_lead</c>, <c>production_lead</c>; Standard Schichtleiter).
/// <list type="bullet">
/// <item><c>GET /api/me</c>, <c>GET /api/overview</c>, <c>GET /api/inbox</c>, <c>POST /api/inbox/{id}/take</c>, <c>GET /api/feed</c></item>
/// <item><c>GET/PUT /api/favorites</c>, <c>POST /api/favorites/reset</c> (Tabelle <c>favorite</c>)</item>
/// <item><c>/api/history/*</c> (siehe <see cref="HistoryEndpoints"/>), <c>POST /api/ask</c> (siehe <see cref="AskEndpoint"/>)</item>
/// </list>
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/me", (string? role, HttpContext http, RoleResolver resolver,
                IRoleProfileSource profiles, IPlantStatusSource plant, IDemoClock clock,
                IInboxSource inbox, IFeedSource feed, DataStateMonitor dataState) =>
            resolver.WithRoleAsync(role, http, async context =>
            {
                var profile = profiles.Find(context.Role.Id);
                if (profile is null) return ApiResults.UnknownRole(role);

                var allRoles = await resolver.ListRolesAsync(http.RequestAborted);
                var options = profiles.RoleIds
                    .Where(id => allRoles.Any(r => r.Id == id))
                    .Select(id => profiles.Find(id)!.ToOption(id))
                    .ToList();
                var status = await plant.GetAsync(http.RequestAborted);
                var data = await dataState.GetAsync(http.RequestAborted);
                var openItems = (await inbox.GetAsync(context.Role.Id, context.Owner.Key, http.RequestAborted))
                    .Where(i => i.TakenBy is null).ToList();
                var feedItems = await VisibleFeedAsync(feed, context, http.RequestAborted);

                var (area, shift) = SplitScope(profile.ScopeText);
                return Results.Ok(new MeResponse(
                    new RoleView(context.Role.Id, context.Role.Title, profile.Label, profile.Scope, context.Role.ScopeDescription,
                        context.Role.Devices, context.Role.Rights, context.Role.MessageFilter, profile.FeedFilterText),
                    new UserView(context.User.Id, context.User.DisplayName, profile.Initials, profile.FirstName, profile.Title,
                        profile.MobileTitle, profile.WorkTime, profile.Notification),
                    options,
                    ClockOf(clock),
                    new StatusView(status.OpcConnected, status.McpToolCount, area, shift, profile.ScopeText, data.HistoryLoading, data.Warning),
                    new CountersView(openItems.Count, openItems.Any(i => i.Level == Rating.Crit), feedItems.Count, (feedItems.Count > 0 ? feedItems[0] : null))));
            }));
        return endpoints;
    }

    public static IEndpointRouteBuilder MapOverviewEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/overview", (string? role, HttpContext http, RoleResolver resolver,
                IHallOverviewSource source, IDemoClock clock) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await BuildOverviewAsync(context, source, clock, http.RequestAborted))));
        return endpoints;
    }

    /// <summary>Anlagenübersicht der Rolle (REST <c>/api/overview</c> und Ereignis <c>station</c> des Live-Stroms).</summary>
    internal static async Task<OverviewResponse> BuildOverviewAsync(
        RoleContext context, IHallOverviewSource source, IDemoClock clock, CancellationToken cancellationToken)
    {
        var snapshot = await source.GetAsync(context.Role.Id, cancellationToken);
        var groups = snapshot.Groups.Select(ToGroupView).ToList();
        var total = Sum(groups.Select(g => g.Counts));
        var scope = context.Role.ScopeHallId is null ? "plant" : "hall";
        return new OverviewResponse(
            scope, snapshot.Id, snapshot.Title, $"live {DemoTime.TimeText(clock.UtcNow)}", snapshot.Climate, total, groups);
    }

    internal static ClockView ClockOf(IDemoClock clock) =>
        new(clock.UtcNow, DemoTime.DateTimeText(clock.UtcNow), DemoTime.TimeText(clock.UtcNow), DemoTime.Greeting(clock.UtcNow));

    public static IEndpointRouteBuilder MapFavoritesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/favorites", (string? role, HttpContext http, RoleResolver resolver, FavoritesService favorites) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await favorites.GetAsync(context, http.RequestAborted))));

        endpoints.MapPut("/api/favorites", (string? role, FavoritesUpdate? body, HttpContext http, RoleResolver resolver,
                FavoritesService favorites, AuditLog audit) =>
            resolver.WithRoleAsync(role, http, async context =>
            {
                var (items, error) = FavoritesService.Validate(body);
                if (items is null) return ApiResults.Invalid(error!);

                var saved = await favorites.ReplaceAsync(context, items, http.RequestAborted);
                await audit.RecordAsync(AuditChannels.Rest, Principal(context), "favorites.put", $"{items.Count} Favoriten",
                    AuditOutcomes.Ok, cancellationToken: http.RequestAborted);
                return Results.Ok(saved);
            }));

        endpoints.MapPost("/api/favorites/reset", (string? role, HttpContext http, RoleResolver resolver,
                FavoritesService favorites, AuditLog audit) =>
            resolver.WithRoleAsync(role, http, async context =>
            {
                var saved = await favorites.ResetAsync(context, http.RequestAborted);
                await audit.RecordAsync(AuditChannels.Rest, Principal(context), "favorites.reset", null,
                    AuditOutcomes.Ok, cancellationToken: http.RequestAborted);
                return Results.Ok(saved);
            }));
        return endpoints;
    }

    public static IEndpointRouteBuilder MapInboxAndFeedEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inbox", (string? role, HttpContext http, RoleResolver resolver, IInboxSource inbox) =>
            resolver.WithRoleAsync(role, http, async context =>
                Results.Ok(await inbox.GetAsync(context.Role.Id, context.Owner.Key, http.RequestAborted))));

        endpoints.MapPost("/api/inbox/{id}/take", (string id, string? role, HttpContext http, RoleResolver resolver,
                IInboxSource inbox, IDemoClock clock, AuditLog audit, ILiveNotifier live) =>
            resolver.WithRoleAsync(role, http, async context =>
            {
                var item = await inbox.TakeAsync(context.Role.Id, context.Owner.Key, id, context.User.DisplayName,
                    DemoTime.TimeText(clock.UtcNow), http.RequestAborted);
                if (item is null) return Results.NotFound();

                await live.NotifyAsync(LiveChangeKind.Inbox, context.Owner.Key, http.RequestAborted);

                await audit.RecordAsync(AuditChannels.Rest, Principal(context), "inbox.take", id,
                    AuditOutcomes.Ok, cancellationToken: http.RequestAborted);
                return Results.Ok(item);
            }));

        endpoints.MapGet("/api/feed", (string? role, HttpContext http, RoleResolver resolver,
                IFeedSource feed, IRoleProfileSource profiles) =>
            resolver.WithRoleAsync(role, http, async context =>
            {
                var items = await VisibleFeedAsync(feed, context, http.RequestAborted);
                return Results.Ok(new FeedResponse(profiles.Find(context.Role.Id)?.FeedFilterText ?? string.Empty, items));
            }));
        return endpoints;
    }

    /// <summary>Meldungsfilter der Rolle: „alle“, „ab Warnung“, „nur Eskalationen“ (Spezifikation Abschnitt 9).</summary>
    internal static int MinimumRank(string messageFilter) => messageFilter switch
    {
        "ab Warnung" => Rating.Rank(Rating.Warn),
        "nur Eskalationen" => Rating.Rank(Rating.Crit),
        _ => 0,
    };

    internal static ClaimsPrincipal Principal(RoleContext context) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, context.User.Id), new Claim(ClaimTypes.Role, context.Role.Id)],
            authenticationType: "local"));

    private static async Task<IReadOnlyList<FeedEntry>> VisibleFeedAsync(
        IFeedSource feed, RoleContext context, CancellationToken cancellationToken)
    {
        var minimum = MinimumRank(context.Role.MessageFilter);
        return [.. (await feed.GetAllAsync(cancellationToken)).Where(e => Rating.Rank(e.Level) >= minimum)];
    }

    private static (string Area, string Shift) SplitScope(string scopeText)
    {
        var parts = scopeText.Split(" · ", 2);
        return (parts[0], parts.Length > 1 ? parts[1] : string.Empty);
    }

    private static RoleOption ToOption(this RoleProfile profile, string id) => new(id, profile.Label, profile.ShortLabel);

    private static StationGroupView ToGroupView(StationGroup group)
    {
        var counts = Count(group.Stations.Select(s => s.Status));
        var worst = counts.Problem > 0 ? StationStatus.Problem : counts.Warning > 0 ? StationStatus.Warning : StationStatus.Running;
        var stations = group.Stations.Select(s =>
            new StationView(s.Id, s.Status, StationStatus.Word(s.Status), s.Article, s.Kpi, s.Message)).ToList();
        return new StationGroupView(group.Id, group.Title, group.KpiLabel, stations.Count, worst, counts, stations);
    }

    private static StatusCounts Count(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        return new StatusCounts(
            list.Count(s => s == StationStatus.Problem), list.Count(s => s == StationStatus.Warning), list.Count(s => s == StationStatus.Running),
            list.Count(s => s == StationStatus.NoData));
    }

    private static StatusCounts Sum(IEnumerable<StatusCounts> counts)
    {
        var list = counts.ToList();
        return new StatusCounts(list.Sum(c => c.Problem), list.Sum(c => c.Warning), list.Sum(c => c.Running), list.Sum(c => c.NoData));
    }
}
