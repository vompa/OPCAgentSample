using System.Globalization;
using System.Text.Json;

namespace OPCAgentSample.Tests.Mcp;

/// <summary>Kleine Navigationshilfen für Tool-Ergebnisse: Pfade wie <c>schichten.0.planerfuellung.prozent</c>.</summary>
internal static class JsonPath
{
    public static JsonElement At(this JsonElement element, string path)
    {
        var current = element;
        foreach (var segment in path.Split('.'))
        {
            current = int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                ? current[index]
                : current.GetProperty(segment);
        }

        return current;
    }

    public static double Number(this JsonElement element, string path) => element.At(path).GetDouble();

    public static string Text(this JsonElement element, string path) => element.At(path).GetString() ?? string.Empty;

    public static bool Has(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null;

    /// <summary>Das Element einer Liste, dessen Eigenschaft den Wert hat (Fehler, wenn es keines gibt).</summary>
    public static JsonElement Item(this JsonElement list, string property, string value) =>
        list.EnumerateArray().FirstOrDefault(i => i.Has(property) && i.GetProperty(property).ToString() == value) is { ValueKind: not JsonValueKind.Undefined } found
            ? found
            : throw new InvalidOperationException($"Kein Element mit {property} = '{value}' in {Shorten(list)}");

    public static IEnumerable<JsonElement> Items(this JsonElement list) => list.EnumerateArray();

    private static string Shorten(JsonElement element)
    {
        var text = element.GetRawText();
        return text.Length > 400 ? text[..400] + "..." : text;
    }
}
