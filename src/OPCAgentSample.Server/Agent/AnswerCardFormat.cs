using System.Text.Json;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Server.Agent;

/// <summary>
/// Format der Antwortkarte, wie Claude sie liefert (Structured Outputs: <c>output_config.format</c>), und die serverseitige Prüfung.
/// Datenstand und Quellen schreibt der Server aus den Tool-Ergebnissen in die Karte, nicht das Modell; es soll sie nicht erfinden können.
/// </summary>
public static class AnswerCardFormat
{
    public const int MaxHeadlineLength = 500;
    public const int MaxTodoItems = 4;
    public const int MaxRows = 12;
    public const int MaxBars = 12;
    public const int MaxWhyItems = 4;
    public const int MaxTextLength = 600;

    private static readonly string[] s_ratings = [Rating.Ok, Rating.Warn, Rating.Crit, Rating.Info];

    /// <summary>Spalten der Werte-Tabelle; sie legt der Server fest, nicht das Modell.</summary>
    private static readonly string[] s_columns = ["Thema", "Daten", "Bewertung"];

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>JSON-Schema der Karte. Alle Objekte mit <c>additionalProperties: false</c>, wie Structured Outputs es verlangen.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Schema() => s_schema.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> s_schema = new(BuildSchema);

    private static Dictionary<string, JsonElement> BuildSchema()
    {
        var text = new { type = "string" };
        var rating = new { type = "string", @enum = s_ratings };
        // Feste Felder statt einer Zellenliste: Structured Outputs garantiert die Form, eine Länge ließe sich nicht erzwingen
        var row = new
        {
            type = "object",
            properties = new
            {
                topic = new { type = "string", description = "Spalte Thema, z. B. 'RT-03 · Störung Gewebegreifer'." },
                data = new { type = "string", description = "Spalte Daten: die Werte aus den Tool-Ergebnissen." },
                rating,
            },
            required = new[] { "topic", "data", "rating" },
            additionalProperties = false,
        };
        var table = new
        {
            type = "object",
            properties = new { rows = new { type = "array", items = row } },
            required = new[] { "rows" },
            additionalProperties = false,
        };
        var bar = new
        {
            type = "object",
            properties = new { label = text, value = new { type = "number" }, rating },
            required = new[] { "label", "value", "rating" },
            additionalProperties = false,
        };
        var chart = new
        {
            type = "object",
            properties = new
            {
                title = text,
                max = new { type = "number", description = "Ende der Skala, mindestens der größte Wert." },
                unit = new { type = "string", description = "Einheit mit führendem Leerzeichen, z. B. ' %' oder ' min'." },
                decimals = new { type = "integer" },
                bars = new { type = "array", items = bar },
            },
            required = new[] { "title", "max", "unit", "decimals", "bars" },
            additionalProperties = false,
        };
        var root = new
        {
            type = "object",
            properties = new
            {
                headline = new { type = "string", description = "Kernaussage in ein bis zwei kurzen Sätzen, höchstens 250 Zeichen." },
                todo = new { type = "array", items = text, description = "Was tun? Null bis drei konkrete Schritte; leer, wenn nichts zu tun ist." },
                table,
                chart = new { anyOf = new object[] { chart, new { type = "null" } } },
                why = new { type = "array", items = text, description = "Ein bis drei kurze Absätze zur Ursache." },
            },
            required = new[] { "headline", "todo", "table", "chart", "why" },
            additionalProperties = false,
        };

        return JsonSerializer.SerializeToElement(root).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>Liest die Karte aus dem Text der Modellantwort und prüft sie. <paramref name="problem"/> beschreibt den ersten Fehler.</summary>
    public static bool TryRead(string json, out AnswerCard card, out string problem)
    {
        card = null!;
        ModelCard? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ModelCard>(json, s_json);
        }
        catch (JsonException ex)
        {
            problem = $"Kein gültiges JSON ({ex.GetType().Name}).";
            return false;
        }

        if (dto is null) return Fail("Leere Antwort.", out problem);
        if (string.IsNullOrWhiteSpace(dto.Headline) || dto.Headline.Length > MaxHeadlineLength) return Fail("Kernaussage fehlt oder ist zu lang.", out problem);
        if (dto.Todo is null || dto.Todo.Count > MaxTodoItems || dto.Todo.Any(t => TooLong(t))) return Fail("Was-tun-Liste ungültig.", out problem);
        if (dto.Why is null || dto.Why.Count > MaxWhyItems || dto.Why.Any(t => TooLong(t))) return Fail("Warum-Liste ungültig.", out problem);
        if (dto.Table?.Rows is null) return Fail("Tabelle fehlt.", out problem);
        if (dto.Table.Rows.Count > MaxRows) return Fail("Tabelle hat zu viele Zeilen.", out problem);

        var rows = new List<AnswerTableRow>();
        foreach (var row in dto.Table.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Topic) || TooLong(row.Topic) || TooLong(row.Data))
                return Fail("Eine Tabellenzeile hat kein Thema oder zu langen Text.", out problem);
            if (!s_ratings.Contains(row.Rating, StringComparer.Ordinal)) return Fail("Unbekannte Bewertung in der Tabelle.", out problem);
            rows.Add(new AnswerTableRow([row.Topic.Trim(), row.Data!.Trim()], row.Rating!));
        }

        AnswerChart? chart = null;
        if (dto.Chart is { } c)
        {
            if (c.Bars is null || c.Bars.Count == 0 || c.Bars.Count > MaxBars) return Fail("Diagramm ohne oder mit zu vielen Balken.", out problem);
            if (c.Bars.Any(b => string.IsNullOrWhiteSpace(b.Label) || !s_ratings.Contains(b.Rating, StringComparer.Ordinal)
                    || double.IsNaN(b.Value) || double.IsInfinity(b.Value)))
                return Fail("Ein Balken ist ungültig.", out problem);
            if (string.IsNullOrWhiteSpace(c.Title) || c.Decimals is < 0 or > 3 || !(c.Max > 0) || c.Bars.Any(b => b.Value > c.Max || b.Value < 0))
                return Fail("Diagramm: Titel, Nachkommastellen oder Skala ungültig.", out problem);
            chart = new AnswerChart(c.Title, c.Max, c.Unit ?? string.Empty, c.Decimals, [.. c.Bars.Select(b => new ChartBar(b.Label!, b.Value, b.Rating!))]);
        }

        var table = new AnswerTable(s_columns, rows);
        card = new AnswerCard(
            dto.Headline.Trim(), dto.Todo, table, chart, dto.Why,
            DataAsOf: string.Empty, Sources: [], Prompt: null, Form: FormOf(rows.Count, chart));
        problem = string.Empty;
        return true;
    }

    private static bool TooLong(string? text) => text is null || text.Length > MaxTextLength;

    private static bool Fail(string message, out string problem)
    {
        problem = message;
        return false;
    }

    internal static string FormOf(int rowCount, AnswerChart? chart) =>
        chart is not null ? "Tabelle + Balken" : rowCount > 0 ? "Tabelle" : "Text";

    private sealed record ModelCard(
        string? Headline, List<string>? Todo, ModelTable? Table, ModelChart? Chart, List<string>? Why);

    private sealed record ModelTable(List<ModelRow>? Rows);

    private sealed record ModelRow(string? Topic, string? Data, string? Rating);

    private sealed record ModelChart(string? Title, double Max, string? Unit, int Decimals, List<ModelBar>? Bars);

    private sealed record ModelBar(string? Label, double Value, string? Rating);
}
