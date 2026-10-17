using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Agent;

/// <summary>Wiederholungsschutz: gleiche Frage desselben Besitzers in derselben Rolle wird ohne Modellaufruf und ohne Kontingent beantwortet.</summary>
public sealed class RepeatingAnswerEngineTests
{
    private const string Question = "Wie ist die Lage in Halle A?";

    private static RepeatingAnswerEngine Wrap(EngineHarness harness) =>
        new(harness.Engine, Microsoft.Extensions.Options.Options.Create(harness.Options), harness.Time, NullLogger<RepeatingAnswerEngine>.Instance);

    private static async Task<List<AnswerStep>> AskAsync(
        RepeatingAnswerEngine engine, string question = Question, string role = "hall_lead", string owner = "code:abc", bool fresh = false)
    {
        var steps = new List<AnswerStep>();
        await foreach (var step in engine.AskAsync(new AskRequest(role, question, owner, fresh))) steps.Add(step);
        return steps;
    }

    private static AnswerReadyStep Ready(IEnumerable<AnswerStep> steps) => steps.OfType<AnswerReadyStep>().Single();

    private static void ScriptAnswers(EngineHarness harness, int count)
    {
        for (var i = 0; i < count; i++) harness.Model.Then(Replies.Tools(("t1", "get_hall_overview", "{}"))).Then(Replies.Card());
    }

    [Fact]
    public async Task Same_question_again_is_answered_from_memory_with_the_tool_calls_zero_tokens_and_the_original_data_stamp()
    {
        var harness = new EngineHarness();
        ScriptAnswers(harness, 2);
        var engine = Wrap(harness);

        var first = await AskAsync(engine);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        var second = await AskAsync(engine);

        Assert.Equal(2, harness.Model.Requests.Count); // nur die beiden Runden der ersten Frage
        Assert.False(Ready(first).Repeat);
        var repeat = Ready(second);
        Assert.True(repeat.Repeat);
        Assert.False(repeat.Cacheable);
        Assert.Equal((0, 0, 0, 0), (repeat.InputTokens, repeat.OutputTokens, repeat.CacheReadTokens, repeat.CacheWriteTokens));
        Assert.Equal(Ready(first).Card, repeat.Card);
        Assert.Equal(Ready(first).DataAsOfUtc, repeat.DataAsOfUtc);
        Assert.Equal(first.OfType<ToolCallStep>().Select(s => s.Call), second.OfType<ToolCallStep>().Select(s => s.Call));
        Assert.Single(harness.Toolbox.Calls); // die Tools laufen nicht noch einmal
    }

    [Fact]
    public async Task A_repeat_does_not_count_against_the_daily_quota()
    {
        var harness = new EngineHarness(o => o.DailyQuestionsPerOwner = 1);
        ScriptAnswers(harness, 1);
        var engine = Wrap(harness);

        await AskAsync(engine);
        var repeat = Ready(await AskAsync(engine));
        var other = Ready(await AskAsync(engine, "Welche Alarme sind offen?")); // das einzige Kontingent der ersten Frage ist verbraucht

        Assert.True(repeat.Repeat);
        Assert.Contains("Tageskontingent", other.Card.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Theory]
    [InlineData("wie ist die lage in halle a")]
    [InlineData("  Wie  ist die\tLage in   Halle A ?! ")]
    [InlineData("WIE IST DIE LAGE IN HALLE A.")]
    public async Task Case_spacing_and_trailing_punctuation_do_not_make_a_different_question(string variant)
    {
        var harness = new EngineHarness();
        ScriptAnswers(harness, 1);
        var engine = Wrap(harness);

        await AskAsync(engine);
        var second = Ready(await AskAsync(engine, variant));

        Assert.True(second.Repeat);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task Another_owner_or_role_or_question_is_no_hit()
    {
        var harness = new EngineHarness();
        ScriptAnswers(harness, 4);
        var engine = Wrap(harness);
        await AskAsync(engine);

        var otherOwner = Ready(await AskAsync(engine, owner: "code:xyz"));
        var otherRole = Ready(await AskAsync(engine, role: "shift_lead"));
        var otherQuestion = Ready(await AskAsync(engine, "Welche Alarme sind offen?"));

        Assert.All(new[] { otherOwner, otherRole, otherQuestion }, r => Assert.False(r.Repeat));
        Assert.Equal(8, harness.Model.Requests.Count);
    }

    [Fact]
    public void The_same_question_from_another_report_source_has_another_key()
    {
        var plain = new AskRequest("hall_lead", Question, "code:abc");
        var fromWeek = plain with { Report = new ReportContext("machines|week|loss-top", "Zeitraum A") };
        var fromFourWeeks = plain with { Report = new ReportContext("machines|fourWeeks|loss-top", "Zeitraum B") };

        Assert.Equal(3, new[] { plain, fromWeek, fromFourWeeks }.Select(RepeatingAnswerEngine.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(RepeatingAnswerEngine.Key(fromWeek), RepeatingAnswerEngine.Key(fromWeek with { Report = new ReportContext("machines|week|loss-top", "Neuer Datenstand") }));
    }

    [Fact]
    public async Task The_stored_answer_expires_after_the_window()
    {
        var harness = new EngineHarness(o => o.RepeatWindowSeconds = 300);
        ScriptAnswers(harness, 2);
        var engine = Wrap(harness);
        await AskAsync(engine);

        harness.Time.Advance(TimeSpan.FromSeconds(299));
        Assert.True(Ready(await AskAsync(engine)).Repeat);

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        var expired = Ready(await AskAsync(engine));

        Assert.False(expired.Repeat);
        Assert.Equal(4, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task Fresh_ignores_the_stored_answer_and_overwrites_it()
    {
        var harness = new EngineHarness();
        harness.Model.Then(Replies.Card(Replies.ValidCard.Replace("RT-03 steht", "ERSTE Antwort steht", StringComparison.Ordinal)))
            .Then(Replies.Card(Replies.ValidCard.Replace("RT-03 steht", "ZWEITE Antwort steht", StringComparison.Ordinal)));
        var engine = Wrap(harness);
        await AskAsync(engine);

        var fresh = Ready(await AskAsync(engine, fresh: true));
        var afterwards = Ready(await AskAsync(engine));

        Assert.False(fresh.Repeat);
        Assert.StartsWith("ZWEITE", fresh.Card.Headline, StringComparison.Ordinal);
        Assert.True(afterwards.Repeat);
        Assert.StartsWith("ZWEITE", afterwards.Card.Headline, StringComparison.Ordinal);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task Error_and_limit_cards_are_not_stored()
    {
        var harness = new EngineHarness();
        harness.Model.ThenFail(ModelFailureKind.ServerError).Then(Replies.Card());
        var engine = Wrap(harness);

        var failed = Ready(await AskAsync(engine));
        var again = Ready(await AskAsync(engine));
        var third = Ready(await AskAsync(engine));

        Assert.False(failed.Cacheable);
        Assert.False(again.Repeat);
        Assert.StartsWith("RT-03", again.Card.Headline, StringComparison.Ordinal);
        Assert.True(third.Repeat);
        Assert.Equal(2, harness.Model.Requests.Count);
    }

    [Fact]
    public async Task Window_zero_switches_the_protection_off()
    {
        var harness = new EngineHarness(o => o.RepeatWindowSeconds = 0);
        ScriptAnswers(harness, 2);
        var engine = Wrap(harness);

        await AskAsync(engine);
        var second = Ready(await AskAsync(engine));

        Assert.False(second.Repeat);
        Assert.Equal(4, harness.Model.Requests.Count);
    }

    /// <summary>Innere Engine, die auf ein Signal wartet, zählt ihre Aufrufe und liefert eine Karte oder eine nicht speicherbare Karte.</summary>
    private sealed class GatedEngine(bool cacheable = true) : IAnswerEngine
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async IAsyncEnumerable<AnswerStep> AskAsync(AskRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            var table = new AnswerTable(["Thema", "Daten", "Bewertung"], []);
            yield return new AnswerReadyStep(
                new AnswerCard("Antwort " + Calls, [], table, null, [], "09:40", ["Redis"]), 10, 5, DataAsOfUtc: new DateTimeOffset(2026, 10, 15, 7, 40, 0, TimeSpan.Zero),
                Cacheable: cacheable);
        }
    }

    private static RepeatingAnswerEngine Wrap(IAnswerEngine inner, ManualTimeProvider time) =>
        new(inner, Microsoft.Extensions.Options.Options.Create(new ClaudeOptions { ApiKey = "sk-ant-test" }), time, NullLogger<RepeatingAnswerEngine>.Instance);

    private static ManualTimeProvider NewTime() => new(new DateTimeOffset(2026, 10, 15, 7, 42, 0, TimeSpan.Zero));

    [Fact]
    public async Task A_concurrent_identical_request_waits_for_the_first_and_calls_the_model_only_once()
    {
        var inner = new GatedEngine();
        var engine = Wrap(inner, NewTime());

        var first = AskAsync(engine);
        await inner.Started.Task;
        var second = AskAsync(engine); // Doppelklick, während die erste noch läuft
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        inner.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, inner.Calls);
        Assert.False(Ready(results[0]).Repeat);
        var waiting = Ready(results[1]);
        Assert.True(waiting.Repeat);
        Assert.Equal(Ready(results[0]).Card, waiting.Card);
    }

    [Fact]
    public async Task A_waiting_request_asks_itself_when_the_first_one_produced_no_storable_answer()
    {
        var inner = new GatedEngine(cacheable: false);
        var engine = Wrap(inner, NewTime());

        var first = AskAsync(engine);
        await inner.Started.Task;
        var second = AskAsync(engine);
        await Task.Delay(100);
        inner.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(2, inner.Calls);
        Assert.All(results, r => Assert.False(Ready(r).Repeat));
    }

    [Fact]
    public async Task A_cancelled_first_request_does_not_leave_waiters_hanging()
    {
        var inner = new GatedEngine();
        var engine = Wrap(inner, NewTime());
        using var cancel = new CancellationTokenSource();

        var first = Task.Run(async () =>
        {
            await foreach (var unused in engine.AskAsync(new AskRequest("hall_lead", Question, "code:abc"), cancel.Token))
            {
            }
        });
        await inner.Started.Task;
        var second = AskAsync(engine);
        await Task.Delay(100);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        inner.Release();

        var result = Ready(await second.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.False(result.Repeat);
        Assert.Equal(2, inner.Calls);
    }
}
