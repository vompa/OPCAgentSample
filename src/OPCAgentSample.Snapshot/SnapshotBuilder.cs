using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OPCAgentSample.Server.Api;

namespace OPCAgentSample.Snapshot;

/// <summary>
/// Erzeugt die Momentaufnahme der Demo zur Demo-Uhr (Do 15.10.2026 09:42) je Rolle, indem sie die HTTP-API des Servers
/// aufruft – also mit denselben Funktionen wie die Weboberfläche. Die Oberfläche zeigt die Dateien als „Vorschau“, solange das
/// Backend schläft. Der Maschinen-Report liegt je Rolle in ihrem Standardzeitraum vor (Schicht, Woche, vier Wochen). Ergebnis: Dateiname → Inhalt (kompaktes JSON, UTF-8, LF, abschließender Zeilenumbruch).
/// </summary>
/// <remarks>
/// Voraussetzungen für Reproduzierbarkeit: Redis mit der Szenario-Historie bis zur Demo-Uhr (frisch aufgenommen), Referenz-Engine
/// statt Claude, Demo-Zugang aus. Zufällige Werte (Sitzungs-Id, Zeitstempel) werden auf feste Werte gesetzt.
/// </remarks>
public static class SnapshotBuilder
{
    public const string ReportsFile = "reports.json";
    public const string FormatVersion = "1";

    private const string FixedSessionId = "00000000-0000-0000-0000-000000000001";
    private static readonly JsonSerializerOptions s_write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);

    /// <summary>Dateiname der Rolle, z. B. <c>shift_lead.json</c>.</summary>
    public static string FileOf(string roleId) => $"{roleId}.json";

    /// <summary>Dateiname des Maschinen-Reports der Rolle im Standardzeitraum der Rolle, z. B. <c>machines_shift_lead.json</c>.</summary>
    public static string MachinesFileOf(string roleId) => $"machines_{roleId}.json";

    public static async Task<IReadOnlyDictionary<string, string>> BuildAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var first = await GetAsync(client, "/api/me", cancellationToken);
        var clock = first["clock"]!["utcNow"]!.GetValue<string>();
        foreach (var role in first["roles"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()))
        {
            files[FileOf(role)] = Serialize(await BuildRoleAsync(client, role, clock, cancellationToken));
            files[MachinesFileOf(role)] = Serialize(await GetAsync(client, $"/api/reports/machines?role={Uri.EscapeDataString(role)}", cancellationToken));
        }

        files[ReportsFile] = Serialize(JsonSerializer.SerializeToNode(SampleReports(DateTimeOffset.Parse(clock, System.Globalization.CultureInfo.InvariantCulture)), s_web)!);
        return files;
    }

    private static async Task<JsonObject> BuildRoleAsync(HttpClient client, string role, string clock, CancellationToken cancellationToken)
    {
        var query = $"?role={Uri.EscapeDataString(role)}";
        var me = await GetAsync(client, $"/api/me{query}", cancellationToken);
        var favorites = await GetAsync(client, $"/api/favorites{query}", cancellationToken);
        // Vor den Fragen lesen: Beim ersten Aufruf legt der Server den vorbelegten Beispielverlauf an.
        var history = NormalizeHistory(await GetAsync(client, $"/api/history/latest{query}", cancellationToken), clock);

        var answers = new JsonObject();
        foreach (var item in favorites["defaults"]!.AsArray())
        {
            var question = item!["question"]!.GetValue<string>();
            answers[question] = await AskAsync(client, role, question, cancellationToken);
        }

        return new JsonObject
        {
            ["version"] = FormatVersion,
            ["asOfUtc"] = clock,
            ["me"] = me,
            ["overview"] = await GetAsync(client, $"/api/overview{query}", cancellationToken),
            ["favorites"] = favorites,
            ["inbox"] = await GetAsync(client, $"/api/inbox{query}", cancellationToken),
            ["feed"] = await GetAsync(client, $"/api/feed{query}", cancellationToken),
            ["history"] = history,
            ["answers"] = answers,
        };
    }

    private static async Task<JsonNode> GetAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken))!;
    }

    /// <summary>Stellt die Frage wie die Oberfläche (SSE) und gibt Tool-Aufrufe und Antwortkarte zurück.</summary>
    private static async Task<JsonObject> AskAsync(HttpClient client, string role, string question, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/ask", UriKind.Relative), new { role, question }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var toolCalls = new JsonArray();
        JsonNode? card = null;
        foreach (var block in (await response.Content.ReadAsStringAsync(cancellationToken)).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = JsonNode.Parse(lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..]);
            if (name == "tool_call") toolCalls.Add(data);
            else if (name == "answer") card = data;
        }

        return new JsonObject { ["toolCalls"] = toolCalls, ["card"] = card ?? throw new InvalidOperationException($"Keine Antwort auf '{question}'.") };
    }

    private static JsonNode NormalizeHistory(JsonNode history, string clock)
    {
        var session = history["session"]!;
        session["id"] = FixedSessionId;
        session["startedAtUtc"] = clock;
        session["lastActivityUtc"] = clock;
        foreach (var turn in history["turns"]!.AsArray()) turn!["askedAtUtc"] = clock;
        return history;
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(s_write) + "\n";

    /// <summary>Beispielwerte für die Report-Seite in der Vorschau (fest, aber im Format der echten Antwort).</summary>
    private static ReportsSummary SampleReports(DateTimeOffset now)
    {
        var start = now.AddHours(-23);
        long[] questions = [0, 0, 0, 0, 0, 0, 0, 1, 2, 0, 3, 4, 2, 1, 0, 0, 2, 1, 0, 0, 1, 0, 1, 0];
        long[] input = [.. questions.Select(q => q * 5200)];
        long[] output = [.. questions.Select(q => q * 780)];
        return new ReportsSummary(
            new ReportPeriod(now, now.AddHours(-22), now.AddHours(-23), 24),
            new ReportQuestions(18, 7, 18, 14),
            new ReportTokens(72_800, 10_900, 61_000, 9_400),
            new ReportCost(0.0125m, 0.456, new ReportPrices()),
            new ReportLatency(14, 6_200, 5_400, 11_800),
            [
                new(0, 250, 0), new(250, 500, 0), new(500, 1000, 0), new(1000, 2000, 0), new(2000, 3000, 1), new(3000, 5000, 4),
                new(5000, 8000, 6), new(8000, 12000, 2), new(12000, 20000, 1), new(20000, 30000, 0), new(30000, null, 0),
            ],
            new ReportTimeSeries(start, 60, questions, input, output),
            [
                new("get_hall_overview", "shift_lead", "ok", 21, 180, 420),
                new("get_oee", "hall_lead", "ok", 15, 260, 610),
                new("get_order_progress", "production_lead", "ok", 9, 210, 480),
                new("get_maintenance_due", "hall_lead", "ok", 6, 150, 330),
            ],
            new ReportIngestion(0, now, now, 1_523_000, 1, 0));
    }
}
