using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Rollup;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Mcp;

/// <summary>Verbotener Zugriff (Rolle oder Scope); der Audit-Filter vermerkt ihn als <c>denied</c>.</summary>
public sealed class McpDeniedException(string message) : McpException(message);

/// <summary>
/// Der aufrufende Client einer MCP-Anfrage: Name und Rolle aus dem API-Key (Claims) und die Rolle aus den Stammdaten.
/// Die Rolle bestimmt Scope (Halle A oder Werk) und Meldungsfilter; Namen von Personen kommen nie an Maschinendaten.
/// </summary>
public sealed class McpCaller(IHttpContextAccessor httpContext, MasterDataStore masterData)
{
    private AppRole? _role;

    private const string DeniedKey = "opcagent.mcp.denied";

    /// <summary>Wurde in diesem Aufruf etwas verweigert? Der Audit-Filter liest das nach dem Tool-Aufruf.</summary>
    public bool WasDenied => WasDeniedIn(httpContext.HttpContext);

    /// <summary>
    /// Der Vermerk hängt an der HTTP-Anfrage (nicht an einer Instanz), weil Tool und Audit-Filter nicht zwingend im selben
    /// Dienstebereich laufen.
    /// </summary>
    public static bool WasDeniedIn(HttpContext? context) => context?.Items.ContainsKey(DeniedKey) == true;

    public ClaimsPrincipal User => httpContext.HttpContext?.User ?? new ClaimsPrincipal();

    public string RoleId => User.FindFirst(ClaimTypes.Role)?.Value ?? string.Empty;

    public async Task<AppRole> GetRoleAsync(CancellationToken cancellationToken)
    {
        if (_role is not null) return _role;

        var data = await masterData.GetAsync(cancellationToken);
        _role = data.Roles.GetValueOrDefault(RoleId) ?? throw Deny($"Die Rolle '{RoleId}' ist nicht bekannt.");
        return _role;
    }

    /// <summary>Die Rolle sieht das ganze Werk (Hallen A bis C) und nicht nur Halle A.</summary>
    public async Task<bool> SeesWholePlantAsync(CancellationToken cancellationToken) =>
        (await GetRoleAsync(cancellationToken)).ScopeHallId is null;

    /// <summary>Wartungsplanung (Konflikte, Bündelung) gehört zur Hallenleiterin und zum Produktionsleiter.</summary>
    public bool CanPlanMaintenance => RoleId is McpRoles.HallLead or McpRoles.ProductionLead;

    /// <summary>
    /// Mindestrang einer Meldung für die Rolle (Spezifikation Abschnitt 9): Schichtleiter alle, Hallenleiterin ab Warnung,
    /// Produktionsleiter nur Eskalationen (kritisch).
    /// </summary>
    public async Task<int> MinimumMessageRankAsync(CancellationToken cancellationToken) =>
        (await GetRoleAsync(cancellationToken)).MessageFilter switch
        {
            "ab Warnung" => Rating.Rank(Rating.Warn),
            "nur Eskalationen" => Rating.Rank(Rating.Crit),
            _ => 0,
        };

    public McpDeniedException Deny(string message)
    {
        if (httpContext.HttpContext is { } context) context.Items[DeniedKey] = true;
        return new McpDeniedException(message);
    }
}

/// <summary>Herkunft der Daten eines Tool-Ergebnisses: Quellen (Redis, Wartungs-DB) und Datenstand.</summary>
public sealed class SourceTrace
{
    private readonly List<string> _labels = [];

    public IReadOnlyList<string> Labels => _labels;

    /// <summary>Datenstand des Ergebnisses: der späteste Stand der beteiligten Quellen (Zeiträume bis jetzt tragen den Stand „jetzt“ bei).</summary>
    public DateTimeOffset? SourceUntilUtc { get; private set; }

    /// <summary>Hebt den Datenstand an, ohne eine Quelle zu nennen.</summary>
    public void Touch(DateTimeOffset sourceUntilUtc)
    {
        if (SourceUntilUtc is null || sourceUntilUtc > SourceUntilUtc) SourceUntilUtc = sourceUntilUtc;
    }

    public void Add(string label, DateTimeOffset? sourceUntilUtc = null)
    {
        if (!_labels.Contains(label, StringComparer.Ordinal)) _labels.Add(label);
        if (sourceUntilUtc is { } until && (SourceUntilUtc is null || until > SourceUntilUtc)) SourceUntilUtc = until;
    }
}

/// <summary>
/// Zustand eines Tool-Aufrufs (gültig je HTTP-Anfrage): aufrufender Client, Datenstand („jetzt“), Herkunft der Daten und
/// Hinweise für das Modell. Der Datenstand ist der Wasserstand der Aufnahme, ohne Aufnahme die Demo-Uhr.
/// </summary>
public sealed class McpSession(McpCaller caller, RollupReader reader, IDemoClock clock)
{
    private DateTimeOffset? _now;

    public McpCaller Caller { get; } = caller;

    public SourceTrace Sources { get; } = new();

    public List<string> Notes { get; } = [];

    public async Task<DateTimeOffset> NowAsync(CancellationToken cancellationToken)
    {
        _now ??= await reader.ReadOverallWatermarkAsync() ?? clock.UtcNow;
        return _now.Value;
    }
}

/// <summary>JSON-Ausgabe der Tools: snake_case, Umlaute unmaskiert (sparsam für das Modell), Enums als Text.</summary>
public static class McpJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public static CallToolResult Result(IReadOnlyDictionary<string, object?> payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, Options);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }
}

/// <summary>
/// Ergebnis eines Tools als geordnete Felder. <see cref="Build"/> ergänzt Datenstand (<c>source_until</c>), Quelle und
/// Hinweise, die jedes Ergebnis trägt (das MCP-Band der Oberfläche zeigt sie an).
/// </summary>
public sealed class Reply(string tool)
{
    private readonly Dictionary<string, object?> _fields = new(StringComparer.Ordinal) { ["tool"] = tool };

    public Reply Add(string key, object? value)
    {
        _fields[key] = value;
        return this;
    }

    public async Task<CallToolResult> BuildAsync(McpSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var now = await session.NowAsync(cancellationToken);
        var until = session.Sources.SourceUntilUtc ?? now;
        _fields["source_until"] = until.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        _fields["datenstand"] = Fmt.Stamp(until);
        _fields["quelle"] = session.Sources.Labels.Count == 0 ? "Berechnung" : string.Join(" + ", session.Sources.Labels);
        if (session.Notes.Count > 0) _fields["hinweise"] = session.Notes;
        return McpJson.Result(_fields);
    }
}

/// <summary>Formatierung für Ausgaben: Ortszeit Wien, Dezimalpunkt im JSON, gerundete Werte.</summary>
public static class Fmt
{
    private static readonly string[] s_weekdays = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    /// <summary>Uhrzeit in Ortszeit, z. B. <c>09:42</c>.</summary>
    public static string Time(DateTimeOffset utc) => DemoTime.TimeText(utc);

    /// <summary>Wochentag, Datum und Uhrzeit in Ortszeit, z. B. <c>Do 15.10. 09:42</c>.</summary>
    public static string Stamp(DateTimeOffset utc)
    {
        var local = DemoTime.ToLocal(utc);
        return $"{s_weekdays[(int)local.DayOfWeek]} {local.ToString("dd.MM. HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Wochentag und Datum in Ortszeit, z. B. <c>Mo 19.10.</c>.</summary>
    public static string Day(DateTimeOffset utc)
    {
        var local = DemoTime.ToLocal(utc);
        return $"{s_weekdays[(int)local.DayOfWeek]} {local.ToString("dd.MM.", CultureInfo.InvariantCulture)}";
    }

    public static string Day(DateOnly day) =>
        $"{s_weekdays[(int)day.DayOfWeek]} {day.ToString("dd.MM.", CultureInfo.InvariantCulture)}";

    public static double Round(double value, int decimals = 1) => Math.Round(value, decimals, MidpointRounding.AwayFromZero);

    public static double Round(decimal value, int decimals = 1) => Math.Round((double)value, decimals, MidpointRounding.AwayFromZero);

    public static double Minutes(TimeSpan span) => Round(span.TotalMinutes, 1);

    public static double Hours(TimeSpan span) => Round(span.TotalHours, 2);

    /// <summary>Ganze Zahl mit Punkt als Tausendertrenner (24.100), unabhängig von der Kultur des Rechners.</summary>
    public static string Thousands(double value) =>
        Math.Round(value).ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');

    public static int Percent(double ratio) => (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);

    /// <summary>Dauer in Minuten als Text, z. B. <c>1 h 40 min</c>.</summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
    }
}

/// <summary>Bewertungswort zu einem <see cref="Rating"/> (Wort steht in der Oberfläche immer zusätzlich zur Farbe).</summary>
public static class RatingText
{
    public static string Word(string rating) => rating switch
    {
        Rating.Ok => "in Ordnung",
        Rating.Warn => "Warnung",
        Rating.Crit => "kritisch",
        _ => "Hinweis",
    };

    public static string From(TrafficLight light) => light switch
    {
        TrafficLight.Problem => Rating.Crit,
        TrafficLight.Warning => Rating.Warn,
        _ => Rating.Ok,
    };
}
