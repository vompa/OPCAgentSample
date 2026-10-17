namespace OPCAgentSample.Server.Api;

/// <summary>Konfiguration der Reports (Abschnitt <c>Reports</c>).</summary>
public sealed class ReportsOptions
{
    public const string SectionName = "Reports";

    /// <summary>Preise der Kostenschätzung.</summary>
    public ReportPrices Prices { get; set; } = new();

    /// <summary>Wie lange eine berechnete Zusammenfassung wiederverwendet wird.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Wie lange ein berechneter Maschinen-Report je Zeitraum wiederverwendet wird.</summary>
    public TimeSpan MachineCacheDuration { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>USD je 1 Mio. Tokens; die Standardwerte sind Richtpreise für das eingesetzte Claude-Modell.</summary>
public sealed class ReportPrices
{
    public decimal InputPerMillion { get; set; } = 0.10m;
    public decimal OutputPerMillion { get; set; } = 0.50m;
    public decimal CacheReadPerMillion { get; set; } = 0.01m;
    public decimal CacheWritePerMillion { get; set; } = 0.125m;
}

/// <summary>
/// <c>GET /api/reports/summary</c>: aggregierte Werte für die Report-Seite. Sichtbar für alle Rollen (mit der Demo-Sitzung);
/// die Antwort enthält keine Frageinhalte und keine Besitzer-Kennungen.
/// <c>GET /api/reports/machines?role=&amp;range=&amp;compare=</c>: Maschinen-Report der Halle A (<see cref="MachineReportService"/>);
/// <c>range</c> ist <c>shift</c>, <c>yesterday</c>, <c>week</c> oder <c>fourWeeks</c> (Standard je Rolle), <c>compare</c> (Standard
/// <c>true</c>) fügt die Vorperiode hinzu. Die Antwort enthält keine Personendaten.
/// </summary>
public static class ReportsEndpoint
{
    public static IEndpointRouteBuilder MapReportsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/reports/summary", async (ReportsService reports, HttpContext http) =>
            Results.Ok(await reports.GetSummaryAsync(http.RequestAborted)));

        endpoints.MapGet("/api/reports/machines", (string? role, string? range, bool? compare, HttpContext http, RoleResolver resolver,
                MachineReportCache cache, MachineReportService service) =>
            resolver.WithRoleAsync(role, http, async context =>
            {
                var chosen = string.IsNullOrWhiteSpace(range) ? MachineRanges.DefaultOf(context.Role.Id) : range.Trim();
                if (!MachineRanges.All.Contains(chosen, StringComparer.Ordinal))
                {
                    return ApiResults.Invalid($"Der Zeitraum '{range}' ist unbekannt. Gültig: {string.Join(", ", MachineRanges.All)}.");
                }

                var report = await cache.GetAsync(chosen, compare ?? true, service, http.RequestAborted);
                return Results.Ok(report with { Role = context.Role.Id, DefaultRange = MachineRanges.DefaultOf(context.Role.Id) });
            }));
        return endpoints;
    }
}
