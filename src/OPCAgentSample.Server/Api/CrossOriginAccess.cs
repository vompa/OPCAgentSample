namespace OPCAgentSample.Server.Api;

/// <summary>
/// Cross-Origin-Zugriff (CORS) für die getrennt gehostete Oberfläche (Abschnitt <c>Cors</c>). Ohne Herkunft ist die Oberfläche
/// nur von der eigenen Herkunft aus erreichbar (Standard, lokal liefert der Server <c>wwwroot</c> selbst).
/// </summary>
/// <remarks>
/// Das Cookie <c>demo_session</c> bleibt <c>SameSite=Strict</c>: <c>opc.vompa.dev</c> (Oberfläche) und <c>api.opc.vompa.dev</c>
/// (Server) sind verschiedene Herkünfte, aber dieselbe <em>Site</em> (<c>vompa.dev</c>, registrierbare Domain). Für „strict“ zählt die
/// Site, nicht die Herkunft; der Browser sendet das Cookie also bei <c>fetch(..., { credentials: 'include' })</c> mit. Gleiches gilt
/// lokal für <c>localhost:4173</c> → <c>localhost:8080</c>.
/// </remarks>
public sealed class WebUiCorsOptions
{
    public const string SectionName = "Cors";
    public const string PolicyName = "web-ui";

    /// <summary>Erlaubte Herkünfte (Schema, Host, optional Port; ohne Pfad), z. B. <c>https://opc.vompa.dev</c>.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Die bereinigten Herkünfte; wirft bei ungültigen Einträgen mit Klartext, damit der Start scheitert statt still zu öffnen.</summary>
    public IReadOnlyList<string> ParseOrigins()
    {
        var result = new List<string>();
        foreach (var raw in AllowedOrigins)
        {
            var text = raw.Trim().TrimEnd('/');
            if (text.Length == 0) continue;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.PathAndQuery != "/" || text == "*")
                throw new InvalidOperationException($"Cors:AllowedOrigins: '{raw}' ist keine Herkunft (erwartet z. B. https://opc.vompa.dev).");
            result.Add(uri.GetLeftPart(UriPartial.Authority));
        }

        return result;
    }
}

public static class CrossOriginRegistration
{
    /// <summary>
    /// Registriert CORS; die Herkünfte werden erst beim ersten Gebrauch aus der Konfiguration gelesen (nicht beim Registrieren),
    /// damit auch spät hinzugefügte Konfiguration wirkt (Tests, Host-Builder).
    /// </summary>
    public static IServiceCollection AddWebUiCors(this IServiceCollection services)
    {
        services.AddCors();
        services.AddOptions<Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions>().Configure<IConfiguration>((cors, configuration) =>
        {
            var origins = ReadOrigins(configuration);
            if (origins.Count == 0) return;

            cors.AddPolicy(WebUiCorsOptions.PolicyName, policy => policy
                .WithOrigins([.. origins])
                .AllowCredentials()
                .WithMethods("GET", "POST", "PUT", "OPTIONS")
                .WithHeaders("Content-Type", "Accept", "Authorization", "Last-Event-ID")
                .WithExposedHeaders("Retry-After")
                .SetPreflightMaxAge(TimeSpan.FromHours(1)));
        });
        return services;
    }

    /// <summary>Schaltet CORS ein, sobald Herkünfte konfiguriert sind; vor der Anmeldeprüfung, damit auch 401 die Köpfe trägt.</summary>
    public static IApplicationBuilder UseWebUiCors(this WebApplication app)
    {
        if (ReadOrigins(app.Configuration).Count > 0) app.UseCors(WebUiCorsOptions.PolicyName);
        return app;
    }

    private static IReadOnlyList<string> ReadOrigins(IConfiguration configuration)
    {
        var options = new WebUiCorsOptions();
        configuration.GetSection(WebUiCorsOptions.SectionName).Bind(options);
        return options.ParseOrigins();
    }
}
