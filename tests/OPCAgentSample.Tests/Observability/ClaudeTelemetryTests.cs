using System.Diagnostics;
using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Observability;
using OPCAgentSample.Tests.Agent;

namespace OPCAgentSample.Tests.Observability;

/// <summary>Claude-Engine mit nachgebautem Client: Tokens nach Art als Metrik, Spans ohne Frageinhalt. Kein echter Aufruf, kein Docker.</summary>
public sealed class ClaudeTelemetryTests
{
    [Fact]
    public async Task Tokens_are_counted_by_kind_and_spans_carry_model_rounds_and_stop_reason_but_never_the_question()
    {
        using var metrics = new MetricCollector();
        using var spans = new SpanCollector();
        var model = $"claude-test-{Guid.NewGuid():N}";
        var secretQuestion = $"GEHEIME-FRAGE-{Guid.NewGuid():N} Wie läuft Presse 3?";
        var harness = new EngineHarness(options => options.Model = model);
        harness.Model
            .Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"Halle A"}""")))
            .Then(Replies.Card(usage: Replies.Usage(input: 1000, output: 200, cacheRead: 300, cacheWrite: 50)));

        var steps = await harness.AskAsync(secretQuestion);

        Assert.IsType<AnswerReadyStep>(steps[^1]);

        // Tokens je Art: Tool-Runde (1000/200) plus Abschluss (1000/200, Cache 300 gelesen, 50 geschrieben).
        double Tokens(string kind) => metrics.Sum(AppTelemetry.ClaudeTokens, m => m.Tag("model") == model && m.Tag("kind") == kind);
        Assert.Equal(2000, Tokens(AppTelemetry.TokenKinds.Input));
        Assert.Equal(400, Tokens(AppTelemetry.TokenKinds.Output));
        Assert.Equal(300, Tokens(AppTelemetry.TokenKinds.CacheRead));
        Assert.Equal(50, Tokens(AppTelemetry.TokenKinds.CacheWrite));

        // Spans: ein Modellaufruf je Runde, beide unter dem Span der Frage.
        var modelCalls = spans.Items.Where(a => a.OperationName == AppTelemetry.Spans.ClaudeModelCall && Equals(a.GetTagItem("claude.model"), model)).ToList();
        Assert.Equal(2, modelCalls.Count);
        Assert.Equal([1, 2], modelCalls.Select(a => (int)a.GetTagItem("claude.round")!).Order());
        Assert.Equal(["end_turn", "tool_use"], modelCalls.Select(a => (string)a.GetTagItem("claude.stop_reason")!).Order());
        var last = modelCalls.Single(a => (int)a.GetTagItem("claude.round")! == 2);
        Assert.Equal(300L, last.GetTagItem("claude.tokens.cache_read"));

        var question = spans.Items.Single(a => a.OperationName == AppTelemetry.Spans.ClaudeQuestion && a.SpanId == modelCalls[0].ParentSpanId);
        Assert.Equal(question.TraceId, modelCalls[1].TraceId);

        // Datenschutz: In keinem Span dieser Frage und in keiner Metrik steht der Fragetext oder der Schlüssel.
        var trace = spans.Items.Where(a => a.TraceId == question.TraceId).ToList();
        var allText = trace.SelectMany(a => a.TagObjects.Select(t => $"{t.Key}={t.Value}").Append(a.DisplayName).Append(a.OperationName))
            .Concat(metrics.Items.SelectMany(m => m.Tags.Select(t => $"{t.Key}={t.Value}")))
            .ToList();
        Assert.All(allText, text =>
        {
            Assert.DoesNotContain("GEHEIME-FRAGE", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Presse 3", text, StringComparison.Ordinal);
            Assert.DoesNotContain(harness.Options.ApiKey, text, StringComparison.Ordinal);
        });

        // Der Tool-Aufruf trägt nur den Namen des Tools, nie die Argumente.
        var toolCall = trace.Single(a => a.OperationName == "claude.tool_call");
        Assert.Equal("get_shift_summary", toolCall.GetTagItem("mcp.tool.name"));
        Assert.DoesNotContain(toolCall.TagObjects, t => t.Value?.ToString()?.Contains("Halle A", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_question_counts_with_its_role_and_a_rejected_question_counts_as_quota_denial()
    {
        using var metrics = new MetricCollector();
        var harness = new EngineHarness(options => options.DailyQuestionsPerOwner = 0);

        var steps = await harness.AskAsync(owner: $"code:{Guid.NewGuid():N}");

        Assert.IsType<AnswerReadyStep>(Assert.Single(steps));
        Assert.True(metrics.Count(AppTelemetry.ClaudeQuotaDenials, m => m.Tag("reason") == nameof(QuotaDenial.OwnerQuestions)) >= 1);
    }

    [Fact]
    public async Task An_accepted_question_is_counted_and_its_duration_recorded()
    {
        using var metrics = new MetricCollector();
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Card());

        await harness.AskAsync(role: "production_lead");

        Assert.True(metrics.Count(AppTelemetry.ClaudeQuestions, m => m.Tag("role") == "production_lead") >= 1);
        Assert.True(metrics.Count(AppTelemetry.ClaudeQuestionDuration) >= 1);
    }
}
