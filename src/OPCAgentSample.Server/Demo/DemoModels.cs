using System.Text.Json;

namespace OPCAgentSample.Server.Demo;

// Fachliche Modelle, die die Oberfläche anzeigt. Die Schnittstellen in IDemoSources.cs liefern sie;
// heute aus Referenzdaten (Mockup), später aus Kennzahlen-Engine, MCP-Server und Claude-Agent.

/// <summary>Status einer Station. Das Statuswort steht in der Oberfläche immer zusätzlich zur Farbe.</summary>
public static class StationStatus
{
    public const string Running = "running";
    public const string Warning = "warning";
    public const string Problem = "problem";

    /// <summary>Für die Station liegen keine Messwerte vor: weder Entwarnung noch Befund (neutraler Zustand).</summary>
    public const string NoData = "nodata";

    public static string Word(string status) => status switch
    {
        Running => "Läuft",
        Warning => "Warnung",
        Problem => "Problem",
        NoData => "Keine Daten",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unbekannter Stationsstatus."),
    };
}

/// <summary>Bewertungen in Tabellen, Balken und Meldungen: <c>ok</c>, <c>warn</c>, <c>crit</c>, <c>info</c>.</summary>
public static class Rating
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Crit = "crit";
    public const string Info = "info";

    /// <summary>Rang für Filter: Info und OK = 0, Warnung = 1, kritisch = 2.</summary>
    public static int Rank(string rating) => rating switch
    {
        Warn => 1,
        Crit => 2,
        _ => 0,
    };
}

public sealed record ClimateInfo(string Temperature, string Humidity, string Text, string Tooltip, string Status);

public sealed record StationKpi(string Value, string Unit);

public sealed record StationInfo(string Id, string Status, string Article, StationKpi Kpi, string Message);

public sealed record StationGroup(string Id, string Title, string KpiLabel, IReadOnlyList<StationInfo> Stations);

/// <summary>Anlagenübersicht einer Rolle: Halle (Stationen) oder Werk (Hallen als Stationen).</summary>
public sealed record HallOverviewSnapshot(string Id, string Title, ClimateInfo? Climate, IReadOnlyList<StationGroup> Groups);

public sealed record AnswerTableRow(IReadOnlyList<string> Cells, string Rating);

/// <summary>Spalten haben einen Eintrag mehr als die Zellen: Die letzte Spalte ist die Bewertung.</summary>
public sealed record AnswerTable(IReadOnlyList<string> Columns, IReadOnlyList<AnswerTableRow> Rows);

public sealed record ChartBar(string Label, double Value, string Rating);

public sealed record AnswerChart(string Title, double Max, string Unit, int Decimals, IReadOnlyList<ChartBar> Bars);

/// <summary>
/// Antwortkarte, überall in derselben Reihenfolge (Spezifikation Abschnitt 11): Kernaussage, Was tun?,
/// Die Werte, Diagramm, Warum, Datenstand mit Quellen.
/// </summary>
public sealed record AnswerCard(
    string Headline,
    IReadOnlyList<string> Todo,
    AnswerTable Table,
    AnswerChart? Chart,
    IReadOnlyList<string> Why,
    string DataAsOf,
    IReadOnlyList<string> Sources,
    string? Prompt = null,
    string? Form = null,
    string? Notice = null);

/// <summary>
/// Ein Tool-Aufruf für das MCP-Band (nur Anzeige). <see cref="Source"/> nennt den Lieferanten
/// (<c>reference</c> = Referenzdaten, <c>mcp</c> = Tool des MCP-Servers), <see cref="DataSource"/> die Datenquelle (z. B. OPC HistoryRead),
/// <see cref="DataAsOf"/> den Datenstand des Ergebnisses und <see cref="Outcome"/> <c>ok</c> oder <c>error</c>.
/// </summary>
public sealed record ToolCall(
    string Tool, JsonElement Parameters, int? DurationMs, string Source, string DataSource, string? DataAsOf = null, string? Outcome = null);

public sealed record InboxItem(
    string Id,
    string Level,
    string Priority,
    string PriorityText,
    string Title,
    string Detail,
    string Due,
    string? Question,
    string? TakenBy = null,
    string? TakenAtText = null);

public sealed record FeedEntry(string Time, string Source, string Text, string Level);

/// <summary>Darstellungsdaten einer Rolle, die nicht in der Datenbank stehen (Beschriftungen, Arbeitszeit, Statusleiste).</summary>
public sealed record RoleProfile(
    string Label,
    string ShortLabel,
    string Initials,
    string FirstName,
    string Title,
    string MobileTitle,
    string Scope,
    string WorkTime,
    string Notification,
    string ScopeText,
    string FeedFilterText);

public sealed record PlantStatus(bool OpcConnected, int McpToolCount);

/// <summary>Frage an die Antwort-Engine.</summary>
/// <param name="OwnerKey">Besitzer des Verlaufs (Schlüssel <c>art:kennung</c>); Grundlage von Kontingent und anonymer Nutzerkennung.</param>
/// <param name="Fresh">Ignoriert eine gespeicherte Antwort auf dieselbe Frage (Wiederholungsschutz) und überschreibt sie.</param>
/// <param name="Report">Kontext des Reports, aus dem die Frage stammt (vom Server aus dem Report berechnet, nie vom Client).</param>
public sealed record AskRequest(string RoleId, string Question, string? OwnerKey = null, bool Fresh = false, ReportContext? Report = null);

/// <summary>Kontext einer Frage aus einem Report: <see cref="Key"/> benennt die Quelle (Report, Zeitraum, Auffälligkeit), <see cref="Text"/> sind Zeitraum und Werte für Claude.</summary>
public sealed record ReportContext(string Key, string Text);

/// <summary>Schritt einer Antwort: Tool-Aufrufe, dazwischen der Entwurf der Karte, am Ende die Antwortkarte.</summary>
public abstract record AnswerStep;

public sealed record ToolCallStep(ToolCall Call) : AnswerStep;

/// <summary>Anfang der Karte (Überschrift, fertige Was-tun-Einträge), noch während das Modell schreibt. Nur zur Anzeige, nicht geprüft.</summary>
public sealed record AnswerDraftStep(string Headline, IReadOnlyList<string> Todo) : AnswerStep;

/// <param name="DataAsOfUtc">Datenstand der Antwort (aus den Tool-Ergebnissen); ohne Angabe gilt die Demo-Uhr.</param>
/// <param name="Cacheable">Die Karte ist eine geprüfte Antwort von Claude (keine Fehler-, Grenz- oder Referenzkarte) und darf wiederholt werden.</param>
/// <param name="Repeat">Gespeicherte Antwort auf dieselbe Frage; es wurde nichts verbraucht (Tokens 0).</param>
public sealed record AnswerReadyStep(
    AnswerCard Card,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    DateTimeOffset? DataAsOfUtc = null,
    bool Cacheable = false,
    bool Repeat = false) : AnswerStep;

/// <summary>Vorbelegter Beispielverlauf für den ersten Besuch.</summary>
public sealed record SampleTurn(string Question, AnswerCard Card, IReadOnlyList<ToolCall> ToolCalls);
