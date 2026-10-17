using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Api;

// Antworten der HTTP-API (JSON, camelCase). Der Vertrag steht im Kopf von ApiEndpoints.cs.

public sealed record RoleView(
    string Id,
    string Title,
    string Label,
    string Scope,
    string ScopeDescription,
    string Devices,
    IReadOnlyList<string> Rights,
    string MessageFilter,
    string FeedFilterText);

public sealed record UserView(
    string Id,
    string DisplayName,
    string Initials,
    string FirstName,
    string Title,
    string MobileTitle,
    string WorkTime,
    string Notification);

public sealed record RoleOption(string Id, string Label, string ShortLabel);

public sealed record ClockView(DateTimeOffset UtcNow, string LocalText, string LocalTime, string Greeting);

/// <param name="HistoryLoading">Die Historie wird beim Start noch aufgenommen (Kaltstart); Zahlen sind noch unvollständig.</param>
/// <param name="DataWarning">Hinweis zur Zeitbasis, z. B. Simulator mit früherer Startzeit neu gestartet; sonst <see langword="null"/>.</param>
public sealed record StatusView(
    bool OpcConnected, int McpToolCount, string Area, string Shift, string ScopeText,
    bool HistoryLoading = false, string? DataWarning = null);

public sealed record CountersView(int InboxOpen, bool InboxHasUrgent, int FeedCount, FeedEntry? LatestFeed);

public sealed record MeResponse(
    RoleView Role,
    UserView User,
    IReadOnlyList<RoleOption> Roles,
    ClockView Clock,
    StatusView Status,
    CountersView Counters);

/// <summary>Anzahl je Zustand; <c>NoData</c> zählt Stationen ohne Messwerte und gehört weder zu „Läuft“ noch zu einem Befund.</summary>
public sealed record StatusCounts(int Problem, int Warning, int Running, int NoData = 0);

public sealed record StationView(string Id, string Status, string StatusWord, string Article, StationKpi Kpi, string Message);

public sealed record StationGroupView(
    string Id, string Title, string KpiLabel, int StationCount, string WorstStatus, StatusCounts Counts,
    IReadOnlyList<StationView> Stations);

public sealed record OverviewResponse(
    string Scope,
    string Id,
    string Title,
    string LiveText,
    ClimateInfo? Climate,
    StatusCounts Counts,
    IReadOnlyList<StationGroupView> Groups);

public sealed record FavoriteView(string ShortTitle, string Question, bool IsDefault);

public sealed record FavoriteDefault(string ShortTitle, string Question);

public sealed record FavoritesResponse(
    int Max, int MaxShortTitleLength, IReadOnlyList<FavoriteView> Items, IReadOnlyList<FavoriteDefault> Defaults);

public sealed record FavoriteInput(string? ShortTitle, string? Question);

public sealed record FavoritesUpdate(IReadOnlyList<FavoriteInput>? Items);

public sealed record FeedResponse(string FilterText, IReadOnlyList<FeedEntry> Items);

/// <param name="Fresh">Ignoriert eine gespeicherte Antwort auf dieselbe Frage und fragt neu („Neu fragen“).</param>
/// <param name="Report">Verweis auf die Auffälligkeit eines Reports; den Kontext dazu berechnet der Server selbst.</param>
public sealed record AskBody(string? Role, string? Question, Guid? SessionId, bool Fresh = false, ReportRef? Report = null);

/// <param name="Repeat"><see langword="true"/>: gespeicherte Antwort auf dieselbe Frage von eben; es wurde nichts verbraucht.</param>
public sealed record AskDone(
    Guid SessionId, int Sequence, TokenUsage Tokens, DateTimeOffset DataAsOf, string DataAsOfText, bool Repeat = false);

/// <summary>Tokens einer Antwort: <c>Input</c> ohne Cache-Anteile (wie die API sie meldet), Cache getrennt.</summary>
public sealed record TokenUsage(int Input, int Output, int CacheRead = 0, int CacheWrite = 0);
