using Microsoft.Extensions.DependencyInjection.Extensions;
using OPCAgentSample.Server.DemoAccess;
using OPCAgentSample.Server.Features.ChatHistory;

namespace OPCAgentSample.Server.Api;

/// <summary>Registrierung der Weboberfläche und ihrer HTTP-API.</summary>
public static class ApiRegistration
{
    /// <summary>Content-Security-Policy der Oberfläche: nur eigene Skripte, Stile und Schriften (lokal eingebunden), keine Inline-Skripte, Service Worker und Manifest der PWA von der eigenen Herkunft.</summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; "
        + "font-src 'self'; img-src 'self' data:; connect-src 'self'; worker-src 'self'; manifest-src 'self'; "
        + "object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";

    public static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.TryAddSingleton<IChatOwnerResolver, LocalUserChatOwnerResolver>();
        services.AddSingleton<RoleResolver>();
        services.AddSingleton<FavoritesService>();
        services.AddOptions<ReportsOptions>().BindConfiguration(ReportsOptions.SectionName);
        services.AddSingleton<ReportsService>();
        services.AddSingleton<MachineReportQueries>();
        services.AddSingleton<MachineReportCache>();
        services.AddScoped<MachineReportService>();
        services.AddScoped<ReportAskContextResolver>();
        return services;
    }

    /// <summary>Sicherheitsköpfe und statische Dateien der Oberfläche unter <c>/</c>.</summary>
    public static IApplicationBuilder UseWebUi(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                return Task.CompletedTask;
            });
            await next();
        });
        if (!ServesUi(app)) return app;
        app.UseDefaultFiles();
        app.UseStaticFiles();
        return app;
    }

    /// <summary>
    /// <c>Web:ServeUi</c> (Standard <see langword="true"/>): Der Server liefert <c>wwwroot</c> aus. In Azure liegt die Oberfläche
    /// auf einer eigenen Static Web App; der Server ist dort reine API (<c>false</c>).
    /// </summary>
    private static bool ServesUi(IApplicationBuilder app) =>
        app.ApplicationServices.GetRequiredService<IConfiguration>().GetValue("Web:ServeUi", true);

    public static IEndpointRouteBuilder MapApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints
            .MapMeEndpoints()
            .MapOverviewEndpoint()
            .MapFavoritesEndpoints()
            .MapInboxAndFeedEndpoints()
            .MapHistoryEndpoints()
            .MapAskEndpoint()
            .MapLiveStreamEndpoint()
            .MapDemoAccessEndpoints()
            .MapReportsEndpoint();
        return endpoints;
    }
}
