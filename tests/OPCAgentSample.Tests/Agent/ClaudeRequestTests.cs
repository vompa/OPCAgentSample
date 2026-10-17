using System.Text.Json;
using System.Text.RegularExpressions;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Aufbau der Anfrage: Prompt-Caching (stabiles Präfix zuerst), Modell, Effort, Structured Output, Nutzerkennung.</summary>
public sealed partial class ClaudeRequestTests
{
    private static int CountCacheControl(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Sum(p => p.NameEquals("cache_control") ? (p.Value.ValueKind == JsonValueKind.Null ? 0 : 1) : CountCacheControl(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Sum(CountCacheControl),
        _ => 0,
    };

    private static async Task<EngineHarness> TwoRoundRunAsync(string role = "hall_lead", string owner = "code:abc123", string question = "Wie ist die Lage?")
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"A"}"""), ("t2", "get_active_alarms", """{"scope":"A"}"""))).Then(Replies.Card());
        await harness.AskAsync(question, role, owner);
        return harness;
    }

    [Fact]
    public async Task Model_effort_format_and_tool_choice_follow_the_configuration()
    {
        var harness = await TwoRoundRunAsync();

        var request = harness.Model.Requests[0];
        Assert.Equal("claude-haiku-5-5", request.Model.ToString().Trim('"'));
        Assert.Equal(6000, request.MaxTokens);
        var body = JsonDocument.Parse(request.OutputConfig!.ToString()!).RootElement;
        Assert.Equal("medium", body.GetProperty("effort").GetString());
        Assert.Equal("json_schema", body.GetProperty("format").GetProperty("type").GetString());
        Assert.Equal(["headline", "todo", "table", "chart", "why"], body.GetProperty("format").GetProperty("schema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(body.GetProperty("format").GetProperty("schema").GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task System_prompt_is_stable_has_a_cache_breakpoint_and_contains_no_time_or_session_data()
    {
        var harness = await TwoRoundRunAsync();

        var system = RequestJson.System(harness.Model.Requests[0]);
        Assert.Equal(1, system.GetArrayLength());
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());

        var text = system[0].GetProperty("text").GetString()!;
        Assert.DoesNotMatch(TimeOfDay(), text);
        Assert.DoesNotMatch(DateLike(), text);
        Assert.DoesNotContain("hall_lead", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Assert.Contains("Du-Form", text, StringComparison.Ordinal);
        Assert.Contains("Tool-Ergebnisse", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prefix_of_tools_and_system_is_byte_identical_across_rounds_roles_and_questions()
    {
        var first = await TwoRoundRunAsync("hall_lead", "code:aaa", "Wie ist die Lage?");
        var second = await TwoRoundRunAsync("production_lead", "code:bbb", "Und im Werk?");

        var reference = (RequestJson.Tools(first.Model.Requests[0]).GetRawText(), RequestJson.System(first.Model.Requests[0]).GetRawText());
        foreach (var request in first.Model.Requests.Concat(second.Model.Requests))
            Assert.Equal(reference, (RequestJson.Tools(request).GetRawText(), RequestJson.System(request).GetRawText()));
    }

    [Fact]
    public async Task Tools_are_sorted_by_name_and_taken_from_the_toolbox()
    {
        var harness = await TwoRoundRunAsync();

        var names = RequestJson.Tools(harness.Model.Requests[0]).EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(["get_active_alarms", "get_hall_overview", "get_shift_summary"], names);
        var schema = RequestJson.Tools(harness.Model.Requests[0])[0].GetProperty("input_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("scope", schema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task Volatile_data_stands_only_in_the_first_user_message()
    {
        var harness = await TwoRoundRunAsync("hall_lead", "code:abc123", "Wie ist die Lage?");

        var messages = RequestJson.Messages(harness.Model.Requests[0]);
        var first = messages[0].GetProperty("content").GetString()!;
        Assert.Contains("Rolle der fragenden Person: hall_lead", first, StringComparison.Ordinal);
        Assert.Contains("Do 15.10. 09:42", first, StringComparison.Ordinal);
        Assert.Contains("<frage>\nWie ist die Lage?\n</frage>", first, StringComparison.Ordinal);
        Assert.Equal(0, CountCacheControl(messages));
    }

    [Fact]
    public async Task Second_round_marks_only_the_last_tool_result_and_stays_within_four_breakpoints()
    {
        var harness = await TwoRoundRunAsync();

        var request = harness.Model.Requests[1];
        var messages = RequestJson.Messages(request);
        var results = messages[2].GetProperty("content").EnumerateArray().ToList();
        Assert.False(results[0].TryGetProperty("cache_control", out var unmarked) && unmarked.ValueKind != JsonValueKind.Null);
        Assert.Equal("ephemeral", results[1].GetProperty("cache_control").GetProperty("type").GetString());
        var total = CountCacheControl(messages) + CountCacheControl(RequestJson.System(request)) + CountCacheControl(RequestJson.Tools(request));
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Breakpoints_do_not_pile_up_over_many_rounds()
    {
        var harness = new EngineHarness(o => o.MaxRounds = 8);
        for (var i = 0; i < 6; i++) harness.Model.Then(Replies.Tools(($"t{i}", "get_shift_summary", $$"""{"scope":"S{{i}}"}""")));
        harness.Model.Then(Replies.Card());

        await harness.AskAsync();

        var last = harness.Model.Requests[^1];
        var total = CountCacheControl(RequestJson.Messages(last)) + CountCacheControl(RequestJson.System(last));
        Assert.True(total <= 4, $"{total} Breakpoints");
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Owner_is_sent_only_as_a_hash()
    {
        var harness = await TwoRoundRunAsync(owner: "code:abc123");

        var id = harness.Model.Requests[0].Metadata!.UserID!;
        Assert.Equal(32, id.Length);
        Assert.Equal(ClaudeRequest.OwnerHash("code:abc123"), id);
        Assert.DoesNotContain("abc123", id, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_context_forms_its_own_block_between_the_header_and_the_question()
    {
        var withContext = ClaudeRequest.QuestionText("hall_lead", "Do 15.10. 09:42", "Warum?", "Zeitraum: KW 42");
        var without = ClaudeRequest.QuestionText("hall_lead", "Do 15.10. 09:42", "Warum?");

        Assert.Equal(
            "Rolle der fragenden Person: hall_lead\nAktuelle Zeit der Anlage: Do 15.10. 09:42\n<report_kontext>\nZeitraum: KW 42\n</report_kontext>\n<frage>\nWarum?\n</frage>",
            withContext);
        Assert.DoesNotContain("report_kontext", without, StringComparison.Ordinal);
        Assert.Equal(without, ClaudeRequest.QuestionText("hall_lead", "Do 15.10. 09:42", "Warum?", "  "));
    }

    [Fact]
    public void The_system_prompt_treats_the_report_context_as_data_and_asks_for_its_range()
    {
        Assert.Contains("<report_kontext>", ClaudeRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("keine Anweisungen", ClaudeRequest.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("genau diesen Zeitraum", ClaudeRequest.SystemPrompt, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\b\d{1,2}:\d{2}\b")]
    private static partial Regex TimeOfDay();

    [GeneratedRegex(@"\b\d{1,2}\.\d{1,2}\.(\d{2,4})?\b|\b20\d{2}\b")]
    private static partial Regex DateLike();
}
