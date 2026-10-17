using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace OPCAgentSample.Server.Security;

/// <summary>Namen für Authentifizierungsschema und Header des MCP-Zugangs.</summary>
public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

/// <summary>Rollen des MCP-Zugangs; die Kennungen sind die Rollen der Stammdaten (Tabelle <c>role</c>).</summary>
public static class McpRoles
{
    public const string ShiftLead = "shift_lead";
    public const string HallLead = "hall_lead";
    public const string ProductionLead = "production_lead";

    public static IReadOnlyList<string> All { get; } = [ShiftLead, HallLead, ProductionLead];

    public static bool IsKnown(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}

/// <summary>Ein API-Key der Konfiguration. Gespeichert ist nur der SHA-256-Hash.</summary>
public sealed class ApiKeyEntry
{
    /// <summary>Anzeigename des Clients; erscheint im Audit-Log als Client.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 des Keys als Hex-Zeichenfolge (64 Zeichen); erzeugbar mit <see cref="ApiKeyHasher.Hash"/>.</summary>
    public string KeySha256 { get; set; } = string.Empty;

    /// <summary><c>shift_lead</c>, <c>hall_lead</c> oder <c>production_lead</c>.</summary>
    public string Role { get; set; } = string.Empty;
}

/// <summary>Absicherung des MCP-Endpunkts (Abschnitt McpSecurity).</summary>
public sealed class McpSecurityOptions
{
    public const string SectionName = "McpSecurity";

    /// <summary>Anfragen je Minute und Client auf <c>/mcp</c>; danach 429 mit <c>Retry-After</c>.</summary>
    [Range(1, 100_000)]
    public int RateLimitPerMinute { get; set; } = 30;

    /// <summary>Gültige Keys. Ohne Eintrag lehnt <c>/mcp</c> jede Anfrage ab (fail closed).</summary>
    public IList<ApiKeyEntry> ApiKeys { get; } = [];
}

/// <summary>
/// API-Keys sind zufällige Tokens mit hoher Entropie, daher genügt ein schneller Hash (SHA-256). Für menschliche
/// Passwörter wäre das falsch.
/// </summary>
public static class ApiKeyHasher
{
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    public static bool TryParseHash(string? hex, out byte[] hash)
    {
        hash = [];
        if (hex is not { Length: 64 }) return false;
        try
        {
            hash = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Prüft die Schlüssel beim Start: Name, Hash (64 Hex-Zeichen) und bekannte Rolle.</summary>
internal sealed class McpSecurityOptionsValidator : IValidateOptions<McpSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, McpSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var key in options.ApiKeys)
        {
            if (key.Name.Length == 0 || !ApiKeyHasher.TryParseHash(key.KeySha256, out _) || !McpRoles.IsKnown(key.Role))
            {
                return ValidateOptionsResult.Fail(
                    "Jeder API-Key braucht Name, SHA-256-Hash (64 Hex-Zeichen) und eine Rolle (shift_lead, hall_lead, production_lead).");
            }
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Authentifiziert über <c>X-Api-Key: KEY</c> oder <c>Authorization: Bearer KEY</c>. Verglichen wird in konstanter Zeit
/// über alle Einträge, damit weder Timing noch die Position eines Treffers etwas über gültige Keys verrät. Der Client
/// bekommt Name und Rolle des Eintrags als Claims.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<McpSecurityOptions> security)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = ReadKey();
        if (presented is null) return Task.FromResult(AuthenticateResult.NoResult());

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        ApiKeyEntry? match = null;
        foreach (var entry in security.Value.ApiKeys)
        {
            if (ApiKeyHasher.TryParseHash(entry.KeySha256, out var expected)
                && CryptographicOperations.FixedTimeEquals(presentedHash, expected))
            {
                match = entry;
            }
        }

        if (match is null) return Task.FromResult(AuthenticateResult.Fail("Ungültiger API-Key."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, match.Name), new Claim(ClaimTypes.Role, match.Role)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        return base.HandleChallengeAsync(properties);
    }

    private string? ReadKey()
    {
        if (Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var header) && header.Count == 1
            && !string.IsNullOrWhiteSpace(header[0]))
        {
            return header[0];
        }

        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var token = authorization[prefix.Length..].Trim();
        return token.Length > 0 ? token : null;
    }
}

/// <summary>Antwort bei überschrittenem Limit: 429 mit <c>Retry-After</c> und verständlichem Text.</summary>
public static partial class RateLimitRejection
{
    private const int FallbackRetryAfterSeconds = 60;

    public static async ValueTask WriteAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
            ? Math.Max((int)Math.Ceiling(delay.TotalSeconds), 1)
            : FallbackRetryAfterSeconds;

        var http = context.HttpContext;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        http.Response.ContentType = "text/plain; charset=utf-8";
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OPCAgentSample.Server.Security.RateLimit");
        LogRejected(logger, ClientKey(http), http.Request.Path, retryAfter);

        await http.Response.WriteAsync($"Zu viele Anfragen. Bitte in {retryAfter} Sekunden erneut versuchen.", cancellationToken);
    }

    /// <summary>Name des Clients, sonst die IP-Adresse (bremst auch das Raten von Keys).</summary>
    public static string ClientKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate Limit überschritten: Client {Client} auf {Path}, erneut möglich in {Seconds} s")]
    private static partial void LogRejected(ILogger logger, string client, PathString path, int seconds);
}

/// <summary>Registrierung von Authentifizierung, Autorisierung und Rate Limit des MCP-Endpunkts.</summary>
public static class McpSecurityRegistration
{
    /// <summary>Name der Rate-Limit-Richtlinie von <c>/mcp</c>.</summary>
    public const string RateLimitPolicy = "mcp";

    /// <summary>Autorisierungsrichtlinie: angemeldet mit bekannter Rolle.</summary>
    public const string AuthorizationPolicy = "McpUser";

    public static IServiceCollection AddMcpSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<McpSecurityOptions>()
            .Bind(configuration.GetSection(McpSecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<McpSecurityOptions>, McpSecurityOptionsValidator>();

        services.AddAuthentication(ApiKeyDefaults.Scheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, _ => { });

        // Nur /mcp verlangt Anmeldung (RequireAuthorization am Endpunkt); die Web-API bleibt für die lokale Demo offen.
        services.AddAuthorizationBuilder().AddPolicy(AuthorizationPolicy, policy =>
            policy.RequireAuthenticatedUser().RequireRole(McpRoles.All));

        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicy, context =>
            {
                var permits = context.RequestServices.GetRequiredService<IOptions<McpSecurityOptions>>().Value.RateLimitPerMinute;
                return RateLimitPartition.GetFixedWindowLimiter(
                    RateLimitRejection.ClientKey(context),
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(permits, 1), Window = TimeSpan.FromMinutes(1) });
            });
            limiter.OnRejected = RateLimitRejection.WriteAsync;
        });
        return services;
    }

    /// <summary>Reihenfolge: erst Authentifizierung (kennt den Client), dann Rate Limit (bremst auch Fehlversuche), dann Autorisierung.</summary>
    public static IApplicationBuilder UseMcpSecurity(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        return app;
    }
}
