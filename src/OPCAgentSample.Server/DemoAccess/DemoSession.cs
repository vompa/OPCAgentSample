using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace OPCAgentSample.Server.DemoAccess;

/// <summary>Eine gültige Sitzung der Live-Demo: zufällige Kennung (ohne Personenbezug) und Ablauf.</summary>
public sealed record DemoSessionInfo(string Id, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Zustandsloses, signiertes Sitzungstoken <c>kennung.ablauf.hmac</c> (HMAC-SHA256). Dasselbe Format dient als Cookie
/// <c>demo_session</c> im Browser und als <c>Authorization: Bearer</c> für Apps (Capacitor). Der Schlüssel kommt aus der
/// Konfiguration, deshalb bleiben Anmeldungen über einen Neustart gültig; es werden keine Personendaten gespeichert.
/// </summary>
public static class DemoSession
{
    public const string CookieName = "demo_session";
    private const int MaxTokenLength = 200;

    /// <summary>Erzeugt ein Token mit neuer zufälliger Kennung.</summary>
    public static string Create(string cookieKey, DateTimeOffset expiresAtUtc, out string sessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(cookieKey);
        sessionId = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));
        var payload = $"{sessionId}.{expiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
        return $"{payload}.{Sign(cookieKey, payload)}";
    }

    /// <summary>Liefert die Sitzung, wenn das Token echt und nicht abgelaufen ist, sonst <see langword="null"/>.</summary>
    public static DemoSessionInfo? Validate(string cookieKey, string? token, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookieKey) || string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return null;

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0) return null;
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiry)) return null;

        var expected = Sign(cookieKey, $"{parts[0]}.{parts[1]}");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2]))) return null;

        return expiry > now.ToUnixTimeSeconds() ? new DemoSessionInfo(parts[0], DateTimeOffset.FromUnixTimeSeconds(expiry)) : null;
    }

    private static string Sign(string cookieKey, string payload) =>
        WebEncoders.Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cookieKey), Encoding.UTF8.GetBytes(payload)));
}
