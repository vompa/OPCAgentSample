using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Observability;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Mcp;

/// <summary>
/// Filter um jeden Tool-Aufruf: schreibt genau einen Audit-Eintrag (<c>ok</c>, <c>denied</c> oder <c>error</c>) in den
/// <see cref="AuditLog"/> und lehnt jedes Tool ab, das nicht als nur lesend markiert ist (fail closed). Es gibt kein schreibendes Tool.
/// </summary>
public static class McpAuditFilter
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Create(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var audit = context.Services!.GetRequiredService<AuditLog>();
            var requests = context.Services!.GetRequiredService<IHttpContextAccessor>();
            var user = context.User;
            var action = context.Params?.Name ?? "unbekannt";
            var arguments = FormatArguments(context.Params?.Arguments);
            using var telemetry = McpToolTelemetry.Start(action, context.User);

            if (context.MatchedPrimitive is McpServerTool { ProtocolTool.Annotations.ReadOnlyHint: not true })
            {
                const string message = "Dieses Tool ist nicht als nur lesend markiert und wird nicht ausgeführt: Der Server steuert keine Maschinen.";
                await audit.RecordAsync(AuditChannels.Mcp, user, action, arguments, AuditOutcomes.Denied, message, cancellationToken);
                telemetry.Complete(AppTelemetry.Results.Denied);
                return ErrorResult(message);
            }

            try
            {
                var result = await next(context, cancellationToken);
                if (result.IsError == true)
                {
                    var denied = McpCaller.WasDeniedIn(requests.HttpContext);
                    await audit.RecordAsync(
                        AuditChannels.Mcp, user, action, arguments, denied ? AuditOutcomes.Denied : AuditOutcomes.Error, TextOf(result), cancellationToken);
                    telemetry.Complete(denied ? AppTelemetry.Results.Denied : AppTelemetry.Results.Error);
                }
                else
                {
                    await audit.RecordAsync(AuditChannels.Mcp, user, action, arguments, AuditOutcomes.Ok, cancellationToken: cancellationToken);
                    telemetry.Complete(AppTelemetry.Results.Ok);
                }

                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                telemetry.Complete(ex is McpDeniedException ? AppTelemetry.Results.Denied : AppTelemetry.Results.Error);
                await audit.RecordAsync(
                    AuditChannels.Mcp, user, action, arguments, ex is McpDeniedException ? AuditOutcomes.Denied : AuditOutcomes.Error, ex.Message, cancellationToken);
                throw;
            }
        };

    private static string? FormatArguments(IDictionary<string, JsonElement>? arguments) =>
        arguments is { Count: > 0 } ? string.Join(' ', arguments.Select(a => $"{a.Key}={a.Value.GetRawText()}")) : null;

    private static string TextOf(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static CallToolResult ErrorResult(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}

/// <summary>Registrierung des MCP-Servers unter <c>/mcp</c>: Tools, Resources, Prompts, Absicherung und Datenzugriff.</summary>
public static class McpRegistration
{
    private const string Instructions =
        "MCP-Server der Produktionshalle (Schleifmittel, Trennscheiben): nur lesender Zugriff auf Zustände, Kennzahlen, Wartung und Rückverfolgung, "
        + "keine Steuerung der Maschinen. Kennzahlen, Ampeln und Reichweiten berechnet der Server deterministisch; wähle Tools, verknüpfe die Ergebnisse "
        + "und formuliere. Jedes Ergebnis nennt Datenstand (source_until) und Quelle. Die Rolle des API-Keys bestimmt, was sichtbar ist "
        + "(Schichtleiter und Hallenleiterin: Halle A, Produktionsleiter: Werk). Zeiträume: aktuelle Schicht, vorherige Schicht, heute, gestern, "
        + "diese Woche, Vorwoche, KW 43, 24 h, 90 Tage oder von/bis. Resources: plant://structure, plant://shift-calendar, plant://kpi-definitions, "
        + "plant://machine/{id}/addressspace.";

    public static IServiceCollection AddMcpEndpoint(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddValidatedOptions<McpOptions>(configuration, McpOptions.SectionName);
        services.AddHttpContextAccessor();
        services.AddMcpSecurity(configuration);
        services.TryAddSingleton(_ => new ShiftCalendar());
        services.TryAddSingletonTimeProvider();

        // Datenzugriff ohne Bezug zur Anfrage (auch vom Postfach der Weboberfläche genutzt)
        services.TryAddSingleton<PlantQueries>();
        services.TryAddSingleton<MaterialReachService>();
        services.TryAddSingleton<OrderService>();
        services.TryAddSingleton<MaintenanceService>();
        services.TryAddSingleton<TimeRangeResolver>();
        services.TryAddSingleton<PlantCostRates>();

        // je Anfrage: Client und Rolle, Datenstand, Bereiche und Kennzahlenzugriff
        services.AddScoped<McpCaller>();
        services.AddScoped<McpSession>();
        services.AddScoped<ScopeResolver>();
        services.AddScoped<KpiAccess>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OPCAgentSample", Title = "OPC Agent Sample Halle A", Version = "1.0.0" };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithTools<LiveTools>()
            .WithTools<KpiTools>()
            .WithTools<MaintenanceTools>()
            .WithTools<TraceAndActionTools>()
            .WithResources<PlantResources>()
            .WithPrompts<McpPrompts>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(McpAuditFilter.Create));
        return services;
    }

    public static IApplicationBuilder UseMcpEndpoint(this IApplicationBuilder app) => app.UseMcpSecurity();

    /// <summary>Der Endpunkt verlangt einen API-Key (401 ohne) und hat ein eigenes Rate Limit (429 mit Retry-After).</summary>
    public static IEndpointRouteBuilder MapMcpEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMcp("/mcp")
            .RequireAuthorization(McpSecurityRegistration.AuthorizationPolicy)
            .RequireRateLimiting(McpSecurityRegistration.RateLimitPolicy);
        return endpoints;
    }
}
