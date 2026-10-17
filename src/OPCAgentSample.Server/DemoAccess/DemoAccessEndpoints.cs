using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.DemoAccess;

/// <summary>Antwort von <c>POST /api/demo/login</c>: Token für Apps (<c>Authorization: Bearer</c>) und Ablauf.</summary>
public sealed record DemoLoginResponse(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>Antwort von <c>GET /api/demo/session</c>.</summary>
public sealed record DemoSessionResponse(bool Enabled, bool Authenticated, DateTimeOffset? ExpiresAtUtc);

/// <summary>Anmeldedaten: der Zugangscode oder ein Einladungs-Key.</summary>
public sealed record DemoLoginRequest(string? Code);

/// <summary>
/// Anmeldung der Live-Demo unter <c>/api/demo</c>: <c>POST /login</c> (Zugangscode → Cookie <c>demo_session</c> und Token),
/// <c>POST /logout</c> (Cookie löschen) und <c>GET /session</c> (Zustand). Alle Pfade sind ohne Sitzung erreichbar; ist der
/// Zugang ausgeschaltet, meldet <c>session</c> das, und <c>login</c> antwortet 404.
/// </summary>
public static class DemoAccessEndpoints
{
    private const int MaxBodyBytes = 1024;
    private const int MaxCodeLength = 200;
    private const string LogCategory = "OPCAgentSample.Server.DemoAccess.Login";

    public static IEndpointRouteBuilder MapDemoAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/demo");
        group.MapPost("/login", LoginAsync);
        group.MapPost("/logout", Logout);
        group.MapGet("/session", Session);
        return endpoints;
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http, IOptions<DemoOptions> options, DemoLoginThrottle throttle, TimeProvider clock,
        ILoggerFactory loggerFactory)
    {
        var settings = options.Value;
        if (!settings.Enabled) return Results.NotFound();

        http.Response.Headers.CacheControl = "no-store";
        var client = http.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";
        if (throttle.IsBlocked(client, out var wait)) return TooManyAttempts(http, wait);

        var request = await ReadBodyAsync(http);
        var access = request?.Code is { } code && code.Length is > 0 and <= MaxCodeLength ? FindAccess(settings, code) : null;
        if (access is null)
        {
            throttle.RecordFailure(client);
            return Results.Problem(
                title: "Zugangscode ungültig",
                detail: "Der Zugangscode ist nicht gültig.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var lifetime = TimeSpan.FromHours(settings.SessionHours);
        var expires = clock.GetUtcNow().Add(lifetime);
        var token = DemoSession.Create(settings.CookieKey, expires, out _);
        http.Response.Cookies.Append(DemoSession.CookieName, token, CookieSettings(http, lifetime));
        var logger = loggerFactory.CreateLogger(LogCategory);
        DemoAccessLog.LoggedIn(logger, access);
        return Results.Ok(new DemoLoginResponse(token, expires));
    }

    private static NoContent Logout(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Cookies.Delete(DemoSession.CookieName, CookieSettings(http, null));
        return TypedResults.NoContent();
    }

    private static Ok<DemoSessionResponse> Session(HttpContext http, IOptions<DemoOptions> options, DemoSessionReader reader)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!options.Value.Enabled) return TypedResults.Ok(new DemoSessionResponse(false, false, null));

        var session = reader.Read(http);
        return TypedResults.Ok(new DemoSessionResponse(true, session is not null, session?.ExpiresAtUtc));
    }

    private static IResult TooManyAttempts(HttpContext http, TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(Math.Max(1, wait.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Problem(
            title: "Zu viele Fehlversuche",
            detail: "Zu viele Fehlversuche. Bitte später noch einmal versuchen.",
            statusCode: StatusCodes.Status429TooManyRequests,
            extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
    }

    /// <summary>Vergleicht den SHA-256 des Codes in konstanter Zeit mit dem konfigurierten Hash.</summary>
    internal static bool CodeMatches(DemoOptions options, string code)
    {
        if (!ApiKeyHasher.TryParseHash(options.AccessCodeSha256, out var expected)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(code)), expected);
    }

    /// <summary>
    /// Zugang, über den der Code gilt: der Hauptcode (<see cref="DemoAccessLog.MainAccessName"/>) oder der Name des Einladungs-Keys,
    /// sonst <see langword="null"/>. Beide Vergleiche laufen immer, damit die Dauer nichts über den Treffer verrät.
    /// </summary>
    internal static string? FindAccess(DemoOptions options, string code)
    {
        var main = CodeMatches(options, code);
        var invite = DemoInviteCodes.Find(options.ParseInviteCodes(), code);
        return main ? DemoAccessLog.MainAccessName : invite;
    }

    private static CookieOptions CookieSettings(HttpContext http, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        IsEssential = true,
        MaxAge = lifetime,
    };

    private static async Task<DemoLoginRequest?> ReadBodyAsync(HttpContext http)
    {
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;

        try
        {
            return await http.Request.ReadFromJsonAsync<DemoLoginRequest>(http.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }
}
