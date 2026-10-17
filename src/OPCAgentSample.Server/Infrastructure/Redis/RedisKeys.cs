using System.Text.RegularExpressions;
using OPCAgentSample.Domain;

namespace OPCAgentSample.Server.Infrastructure.Redis;

/// <summary>
/// Die Schlüssel der Anwendung in Redis (Zustand, Meldungen, Stammdaten, Verlauf, Favoriten, Audit, Nutzung, Kontingent); die der
/// Verdichtung (Zähler, Statistik, Ereignisse) bildet <c>RollupKeys</c>. Perioden-Schlüssel kommen ausschließlich aus
/// <see cref="ProductionPeriod"/> (erzeugt vom <see cref="ShiftCalendar"/>), nie aus eigener Formatierung. Teile mit Doppelpunkt
/// oder Leerzeichen werden abgelehnt, damit kein Schlüssel einen fremden Bereich trifft.
/// </summary>
public static partial class RedisKeys
{
    /// <summary>Zustand einer Station: <c>st:{id}</c> (Hash).</summary>
    public static string Station(string stationId) => $"st:{Segment(stationId, nameof(stationId))}";

    /// <summary>Meldungen des Produktionstags einer Halle: <c>ev:A:{prodtag}</c> (Stream, TTL 48 h).</summary>
    public static string Events(string hallId, ProductionPeriod productionDay)
    {
        ArgumentNullException.ThrowIfNull(productionDay);
        return productionDay.Kind == PeriodKind.Day
            ? $"ev:{Segment(hallId, nameof(hallId))}:{productionDay.Key}"
            : throw new ArgumentException($"Erwartet wird ein Zeitbereich der Ebene Day, nicht {productionDay.Kind}.", nameof(productionDay));
    }

    /// <summary>Aktive Alarme einer Halle: <c>alarm:A:active</c> (Sorted Set nach Severity).</summary>
    public static string ActiveAlarms(string hallId) => $"alarm:{Segment(hallId, nameof(hallId))}:active";

    /// <summary>Postfach einer Rolle: <c>inbox:{rolle}</c>.</summary>
    public static string Inbox(string roleId) => $"inbox:{Segment(roleId, nameof(roleId))}";

    /// <summary>Stammdaten einer Art (Hash Id → JSON): <c>md:{typ}</c>.</summary>
    public static string MasterData(string type) => $"md:{type}";

    /// <summary>Gesetzt, sobald die Stammdaten aus dem Seed in Redis stehen.</summary>
    public const string MasterDataVersion = "md:version";

    /// <summary>Favoriten eines Benutzers als JSON-Liste: <c>fav:{benutzer}</c>.</summary>
    public static string Favorites(string userId) => $"fav:{Segment(userId, nameof(userId))}";

    /// <summary>Letzte Fragen eines Besitzers (Sorted Set Frage → Zeitpunkt): <c>hist:{owner}</c>.</summary>
    public static string History(string ownerKey) => $"hist:{Owner(ownerKey)}";

    /// <summary>Sitzungen eines Besitzers (Sorted Set Sitzung → Beginn): <c>chat:s:{owner}</c>.</summary>
    public static string ChatSessions(string ownerKey) => $"chat:s:{Owner(ownerKey)}";

    public const string ChatSessionsPattern = "chat:s:*";

    /// <summary>Kopfdaten einer Sitzung (Hash): <c>chat:sess:{id}</c>.</summary>
    public static string ChatSession(Guid sessionId) => $"chat:sess:{sessionId:N}";

    /// <summary>Fragen einer Sitzung (Stream, ein Eintrag je Frage): <c>chat:t:{id}</c>.</summary>
    public static string ChatTurns(Guid sessionId) => $"chat:t:{sessionId:N}";

    /// <summary>Audit-Log (Stream, begrenzt).</summary>
    public const string Audit = "audit";

    /// <summary>Zähler der Fragen je Tag (UTC): <c>usage:d:{yyyyMMdd}</c>.</summary>
    public static string UsageDay(DateTimeOffset utc) => $"usage:d:{utc.UtcDateTime:yyyyMMdd}";

    /// <summary>Zähler der Fragen je Stunde (UTC): <c>usage:h:{yyyyMMddHH}</c>.</summary>
    public static string UsageHour(DateTimeOffset utc) => $"usage:h:{utc.UtcDateTime:yyyyMMddHH}";

    /// <summary>Summen aller Fragen und Histogramm der Antwortzeiten (Hash).</summary>
    public const string UsageTotal = "usage:total";

    /// <summary>Aufrufe, Laufzeit und Ergebnis je MCP-Tool (Hash): <c>usage:tool:{name}</c>.</summary>
    public static string UsageTool(string tool) => $"usage:tool:{Segment(tool, nameof(tool))}";

    /// <summary>Namen der Tools, die in <c>usage:tool:*</c> stehen (Set).</summary>
    public const string UsageTools = "usage:tools";

    /// <summary>Tageszähler der Claude-Anbindung je Besitzer: <c>claude:quota:{owner}:{q|t}:{yyyyMMdd}</c> (q = Fragen, t = Tokens).</summary>
    public static string ClaudeQuota(string ownerKey, string kind, DateOnly day) =>
        $"claude:quota:{Owner(ownerKey)}:{QuotaKind(kind)}:{day:yyyyMMdd}";

    /// <summary>Tageszähler der Fragen aller Besitzer: <c>claude:quota:total:q:{yyyyMMdd}</c>.</summary>
    public static string ClaudeQuotaTotal(DateOnly day) => $"claude:quota:total:q:{day:yyyyMMdd}";

    private static string Segment(string value, string name) =>
        !string.IsNullOrEmpty(value) && SegmentPattern().IsMatch(value)
            ? value
            : throw new ArgumentException("Ungültiger Schlüsselteil.", name);

    private static string QuotaKind(string kind) =>
        kind is "q" or "t" ? kind : throw new ArgumentException("Ungültige Zählerart.", nameof(kind));

    /// <summary>Besitzer-Schlüssel haben die Form <c>art:kennung</c> (z. B. <c>user:martin.huber</c>).</summary>
    private static string Owner(string ownerKey) =>
        !string.IsNullOrEmpty(ownerKey) && OwnerPattern().IsMatch(ownerKey)
            ? ownerKey
            : throw new ArgumentException("Ungültiger Besitzer-Schlüssel.", nameof(ownerKey));

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex("^[a-z]{1,16}:[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex OwnerPattern();
}
