using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Demo;
using StackExchange.Redis;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Mcp;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Agent;

/// <summary>
/// <c>/api/ask</c> mit der Claude-Engine gegen den nachgebauten Anthropic-Client, aber mit dem echten MCP-Endpunkt, echter Historie
/// und echtem Rollen-Scoping (Redis aus der Fixture). Es gibt keinen Aufruf der Claude-API.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class ClaudeEndpointTests(InfrastructureFixture infrastructure)
{
    private const string TestKey = "sk-ant-test-nicht-echt-0123456789";

    private sealed class FactoryLoopback : IMcpLoopback
    {
        public WebApplicationFactory<Program>? Factory { get; set; }

        public Uri Endpoint { get; } = new("http://localhost/mcp");

        public HttpClient CreateClient() => Factory!.CreateClient();
    }

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new ListLogger(this);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Messages) owner.Messages.Add(formatter(state, exception));
            }
        }
    }

    private async Task<(WebApplicationFactory<Program> Factory, ScriptedModelClient Model, ListLoggerProvider Logs)> CreateAsync(bool withKey)
    {
        var settings = new Dictionary<string, string?>();
        if (withKey) settings["Claude:ApiKey"] = TestKey;
        var baseFactory = await McpHost.CreateFactoryAsync(infrastructure, settings);

        var model = new ScriptedModelClient();
        var logs = new ListLoggerProvider();
        var loopback = new FactoryLoopback();
        var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddLogging(logging => logging.AddProvider(logs));
                services.AddSingleton<IClaudeModelClient>(model);
                services.AddSingleton<IMcpLoopback>(loopback);
            });
        });
        loopback.Factory = factory;
        return (factory, model, logs);
    }

    private static async Task<List<(string Name, JsonElement Data)>> AskAsync(
        HttpClient client, string role, string question, bool fresh = false, object? report = null)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/ask", UriKind.Relative), new { role, question, fresh, report });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<(string, JsonElement)>();
        foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..];
            events.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    [Fact]
    public async Task Without_key_the_reference_engine_answers_marked_as_prepared_and_the_startup_log_names_the_mode()
    {
        var (factory, model, logs) = await CreateAsync(withKey: false);
        using var factoryScope = factory;
        using var client = factory.CreateClient();

        var events = await AskAsync(client, "shift_lead", "Was muss ich von der Nachtschicht wissen?");
        var unanswered = await AskAsync(client, "shift_lead", "Eine Frage, die niemand vorbereitet hat");

        Assert.Equal("answer", events.Last(e => e.Name != "done").Name);
        Assert.Equal(ReferenceAnswerEngine.NoticeText, events.Single(e => e.Name == "answer").Data.GetProperty("notice").GetString());
        Assert.Equal(ReferenceAnswerEngine.NoticeText, unanswered.Single(e => e.Name == "answer").Data.GetProperty("notice").GetString());
        Assert.Empty(model.Requests);
        lock (logs.Messages)
        {
            Assert.Contains(logs.Messages, m => m.Contains("Antwort-Engine: Referenz", StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Messages, m => m.Contains(TestKey, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task With_key_Claude_answers_in_sse_order_the_turn_is_saved_and_requery_works()
    {
        var (factory, model, logs) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        const string question = "Wie ist die Lage in Halle A?";
        for (var i = 0; i < 2; i++)
        {
            model.Then(Replies.Tools(("t1", "get_hall_overview", "{}"), ("t2", "get_active_alarms", "{}"))).Then(Replies.Card(usage: Replies.Usage(1500, 250, 1200, 300)));
        }

        var events = await AskAsync(client, "hall_lead", question);

        Assert.Equal(["tool_call", "tool_call", "answer", "done"], events.Select(e => e.Name));
        foreach (var call in events.Where(e => e.Name == "tool_call").Select(e => e.Data))
        {
            Assert.Equal("mcp", call.GetProperty("source").GetString());
            Assert.True(call.GetProperty("durationMs").GetInt32() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(call.GetProperty("dataSource").GetString()));
            Assert.Equal("ok", call.GetProperty("outcome").GetString());
            Assert.Matches(@"^\w{2} \d{2}\.\d{2}\. \d{2}:\d{2}$", call.GetProperty("dataAsOf").GetString());
        }

        var answer = events.Single(e => e.Name == "answer").Data;
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("notice").ValueKind);
        Assert.StartsWith("RT-03", answer.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.Contains("Redis", string.Join(' ', answer.GetProperty("sources").EnumerateArray().Select(s => s.GetString())), StringComparison.Ordinal);
        var done = events.Last().Data;
        Assert.Equal(1500 + 1000, done.GetProperty("tokens").GetProperty("input").GetInt32());
        Assert.Equal(250 + 200, done.GetProperty("tokens").GetProperty("output").GetInt32());
        Assert.Equal(1200, done.GetProperty("tokens").GetProperty("cacheRead").GetInt32());
        Assert.Equal(300, done.GetProperty("tokens").GetProperty("cacheWrite").GetInt32());

        // Verlauf: Antwortkarte, Tool-Aufrufe und Datenstand sind gespeichert; "Neu fragen" (fresh) ist eine zweite Frage derselben Sitzung.
        var again = await AskAsync(client, "hall_lead", question, fresh: true);
        Assert.Equal(["tool_call", "tool_call", "answer", "done"], again.Select(e => e.Name));
        var latest = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/history/latest?role=hall_lead", UriKind.Relative));
        var turns = latest.GetProperty("turns").EnumerateArray().Where(t => t.GetProperty("question").GetString() == question).ToList();
        Assert.Equal(2, turns.Count);
        Assert.Equal(2, turns[0].GetProperty("toolCalls").GetArrayLength());
        Assert.StartsWith("RT-03", turns[0].GetProperty("answerCard").GetProperty("headline").GetString(), StringComparison.Ordinal);

        // Der Schlüssel taucht in keiner Logzeile auf; der Start nannte den Modus Claude.
        lock (logs.Messages)
        {
            Assert.Contains(logs.Messages, m => m.Contains("Antwort-Engine: Claude (Modell claude-haiku-5-5", StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Messages, m => m.Contains(TestKey, StringComparison.Ordinal));
        }
    }

    private static string FirstUserMessage(ScriptedModelClient model, int request = 0) =>
        RequestJson.Messages(model.Requests[request])[0].GetProperty("content").GetString()!;

    [Fact]
    public async Task A_question_from_a_report_carries_range_and_values_computed_by_the_server()
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        model.Then(Replies.Card());

        var events = await AskAsync(
            client, "production_lead", "Was steckt hinter „Störung Gewebezuführung“ an RT-03 in den letzten 4 Wochen und was kostet es?",
            report: new { kind = "machines", range = "fourWeeks", findingId = "loss-top" });

        Assert.Equal("done", events[^1].Name);
        var message = FirstUserMessage(model);
        Assert.Contains("<report_kontext>", message, StringComparison.Ordinal);
        Assert.Contains("Zeitraum-Angabe für die Tools: \"KW 39-42\"", message, StringComparison.Ordinal);
        Assert.Contains("Von Mo 21.09. 06:00 bis", message, StringComparison.Ordinal);
        Assert.Contains("Auffälligkeit im Report: Größter Verlust", message, StringComparison.Ordinal);
        Assert.Contains("Werte von RT-03", message, StringComparison.Ordinal);
        Assert.Contains("Größte Stillstandsgründe: Störung Gewebezuführung", message, StringComparison.Ordinal);
        Assert.True(message.IndexOf("</report_kontext>", StringComparison.Ordinal) < message.IndexOf("<frage>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_question_without_report_has_no_context_block_and_a_vanished_finding_keeps_only_the_range()
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        model.Then(Replies.Card()).Then(Replies.Card());

        _ = await AskAsync(client, "hall_lead", "Wie ist die Lage in Halle A?");
        _ = await AskAsync(client, "hall_lead", "Was ist gestern mit RT-09 gewesen?", report: new { kind = "machines", range = "yesterday", findingId = "oee-RT-09" });

        Assert.DoesNotContain("report_kontext", FirstUserMessage(model), StringComparison.Ordinal);
        var second = FirstUserMessage(model, 1);
        Assert.Contains("Zeitraum-Angabe für die Tools: \"gestern\"", second, StringComparison.Ordinal);
        Assert.Contains("nicht mehr enthalten", second, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders", "week", "x")]
    [InlineData("machines", "year", "x")]
    [InlineData("machines", "week", "")]
    public async Task An_invalid_report_reference_is_rejected_before_anything_is_asked(string kind, string range, string findingId)
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/ask", UriKind.Relative), new { role = "hall_lead", question = "Frage?", report = new { kind, range, findingId } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task The_draft_precedes_the_checked_card_and_the_same_question_again_is_a_repeat_without_model_call_or_quota()
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        model.DeltaSize = 30;
        model.Then(Replies.Tools(("t1", "get_hall_overview", "{}"))).Then(Replies.Card());
        const string question = "Was ist mit RT-03 los?";

        var first = await AskAsync(client, "hall_lead", question);

        Assert.Equal("tool_call", first[0].Name);
        Assert.Equal("done", first[^1].Name);
        Assert.Equal("answer", first[^2].Name);
        var drafts = first.Where(e => e.Name == "answer_draft").Select(e => e.Data).ToList();
        Assert.NotEmpty(drafts);
        Assert.True(first.IndexOf(first.First(e => e.Name == "answer_draft")) > 0 && first.FindLastIndex(e => e.Name == "answer_draft") < first.Count - 2);
        Assert.Equal("RT-03 steht seit 05:12 und kostet Menge.", drafts[0].GetProperty("headline").GetString());
        Assert.False(first[^1].Data.TryGetProperty("repeat", out var firstRepeat) && firstRepeat.GetBoolean());
        var requestsAfterFirst = model.Requests.Count;

        var repeat = await AskAsync(client, "hall_lead", "  " + question.ToUpperInvariant());

        Assert.Equal(["tool_call", "answer", "done"], repeat.Select(e => e.Name));
        Assert.Equal(requestsAfterFirst, model.Requests.Count);
        Assert.True(repeat[^1].Data.GetProperty("repeat").GetBoolean());
        Assert.Equal(0, repeat[^1].Data.GetProperty("tokens").GetProperty("input").GetInt32());
        Assert.Equal(0, repeat[^1].Data.GetProperty("tokens").GetProperty("output").GetInt32());
        Assert.Equal(first.Single(e => e.Name == "answer").Data.GetProperty("headline").GetString(), repeat[1].Data.GetProperty("headline").GetString());
        Assert.Equal(first[^1].Data.GetProperty("dataAsOfText").GetString(), repeat[^1].Data.GetProperty("dataAsOfText").GetString());

        // „Neu fragen“ (fresh) fragt Claude wirklich noch einmal und liefert keine Wiederholung.
        model.Then(Replies.Card());
        var fresh = await AskAsync(client, "hall_lead", question, fresh: true);

        Assert.Equal(requestsAfterFirst + 1, model.Requests.Count);
        Assert.False(fresh[^1].Data.TryGetProperty("repeat", out var freshRepeat) && freshRepeat.GetBoolean());

        // Die Wiederholung steht im Verlauf (drei Fragen) und im Audit-Log als eigene Aktion.
        var latest = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/history/latest?role=hall_lead", UriKind.Relative));
        Assert.Equal(3, latest.GetProperty("turns").EnumerateArray().Count(t => string.Equals(t.GetProperty("question").GetString()!.Trim(), question, StringComparison.OrdinalIgnoreCase)));
        var audit = await AuditStream.ReadAsync(factory.Services.GetRequiredService<IConnectionMultiplexer>());
        Assert.Contains(audit, a => a.Action == "ask.repeat");
    }

    [Fact]
    public async Task Tool_calls_run_with_the_role_of_the_asker_and_are_audited_as_the_agent_client()
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        model.Then(Replies.Tools(("t1", "get_hall_overview", """{"hallId":"A"}"""))).Then(Replies.Card());

        await AskAsync(client, "shift_lead", "Wie ist die Lage?");

        var entry = (await AuditStream.ReadAsync(factory.Services.GetRequiredService<IConnectionMultiplexer>()))
            .Single(a => a.Client == InternalAgentKeys.ClientName("shift_lead"));
        Assert.Equal("shift_lead", entry.Role);
        Assert.Equal("get_hall_overview", entry.Action);
        Assert.Equal(AuditOutcomes.Ok, entry.Outcome);
        Assert.Equal(AuditChannels.Mcp, entry.Channel);
    }

    [Fact]
    public async Task Role_scoping_denies_the_plant_to_the_shift_lead_and_the_model_gets_the_error_text()
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        model.Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"Werk"}"""))).Then(Replies.Card());
        model.Then(Replies.Tools(("t1", "get_shift_summary", """{"scope":"Werk"}"""))).Then(Replies.Card());

        var denied = await AskAsync(client, "shift_lead", "Wie läuft das ganze Werk?");
        var allowed = await AskAsync(client, "production_lead", "Wie läuft das ganze Werk?");

        var deniedResult = RequestJson.Messages(model.Requests[1])[2].GetProperty("content")[0];
        Assert.True(deniedResult.GetProperty("is_error").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(deniedResult.GetProperty("content").GetString()));
        Assert.Equal("error", denied.First(e => e.Name == "tool_call").Data.GetProperty("outcome").GetString());
        Assert.Equal("answer", denied.Last(e => e.Name != "done").Name);

        var allowedResult = RequestJson.Messages(model.Requests[3])[2].GetProperty("content")[0];
        Assert.False(allowedResult.TryGetProperty("is_error", out var isError) && isError.GetBoolean());
        Assert.Equal("ok", allowed.First(e => e.Name == "tool_call").Data.GetProperty("outcome").GetString());

        var audit = (await AuditStream.ReadAsync(factory.Services.GetRequiredService<IConnectionMultiplexer>()))
            .Single(a => a.Client == InternalAgentKeys.ClientName("shift_lead"));
        Assert.Equal(AuditOutcomes.Denied, audit.Outcome);
    }

    [Fact]
    public async Task Tool_list_comes_from_the_mcp_server_and_has_all_17_read_only_tools()
    {
        var (factory, model, _) = await CreateAsync(withKey: true);
        using var factoryScope = factory;
        using var client = factory.CreateClient();
        model.Then(Replies.Card());

        await AskAsync(client, "hall_lead", "Wie ist die Lage?");

        var names = RequestJson.Tools(model.Requests[0]).EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal(17, names.Count);
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        Assert.Contains("get_hall_overview", names);
        Assert.Contains("trace_batch", names);
        Assert.All(RequestJson.Tools(model.Requests[0]).EnumerateArray(), t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("description").GetString())));
    }
}
