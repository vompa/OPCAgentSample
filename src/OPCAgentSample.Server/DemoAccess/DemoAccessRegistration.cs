using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.DemoAccess;

/// <summary>Registrierung des Demo-Zugangs und der Auswertung von <c>X-Forwarded-*</c> hinter dem TLS-Proxy.</summary>
public static class DemoAccessRegistration
{
    public const string ForwardedHeadersSection = "ForwardedHeaders";

    /// <summary>
    /// Optionen mit Prüfung beim Start, Sitzungsleser, Anmelde-Bremse und Besitzer des Verlaufs. Ob der Zugang eingeschaltet ist
    /// (<c>Demo:Enabled</c>), wird erst zur Laufzeit aus den Optionen gelesen – nicht beim Registrieren, damit auch spät
    /// hinzugefügte Konfiguration (Tests, Host-Builder) wirkt.
    /// </summary>
    public static IServiceCollection AddDemoAccess(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DemoOptions>().Bind(configuration.GetSection(DemoOptions.SectionName)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DemoOptions>, DemoOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DemoSessionReader>();
        services.TryAddSingleton<DemoLoginThrottle>();

        services.Replace(ServiceDescriptor.Singleton<IChatOwnerResolver, DemoSessionChatOwnerResolver>());
        return services;
    }

    /// <summary>
    /// Konfiguriert die Auswertung von <c>X-Forwarded-For</c>/<c>-Proto</c>; eingeschaltet wird sie erst durch
    /// <see cref="UseForwardedHeadersIfEnabled"/> (Abschnitt <c>ForwardedHeaders:Enabled</c>, Standard aus). Nötig hinter
    /// einem TLS-terminierenden Proxy (Azure Container Apps): Ohne die Auswertung wäre das Cookie nicht <c>Secure</c>, und die
    /// Anmelde-Bremse sähe nur die Adresse des Proxys. Nur einschalten, wenn der Proxy der einzige Zugangsweg ist,
    /// sonst könnte ein Client die Header fälschen.
    /// </summary>
    public static IServiceCollection AddForwardedHeadersOptions(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1; // nur der Eintrag des direkten Proxys, nicht die ganze Kette
            options.KnownIPNetworks.Clear(); // Adressen des Proxys sind in Container Apps nicht vorab bekannt
            options.KnownProxies.Clear();
        });
        return services;
    }

    /// <summary>Ganz vorn in der Pipeline; ohne <c>ForwardedHeaders:Enabled</c> geschieht nichts.</summary>
    public static WebApplication UseForwardedHeadersIfEnabled(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Configuration.GetValue<bool>($"{ForwardedHeadersSection}:Enabled")) _ = app.UseForwardedHeaders();
        return app;
    }

    /// <summary>Verlangt bei eingeschaltetem Zugang für <see cref="DemoAccessRules.RequiresSession"/> eine gültige Sitzung (sonst 401 als Problem-JSON).</summary>
    public static IApplicationBuilder UseDemoAccess(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var enabled = context.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled;
            if (enabled && DemoAccessRules.RequiresSession(context.Request.Path)
                && context.RequestServices.GetRequiredService<DemoSessionReader>().Read(context) is null)
            {
                context.Response.Headers.CacheControl = "no-store";
                await Results.Problem(
                    title: "Nicht angemeldet",
                    detail: "Für diese Demo ist ein Zugangscode nötig.",
                    statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
                return;
            }

            await next();
        });
    }
}
