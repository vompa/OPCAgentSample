using System.Text.Json;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Demo;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Tool-Schleife, Grenzen, Fehlerwege und Kontingent der Claude-Engine gegen einen nachgebauten Anthropic-Client.</summary>
public sealed class ClaudeAnswerEngineTests
{
    private static JsonElement[] Blocks(JsonElement message, string type) =>
        [.. message.GetProperty("content").EnumerateArray().Where(b => b.GetProperty("type").GetString() == type)];

    [Fact]
    public async Task Parallel_tool_calls_return_all_results_in_one_user_message_in_call_order()
    {
        var harness = new EngineHarness();
        harness.Model
            .Then(Replies.ToolsWithThinking(("t1", "get_shift_summary", """{"scope":"Halle A"}"""), ("t2", "get_active_alarms", """{"scope":"Halle A"}""")))
            .Then(Replies.Card());

        var steps = await harness.AskAsync();

        Assert.Equal(2, harness.Model.Requests.Count);
        var second = RequestJson.Messages(harness.Model.Requests[1]);
        Assert.Equal(3, second.GetArrayLength());

        // Assistent: Denkblock (mit Signatur) und beide tool_use unverändert zurück.
        Assert.Equal("assistant", second[1].GetProperty("role").GetString());
        Assert.Equal("sig-1", Blocks(second[1], "thinking").Single().GetProperty("signature").GetString());
        Assert.Equal(["t1", "t2"], Blocks(second[1], "tool_use").Select(b => b.GetProperty("id").GetString()));

        // Alle Ergebnisse in genau einer User-Nachricht, in Aufrufreihenfolge.
        Assert.Equal("user", second[2].GetProperty("role").GetString());
        var results = Blocks(second[2], "tool_result");
        Assert.Equal(["t1", "t2"], results.Select(b => b.GetProperty("tool_use_id").GetString()));
        Assert.All(results, r => Assert.False(r.TryGetProperty("is_error", out var e) && e.GetBoolean()));

        Assert.Equal(["tool_call", "tool_call", "answer"], steps.Select(Kind));
        Assert.Equal(["get_active_alarms", "get_shift_summary"], steps.OfType<ToolCallStep>().Select(s => s.Call.Tool).Order());
    }

    private static string Kind(AnswerStep step) => step switch
    {
        ToolCallStep => "tool_call",
        AnswerDraftStep => "answer_draft",
        AnswerReadyStep => "answer",
        _ => step.GetType().Name,
    };

    [Fact]
    public async Task Card_round_streams_the_draft_before_the_checked_card_and_tool_rounds_stream_none()
    {
        var harness = new EngineHarness();
        harness.Model.DeltaSize = 9;
        harness.Model
            .Then(new ModelReply("tool_use", [new ModelText("Ich schaue nach."), new ModelToolUse("t1", "get_hall_overview", JsonDocument.Parse("{}").RootElement.Clone())], Replies.Usage()))
            .Then(Replies.Card());

        var steps = await harness.AskAsync();

        // Der Satz vor dem Tool-Aufruf ist kein JSON und ergibt keinen Entwurf; die Kartenrunde meldet erst die Überschrift, dann das Was-tun.
        Assert.Equal("tool_call", Kind(steps[0]));
        Assert.Equal("answer", Kind(steps[^1]));
        var drafts = steps.OfType<AnswerDraftStep>().ToList();
        Assert.Equal(2, drafts.Count);
        Assert.Equal("RT-03 steht seit 05:12 und kostet Menge.", drafts[0].Headline);
        Assert.Empty(drafts[0].Todo);
        Assert.Equal(["Instandhaltung beim Greifer von RT-03 nachfragen."], drafts[1].Todo);
        Assert.All(steps.Skip(1).Take(drafts.Count), s => Assert.IsType<AnswerDraftStep>(s));
        Assert.Equal(drafts[0].Headline, steps.OfType<AnswerReadyStep>().Single().Card.Headline);
    }

    [Fact]
    public async Task Failed_validation_ends_with_the_error_card_after_the_draft_and_is_not_cacheable()
    {
        var harness = new EngineHarness();
        harness.Model.DeltaSize = 10;
        harness.Model.Then(Replies.Card("""{"headline":"Sieht gut aus.","todo":[],"table":null,"chart":null,"why":[]}"""));

        var steps = await harness.AskAsync();

        Assert.Equal(["answer_draft", "answer"], steps.Select(Kind));
        var ready = steps.OfType<AnswerReadyStep>().Single();
        Assert.False(ready.Cacheable);
        Assert.NotEqual("Sieht gut aus.", ready.Card.Headline);
    }

    [Fact]
    public async Task A_valid_claude_card_is_marked_cacheable_and_the_usage_is_unchanged_by_streaming()
    {
        var harness = new EngineHarness();
        harness.Model.DeltaSize = 16;
        harness.Model.Then(Replies.Tools(("t1", "get_hall_overview", "{}"))).Then(Replies.Card(usage: Replies.Usage(1500, 250, 1200, 300)));

        var ready = (await harness.AskAsync()).OfType<AnswerReadyStep>().Single();

        Assert.True(ready.Cacheable);
        Assert.False(ready.Repeat);
        Assert.Equal(1000 + 1500, ready.InputTokens);
        Assert.Equal(200 + 250, ready.OutputTokens);
        Assert.Equal(1200, ready.CacheReadTokens);
        Assert.Equal(300, ready.CacheWriteTokens);
    }

    [Fact]
    public async Task Tool_call_step_carries_parameters_duration_source_and_data_stamp_of_the_tool_result()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"Halle A"}"""))).Then(Replies.Card());

        var steps = await harness.AskAsync();

        var call = steps.OfType<ToolCallStep>().Single().Call;
        Assert.Equal("get_shift_summary", call.Tool);
        Assert.Equal("Halle A", call.Parameters.GetProperty("scope").GetString());
        Assert.NotNull(call.DurationMs);
        Assert.Equal("mcp", call.Source);
        Assert.Equal("Redis", call.DataSource);
        Assert.Equal("Do 15.10. 09:40", call.DataAsOf);
        Assert.Equal("ok", call.Outcome);
    }

    [Fact]
    public async Task Tool_runs_with_the_role_of_the_asking_person()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"Halle A"}"""))).Then(Replies.Card());

        await harness.AskAsync(role: "shift_lead");

        Assert.Equal("shift_lead", harness.Toolbox.Calls.Single().Role);
    }

    [Fact]
    public async Task Tool_error_is_returned_to_the_model_as_is_error_and_marked_in_the_step()
    {
        var harness = new EngineHarness();
        harness.Toolbox.Handler = (_, tool, _) => tool == "get_active_alarms"
            ? new ToolOutcome(true, "Die Rolle shift_lead darf Werk nicht abfragen.")
            : new ToolOutcome(false, FakeToolbox.Result(tool));
        harness.Model
            .Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"Halle A"}"""), ("t2", "get_active_alarms", """{"scope":"Werk"}""")))
            .Then(Replies.Card());

        var steps = await harness.AskAsync();

        var results = Blocks(RequestJson.Messages(harness.Model.Requests[1])[2], "tool_result");
        Assert.False(results[0].TryGetProperty("is_error", out var first) && first.GetBoolean());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
        Assert.Contains("darf Werk nicht abfragen", results[1].GetProperty("content").GetString());
        Assert.Equal("error", steps.OfType<ToolCallStep>().Single(s => s.Call.Tool == "get_active_alarms").Call.Outcome);
        Assert.IsType<AnswerReadyStep>(steps[^1]);
    }

    [Fact]
    public async Task Unknown_tool_and_non_object_input_become_errors_without_calling_the_server()
    {
        var harness = new EngineHarness();
        harness.Model
            .Then(Replies.Tools(("t1", "delete_everything", "{}"), ("t2", "get_shift_summary", "[1,2]")))
            .Then(Replies.Card());

        await harness.AskAsync();

        Assert.Empty(harness.Toolbox.Calls);
        var results = Blocks(RequestJson.Messages(harness.Model.Requests[1])[2], "tool_result");
        Assert.All(results, r => Assert.True(r.GetProperty("is_error").GetBoolean()));
    }

    [Fact]
    public async Task Rounds_are_limited_and_the_wrap_up_call_has_no_tools_and_yields_a_partial_answer()
    {
        var harness = new EngineHarness(o => o.MaxRounds = 2);
        harness.Model
            .Then(Replies.Tools(("a1", "get_shift_summary", """{"scope":"Halle A"}""")))
            .Then(Replies.Tools(("a2", "get_active_alarms", """{"scope":"Halle A"}""")))
            .Then(Replies.Card());

        var steps = await harness.AskAsync();

        Assert.Equal(3, harness.Model.Requests.Count);
        Assert.IsType<ToolChoiceAuto>(harness.Model.Requests[0].ToolChoice?.Value);
        Assert.IsType<ToolChoiceNone>(harness.Model.Requests[2].ToolChoice?.Value);
        Assert.Equal(2, steps.OfType<ToolCallStep>().Count());
        Assert.IsType<AnswerReadyStep>(steps[^1]);
    }

    [Fact]
    public async Task Rounds_limit_without_a_usable_wrap_up_gives_an_honest_server_card()
    {
        var harness = new EngineHarness(o => o.MaxRounds = 1);
        harness.Model
            .Then(Replies.Tools(("a1", "get_shift_summary", """{"scope":"Halle A"}""")))
            .Then(Replies.Card("das ist kein JSON"));

        var card = ((AnswerReadyStep)(await harness.AskAsync())[^1]).Card;

        Assert.Contains("Teilantwort", card.Headline);
        Assert.Contains("get_shift_summary", card.Why.Single());
    }

    [Fact]
    public async Task Tool_call_limit_skips_the_excess_calls_and_tells_the_model()
    {
        var harness = new EngineHarness(o => o.MaxToolCalls = 2);
        harness.Model
            .Then(Replies.Tools(
                ("t1", "get_shift_summary", """{"scope":"A"}"""), ("t2", "get_shift_summary", """{"scope":"B"}"""), ("t3", "get_shift_summary", """{"scope":"C"}""")))
            .Then(Replies.Card());

        var steps = await harness.AskAsync();

        Assert.Equal(2, harness.Toolbox.Calls.Count);
        Assert.Equal(2, steps.OfType<ToolCallStep>().Count());
        var results = Blocks(RequestJson.Messages(harness.Model.Requests[1])[2], "tool_result");
        Assert.Equal(3, results.Length);
        Assert.True(results[2].GetProperty("is_error").GetBoolean());
        Assert.Contains("Grenze der Tool-Aufrufe", results[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Refusal_yields_a_refusal_card_without_reading_the_content()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Stop("refusal", new ModelText(Replies.ValidCard)));

        var card = ((AnswerReadyStep)(await harness.AskAsync()).Single()).Card;

        Assert.Equal("Claude beantwortet diese Frage nicht.", card.Headline);
        Assert.Single(harness.Model.Requests);
    }

    [Fact]
    public async Task Max_tokens_never_runs_truncated_tool_calls()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Stop("max_tokens", new ModelToolUse("t1", "get_shift_summary", JsonDocument.Parse("""{"scope":"Hal"}""").RootElement)));

        var steps = await harness.AskAsync();

        Assert.Empty(harness.Toolbox.Calls);
        var card = ((AnswerReadyStep)steps.Single()).Card;
        Assert.Contains("Längenbegrenzung", card.Headline);
    }

    [Fact]
    public async Task Invalid_card_gives_an_understandable_error_card()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Card("""{"headline":"x","todo":[],"table":{"rows":[{"topic":"","data":"a","rating":"ok"}]},"chart":null,"why":[]}"""));

        var card = ((AnswerReadyStep)(await harness.AskAsync()).Single()).Card;

        Assert.Equal("Die Antwort von Claude war nicht brauchbar.", card.Headline);
        Assert.Empty(card.Table.Rows);
    }

    [Fact]
    public async Task Card_gets_data_stamp_and_sources_from_the_tool_results_not_from_the_model()
    {
        var harness = new EngineHarness();
        harness.Toolbox.Handler = (_, tool, _) => new ToolOutcome(false, FakeToolbox.Result(tool, tool == "get_shift_summary" ? "OPC HistoryRead" : "Redis"));
        harness.Model
            .Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"A"}"""), ("t2", "get_active_alarms", """{"scope":"A"}""")))
            .Then(Replies.Card(Replies.ValidCard, Replies.Usage(2000, 300, 800, 100)));

        var ready = (AnswerReadyStep)(await harness.AskAsync())[^1];

        Assert.Equal("09:40", ready.Card.DataAsOf);
        Assert.Equal(["OPC HistoryRead", "Redis"], ready.Card.Sources);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 7, 40, 0, TimeSpan.Zero), ready.DataAsOfUtc);
        Assert.Equal("Tabelle", ready.Card.Form);
        Assert.Null(ready.Card.Notice);
        // Tokens: Summe über alle Modellaufrufe (1000 + 2000 Eingabe, 200 + 300 Ausgabe, Cache getrennt).
        Assert.Equal(3000, ready.InputTokens);
        Assert.Equal(500, ready.OutputTokens);
        Assert.Equal(800, ready.CacheReadTokens);
        Assert.Equal(100, ready.CacheWriteTokens);
    }

    [Fact]
    public async Task Answer_without_tools_says_that_no_data_was_queried()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Card());

        var card = ((AnswerReadyStep)(await harness.AskAsync("Wie wird das Wetter?")).Single()).Card;

        Assert.Equal(["Keine Daten abgefragt"], card.Sources);
        Assert.Equal("09:42", card.DataAsOf);
    }

    [Theory]
    [InlineData(ModelFailureKind.RateLimited, "ausgelastet")]
    [InlineData(ModelFailureKind.ServerError, "nicht verfügbar")]
    [InlineData(ModelFailureKind.Connection, "Verbindung")]
    [InlineData(ModelFailureKind.Misconfigured, "fehlerhaft eingerichtet")]
    [InlineData(ModelFailureKind.Other, "nicht beantworten")]
    public async Task Model_failures_become_understandable_cards_without_details(ModelFailureKind kind, string expected)
    {
        var harness = new EngineHarness();
        harness.Model.ThenFail(kind);

        var card = ((AnswerReadyStep)(await harness.AskAsync()).Single()).Card;

        Assert.Contains(expected, card.Headline);
        Assert.DoesNotContain("sk-ant", card.Headline + string.Join(' ', card.Why));
    }

    [Fact]
    public async Task Failure_in_a_later_round_still_ends_with_an_answer_event()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"A"}"""))).ThenFail(ModelFailureKind.ServerError);

        var steps = await harness.AskAsync();

        Assert.Equal(["tool_call", "answer"], steps.Select(Kind));
    }

    [Fact]
    public async Task Too_long_question_is_rejected_without_a_model_call()
    {
        var harness = new EngineHarness(o => o.MaxQuestionLength = 20);

        var card = ((AnswerReadyStep)(await harness.AskAsync(new string('x', 21))).Single()).Card;

        Assert.Contains("zu lang", card.Headline);
        Assert.Empty(harness.Model.Requests);
    }

    [Fact]
    public async Task Daily_question_quota_per_owner_is_enforced_and_other_owners_are_unaffected()
    {
        var harness = new EngineHarness(o => o.DailyQuestionsPerOwner = 2);
        harness.Model.Then(Replies.Card()).Then(Replies.Card()).Then(Replies.Card());

        await harness.AskAsync(owner: "code:aaa");
        await harness.AskAsync(owner: "code:aaa");
        var rejected = ((AnswerReadyStep)(await harness.AskAsync(owner: "code:aaa")).Single()).Card;
        var other = ((AnswerReadyStep)(await harness.AskAsync(owner: "code:bbb")).Single()).Card;

        Assert.Contains("Tageskontingent an Fragen", rejected.Headline);
        Assert.Equal(3, harness.Model.Requests.Count);
        Assert.StartsWith("RT-03", other.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_quota_counts_the_tokens_of_a_question_and_blocks_the_next_one()
    {
        var harness = new EngineHarness(o => o.DailyTokensPerOwner = 1000);
        harness.Model.Then(Replies.Card(Replies.ValidCard, Replies.Usage(input: 900, output: 150, cacheRead: 5000, cacheWrite: 0)));

        await harness.AskAsync(owner: "code:aaa");
        var rejected = ((AnswerReadyStep)(await harness.AskAsync(owner: "code:aaa")).Single()).Card;

        Assert.Contains("Tokens", rejected.Headline);
        Assert.Single(harness.Model.Requests);
    }

    [Fact]
    public async Task Total_daily_limit_protects_the_costs_of_a_public_demo()
    {
        var harness = new EngineHarness(o => o.DailyQuestionsTotal = 1);
        harness.Model.Then(Replies.Card());

        await harness.AskAsync(owner: "code:aaa");
        var rejected = ((AnswerReadyStep)(await harness.AskAsync(owner: "code:bbb")).Single()).Card;

        Assert.Contains("Kostenlimit", rejected.Headline);
    }

    [Fact]
    public async Task Quota_resets_on_the_next_utc_day()
    {
        var harness = new EngineHarness(o => o.DailyQuestionsPerOwner = 1);
        harness.Model.Then(Replies.Card()).Then(Replies.Card());

        await harness.AskAsync(owner: "code:aaa");
        harness.Time.Advance(TimeSpan.FromDays(1));
        var again = ((AnswerReadyStep)(await harness.AskAsync(owner: "code:aaa")).Single()).Card;

        Assert.StartsWith("RT-03", again.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_key_never_appears_in_logs_or_cards()
    {
        var logger = new CapturingLogger();
        var harness = new EngineHarness(logger: logger);
        harness.Model
            .Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"A"}""")))
            .ThenFail(ModelFailureKind.Misconfigured);

        var steps = await harness.AskAsync();
        var shown = System.Text.Json.JsonSerializer.Serialize(steps.OfType<AnswerReadyStep>().Single().Card);

        Assert.NotEmpty(logger.Messages);
        Assert.DoesNotContain(logger.Messages, m => m.Contains(harness.Options.ApiKey, StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Options.ApiKey, shown, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Model.Requests.Select(r => r.Metadata?.UserID ?? ""), id => id.Contains("aaa", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger : ILogger<ClaudeAnswerEngine>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + (exception is null ? "" : exception.ToString()));
    }
}
