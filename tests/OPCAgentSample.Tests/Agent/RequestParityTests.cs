using System.Text.Json;
using System.Text.Json.Nodes;

namespace OPCAgentSample.Tests.Agent;

/// <summary>
/// AC-02: Die Anfrage des echten Adapters entspricht der bisherigen <c>Build</c>-Anfrage. Der Sollwert (<c>Reference/legacy-request.json</c>)
/// ist der HTTP-Body, den der Altcode für <see cref="ParityHistory"/> gesendet hat; er stammt aus dem Lauf vor dem Löschen von
/// <c>AnthropicModelClient</c>. Erlaubt sind nur die Unterschiede, die die Rundenakte als Ruling hingenommen hat.
/// </summary>
public sealed class RequestParityTests
{
    private static async Task<JsonNode> ActualAsync() =>
        JsonNode.Parse((await ApiRequestRecorder.RecordAsync(ParityHistory.Request(), ParityHistory.Options())).GetRawText())!;

    private static JsonNode Expected() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "OPCAgentSample.Tests", "Agent", "Reference", "legacy-request.json")))!;

    /// <summary>
    /// Entfernt die Unterschiede, die per Ruling erlaubt sind, und zählt sie:
    /// <c>additionalProperties:false</c> am Tool-Schema (Ruling „Tool-Schemata“) und <c>disable_parallel_tool_use:false</c> im
    /// <c>tool_choice</c> (Ruling <c>AllowMultipleToolCalls</c>). Dazu kommt eine Build decision (Slice 03): Der Adapter sendet den Text der
    /// ersten User-Nachricht als Liste mit einem Textblock statt als Zeichenkette; das ist für die API gleichwertig (Kurzform desselben Blocks).
    /// </summary>
    private static int RemoveRuledDifferences(JsonNode request)
    {
        var removed = 0;
        if (request["messages"]![0]!["content"] is JsonArray { Count: 1 } blocks
            && blocks[0]!["type"]!.GetValue<string>() == "text"
            && blocks[0]!.AsObject().Count == 2)
        {
            request["messages"]![0]!["content"] = blocks[0]!["text"]!.GetValue<string>();
            removed++;
        }

        foreach (var tool in request["tools"]!.AsArray())
        {
            var schema = tool!["input_schema"]!.AsObject();
            if (schema["additionalProperties"] is JsonValue value && value.GetValue<bool>() == false)
            {
                schema.Remove("additionalProperties");
                removed++;
            }
        }

        var choice = request["tool_choice"]!.AsObject();
        if (choice["disable_parallel_tool_use"] is JsonValue parallel && parallel.GetValue<bool>() == false)
        {
            choice.Remove("disable_parallel_tool_use");
            removed++;
        }

        return removed;
    }

    /// <summary>Gleiche Schlüsselreihenfolge auf beiden Seiten, damit der Vergleich der Texte die Stelle des Unterschieds zeigt.</summary>
    private static string Canonical(JsonNode node) => node switch
    {
        JsonObject obj => "{" + string.Join(",\n", obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value!))) + "}",
        JsonArray array => "[" + string.Join(",\n", array.Select(item => Canonical(item!))) + "]",
        _ => node.ToJsonString(),
    };

    [Fact]
    public async Task Given_the_frozen_history_When_the_real_adapter_builds_the_request_Then_the_json_differs_only_in_the_ruled_points()
    {
        var actual = await ActualAsync();
        RemoveRuledDifferences(actual);

        Assert.Equal(Canonical(Expected()), Canonical(actual));
    }

    [Fact]
    public async Task Given_the_ruled_differences_When_the_filter_runs_Then_all_of_them_occur_in_the_new_request()
    {
        var actual = await ActualAsync();

        Assert.Equal(4, RemoveRuledDifferences(actual)); // erste User-Nachricht, zwei Tool-Schemata und der tool_choice
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OPCAgentSample.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("OPCAgentSample.slnx nicht gefunden.");
    }
}
