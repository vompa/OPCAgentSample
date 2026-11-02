using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>
/// <see cref="ChatClientModelClient"/> auf zwei Ebenen: mit einem nachgebauten <see cref="IChatClient"/> (Form der Nachrichten, Optionen,
/// Usage-Rechnung) und über den echten Anthropic-Adapter mit aufgezeichnetem HTTP-Body (was der Adapter daraus macht: unveränderter Text,
/// Thinking, <c>is_error</c>, Stopp-Grund aus echten Stream-Ereignissen). Es gibt keinen Netzzugriff und keinen Schlüssel.
/// </summary>
public sealed class ChatClientModelClientTests
{
    private static readonly JsonElement EmptySchema = JsonDocument.Parse("""{"type":"object","properties":{"hallId":{"type":"string"}},"required":["hallId"]}""").RootElement.Clone();

    private static ClaudeOptions Options(string effort = "medium", int maxTokens = 6000) =>
        new() { Model = "claude-haiku-5-5", Effort = effort, MaxTokens = maxTokens };

    private static AgentModelRequest Request(IReadOnlyList<AgentTurn> turns, bool allowTools = true, params AgentTool[] tools) =>
        new(tools, turns, "owner-hash", allowTools);

    private static IReadOnlyList<AgentTurn> Question() => [new UserQuestionTurn("Wie ist die Lage?")];

    private static ChatClientModelClient ClientOver(IChatClient chat, ClaudeOptions? options = null) =>
        new(chat, new AnthropicRequestDialect(Microsoft.Extensions.Options.Options.Create(options ?? Options())), new AnthropicErrorMapper());

    private static async Task<ModelReply> ReplyAsync(FakeChatClient chat, Action<string>? onDelta = null) =>
        await ClientOver(chat).CreateAsync(Request(Question()), onDelta, CancellationToken.None);

    private static bool IsMarked(AIContent content) =>
        content.RawRepresentation is ContentBlockParam raw && raw.TryPickToolResult(out var result) && result.CacheControl is not null;

    // ---------- Form der Anfrage (nachgebauter IChatClient) ----------

    [Fact]
    public async Task Tool_results_share_one_message_and_only_the_last_one_of_the_last_round_is_marked()
    {
        var chat = new FakeChatClient(_ => [Text("{}")]);
        IReadOnlyList<AgentTurn> turns =
        [
            new UserQuestionTurn("Frage"),
            new AssistantTurn([new ModelToolUse("a", "t", Json("{}")), new ModelToolUse("b", "t", Json("{}"))]),
            new ToolResultsTurn([new ToolResultItem("a", "ra", false), new ToolResultItem("b", "rb", false)]),
            new AssistantTurn([new ModelToolUse("c", "t", Json("{}")), new ModelToolUse("d", "t", Json("{}"))]),
            new ToolResultsTurn([new ToolResultItem("c", "rc", false), new ToolResultItem("d", "rd", false)]),
        ];

        await ClientOver(chat).CreateAsync(Request(turns), null, CancellationToken.None);

        var userMessages = chat.Messages.Where(m => m.Role == ChatRole.User).ToList();
        Assert.Equal(3, userMessages.Count); // Frage, Ergebnisse der Runde 1, Ergebnisse der Runde 2
        Assert.Equal(2, userMessages[1].Contents.Count);
        Assert.Equal(2, userMessages[2].Contents.Count);
        var marked = chat.Messages.SelectMany(m => m.Contents).Where(IsMarked).Select(c => ((FunctionResultContent)c).CallId);
        Assert.Equal(["d"], marked);
    }

    [Fact]
    public async Task The_system_prompt_is_the_first_message_and_carries_the_cache_marker()
    {
        var chat = new FakeChatClient(_ => [Text("{}")]);

        await ReplyAsync(chat);

        var system = Assert.Single(chat.Messages, m => m.Role == ChatRole.System);
        Assert.Same(chat.Messages[0], system);
        var block = Assert.IsType<TextBlockParam>(Assert.Single(system.Contents).RawRepresentation);
        Assert.NotNull(block.CacheControl);
    }

    [Fact]
    public async Task Tools_are_declared_sorted_by_name_with_their_schema()
    {
        var chat = new FakeChatClient(_ => [Text("{}")]);
        var request = Request(Question(), true, new AgentTool("z_tool", "letztes", EmptySchema), new AgentTool("a_tool", "erstes", EmptySchema));

        await ClientOver(chat).CreateAsync(request, null, CancellationToken.None);

        var tools = chat.Options!.Tools!.Cast<AIFunctionDeclaration>().ToList();
        Assert.Equal(["a_tool", "z_tool"], tools.Select(t => t.Name));
        Assert.Equal("erstes", tools[0].Description);
        Assert.Equal("string", tools[0].JsonSchema.GetProperty("properties").GetProperty("hallId").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(true, "auto")]
    [InlineData(false, "none")]
    public async Task The_tool_mode_follows_AllowTools_and_parallel_calls_stay_allowed(bool allowTools, string expectedMode)
    {
        var chat = new FakeChatClient(_ => [Text("{}")]);

        await ClientOver(chat).CreateAsync(Request(Question(), allowTools), null, CancellationToken.None);

        var mode = chat.Options!.ToolMode switch { AutoChatToolMode => "auto", NoneChatToolMode => "none", _ => "other" };
        Assert.Equal(expectedMode, mode);
        Assert.True(chat.Options.AllowMultipleToolCalls);
        Assert.Null(chat.Options.Reasoning);
        Assert.Null(chat.Options.MaxOutputTokens);
    }

    [Fact]
    public async Task The_response_format_carries_the_answer_card_schema()
    {
        var chat = new FakeChatClient(_ => [Text("{}")]);

        await ReplyAsync(chat);

        var format = Assert.IsType<ChatResponseFormatJson>(chat.Options!.ResponseFormat);
        Assert.True(format.Schema!.Value.GetProperty("properties").TryGetProperty("headline", out _));
    }

    // ---------- Antwort (nachgebauter IChatClient) ----------

    [Fact]
    public async Task Usage_is_mapped_without_double_counting()
    {
        var usage = new UsageDetails
        {
            InputTokenCount = 150, // Adapter: Input 100 + Cache-Read 30 + Cache-Write 20
            CachedInputTokenCount = 30,
            OutputTokenCount = 7,
            AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["CacheCreationInputTokens"] = 20 },
        };
        var chat = new FakeChatClient(_ => [Text("{}"), new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(usage)])]);

        var reply = await ReplyAsync(chat);

        Assert.Equal(new ModelUsage(100, 7, 30, 20), reply.Usage);
    }

    [Fact]
    public async Task Missing_usage_values_count_as_zero()
    {
        var chat = new FakeChatClient(_ => [Text("{}")]);

        var reply = await ReplyAsync(chat);

        Assert.Equal(new ModelUsage(0, 0, 0, 0), reply.Usage);
    }

    [Fact]
    public async Task Invalid_tool_json_becomes_a_non_object_input()
    {
        var call = new FunctionCallContent("toolu_1", "get_hall_overview") { Exception = new JsonException("kaputt") };
        var chat = new FakeChatClient(_ => [new ChatResponseUpdate(ChatRole.Assistant, [call])]);

        var reply = await ReplyAsync(chat);

        var use = Assert.IsType<ModelToolUse>(Assert.Single(reply.Blocks));
        Assert.Equal("toolu_1", use.Id);
        Assert.NotEqual(JsonValueKind.Object, use.Input.ValueKind);
    }

    [Fact]
    public async Task Only_answer_text_reaches_the_text_delta_callback()
    {
        var chat = new FakeChatClient(_ =>
        [
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("Ich denke nach")]),
            Text("{\"a\""),
            Text(string.Empty),
            Text(":1}"),
        ]);
        var deltas = new List<string>();

        var reply = await ReplyAsync(chat, deltas.Add);

        Assert.Equal(["{\"a\"", ":1}"], deltas);
        Assert.Equal("{\"a\":1}", Assert.IsType<ModelText>(reply.Blocks[^1]).Text);
    }

    [Theory]
    [InlineData(typeof(AnthropicRateLimitException), ModelFailureKind.RateLimited)]
    [InlineData(typeof(Anthropic5xxException), ModelFailureKind.ServerError)]
    [InlineData(typeof(AnthropicUnauthorizedException), ModelFailureKind.Misconfigured)]
    [InlineData(typeof(AnthropicForbiddenException), ModelFailureKind.Misconfigured)]
    [InlineData(typeof(AnthropicNotFoundException), ModelFailureKind.Misconfigured)]
    [InlineData(typeof(AnthropicBadRequestException), ModelFailureKind.Misconfigured)]
    [InlineData(typeof(AnthropicUnprocessableEntityException), ModelFailureKind.Misconfigured)]
    [InlineData(typeof(AnthropicIOException), ModelFailureKind.Connection)]
    [InlineData(typeof(HttpRequestException), ModelFailureKind.Connection)]
    public async Task Every_SDK_failure_maps_to_the_same_kind_as_before(System.Type failure, ModelFailureKind expected)
    {
        var chat = new FakeChatClient(_ => throw (Exception)RuntimeHelpers.GetUninitializedObject(failure));

        var thrown = await Assert.ThrowsAsync<ModelCallException>(() => ReplyAsync(chat));

        Assert.Equal(expected, thrown.Kind);
    }

    [Fact]
    public async Task Cancellation_is_not_mapped()
    {
        var chat = new FakeChatClient(_ => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => ReplyAsync(chat));
    }

    [Fact]
    public async Task Unknown_exceptions_pass_through_unchanged()
    {
        var chat = new FakeChatClient(_ => throw new InvalidOperationException("anderer Fehler"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => ReplyAsync(chat));

        Assert.Equal("anderer Fehler", thrown.Message);
    }

    // ---------- Über den echten Adapter (aufgezeichneter HTTP-Body) ----------

    [Fact]
    public async Task Thinking_and_redacted_thinking_go_back_as_the_same_block_types_through_the_real_adapter()
    {
        IReadOnlyList<AgentTurn> turns =
        [
            new UserQuestionTurn("Frage"),
            new AssistantTurn([new ModelThinking("Erst prüfen", "sig-abc=="), new ModelRedactedThinking("opaque-data"), new ModelToolUse("t1", "get_hall_overview", Json("""{"hallId":"A"}"""))]),
            new ToolResultsTurn([new ToolResultItem("t1", "ok", false)]),
        ];

        var body = await SendThroughRealAdapterAsync(Request(turns));

        var content = body.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal(["thinking", "redacted_thinking", "tool_use"], content.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal("Erst prüfen", content[0].GetProperty("thinking").GetString());
        Assert.Equal("sig-abc==", content[0].GetProperty("signature").GetString());
        Assert.Equal("opaque-data", content[1].GetProperty("data").GetString());
        Assert.Equal("A", content[2].GetProperty("input").GetProperty("hallId").GetString());
    }

    [Fact]
    public async Task Assistant_text_with_trailing_whitespace_goes_back_unchanged_through_the_real_adapter()
    {
        IReadOnlyList<AgentTurn> turns =
        [
            new UserQuestionTurn("Frage"),
            new AssistantTurn([new ModelText("Ich schaue nach.  \n"), new ModelToolUse("t1", "x", Json("{}"))]),
            new ToolResultsTurn([new ToolResultItem("t1", "ok", false)]),
        ];

        var body = await SendThroughRealAdapterAsync(Request(turns));

        Assert.Equal("Ich schaue nach.  \n", body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Tool_results_keep_is_error_and_the_cache_marker_only_on_the_last_one_through_the_real_adapter()
    {
        IReadOnlyList<AgentTurn> turns =
        [
            new UserQuestionTurn("Frage"),
            new AssistantTurn([new ModelToolUse("a", "x", Json("{}")), new ModelToolUse("b", "x", Json("{}"))]),
            new ToolResultsTurn([new ToolResultItem("a", "Fehler", true), new ToolResultItem("b", "Daten", false)]),
        ];

        var body = await SendThroughRealAdapterAsync(Request(turns));

        var results = body.GetProperty("messages")[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["a", "b"], results.Select(r => r.GetProperty("tool_use_id").GetString()));
        Assert.True(results[0].GetProperty("is_error").GetBoolean());
        Assert.False(results[0].TryGetProperty("cache_control", out _));
        Assert.True(results[1].TryGetProperty("cache_control", out _));
    }

    [Fact]
    public async Task Effort_max_sets_output_config_and_leaves_thinking_out()
    {
        var body = await SendThroughRealAdapterAsync(Request(Question()), Options(effort: "max"));

        Assert.Equal("max", body.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.True(body.GetProperty("output_config").TryGetProperty("format", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task Model_max_tokens_and_owner_hash_come_from_the_options_and_the_request()
    {
        var body = await SendThroughRealAdapterAsync(Request(Question()), Options(maxTokens: 4321));

        Assert.Equal("claude-haiku-5-5", body.GetProperty("model").GetString());
        Assert.Equal(4321, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("owner-hash", body.GetProperty("metadata").GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task The_system_prompt_goes_out_as_a_cached_system_block_through_the_real_adapter()
    {
        var body = await SendThroughRealAdapterAsync(Request(Question()));

        var system = Assert.Single(body.GetProperty("system").EnumerateArray());
        Assert.Equal(AgentPrompt.SystemPrompt, system.GetProperty("text").GetString());
        Assert.True(system.TryGetProperty("cache_control", out _));
    }

    [Theory]
    [InlineData("end_turn", ModelStop.Done)]
    [InlineData("stop_sequence", ModelStop.Done)]
    [InlineData("tool_use", ModelStop.ToolCalls)]
    [InlineData("max_tokens", ModelStop.MaxTokens)]
    [InlineData("refusal", ModelStop.Refused)]
    [InlineData("pause_turn", ModelStop.Other)]
    [InlineData("model_context_window_exceeded", ModelStop.Other)]
    public async Task Raw_stop_reasons_map_to_the_stop_enum_and_stay_readable(string raw, ModelStop expected)
    {
        var reply = await ReplyFromStreamAsync(TextStream($"\"{raw}\""));

        Assert.Equal(expected, reply.Stop);
        Assert.Equal(raw, reply.RawStopReason);
    }

    [Fact]
    public async Task A_missing_stop_reason_maps_to_other_with_a_null_raw_value()
    {
        var reply = await ReplyFromStreamAsync(TextStream("null"));

        Assert.Equal(ModelStop.Other, reply.Stop);
        Assert.Null(reply.RawStopReason);
    }

    [Fact]
    public async Task Thinking_redacted_thinking_text_and_tool_use_from_a_real_stream_become_model_blocks()
    {
        string[] events =
        [
            """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":100,"output_tokens":1,"cache_read_input_tokens":30,"cache_creation_input_tokens":20}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Erst prüfen"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc=="}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"redacted_thinking","data":"opaque-data"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"Ich "}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"schaue nach."}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_hall_overview","input":{}}}""",
            """{"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"{\"hallId\":"}}""",
            """{"type":"content_block_delta","index":3,"delta":{"type":"input_json_delta","partial_json":"\"A\"}"}}""",
            """{"type":"content_block_stop","index":3}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":42}}""",
            """{"type":"message_stop"}""",
        ];
        var deltas = new List<string>();

        var reply = await ReplyFromStreamAsync(events, deltas.Add);

        var thinking = Assert.IsType<ModelThinking>(reply.Blocks[0]);
        Assert.Equal("Erst prüfen", thinking.Thinking);
        Assert.Equal("sig-abc==", thinking.Signature);
        Assert.Equal("opaque-data", Assert.IsType<ModelRedactedThinking>(reply.Blocks[1]).Data);
        Assert.Equal("Ich schaue nach.", Assert.IsType<ModelText>(reply.Blocks[2]).Text);
        var use = Assert.IsType<ModelToolUse>(reply.Blocks[3]);
        Assert.Equal(("toolu_1", "get_hall_overview", "A"), (use.Id, use.Name, use.Input.GetProperty("hallId").GetString()));
        Assert.Equal(["Ich ", "schaue nach."], deltas);
        Assert.Equal(new ModelUsage(100, 42, 30, 20), reply.Usage);
    }

    // ---------- Hilfen ----------

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text);

    private static string[] TextStream(string stopReasonJson) =>
    [
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{}"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":STOP,"stop_sequence":null},"usage":{"output_tokens":2}}""".Replace("STOP", stopReasonJson, StringComparison.Ordinal),
        """{"type":"message_stop"}""",
    ];

    private static async Task<ModelReply> ReplyFromStreamAsync(string[] events, Action<string>? onDelta = null)
    {
        var handler = new RecordingHandler(events);
        var client = ClientOver(RealAdapter(handler));
        return await client.CreateAsync(Request(Question()), onDelta, CancellationToken.None);
    }

    private static async Task<JsonElement> SendThroughRealAdapterAsync(AgentModelRequest request, ClaudeOptions? options = null)
    {
        var handler = new RecordingHandler(TextStream("\"end_turn\""));
        var client = ClientOver(RealAdapter(handler, options), options);
        await client.CreateAsync(request, null, CancellationToken.None);
        return JsonDocument.Parse(handler.Body!).RootElement.Clone();
    }

    private static IChatClient RealAdapter(RecordingHandler handler, ClaudeOptions? options = null)
    {
        var settings = options ?? Options();
        var anthropic = new AnthropicClient
        {
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            MaxRetries = 0,
            HttpClient = new HttpClient(handler),
        };
        return anthropic.AsIChatClient(settings.Model, settings.MaxTokens);
    }

    /// <summary>Zeichnet den Body der Anfrage auf und antwortet mit einem festen SSE-Stream; es gibt kein Netz.</summary>
    private sealed class RecordingHandler(string[] events) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var sse = new StringBuilder();
            foreach (var json in events)
            {
                using var document = JsonDocument.Parse(json);
                sse.Append("event: ").Append(document.RootElement.GetProperty("type").GetString()).Append('\n');
                sse.Append("data: ").Append(json).Append("\n\n");
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    /// <summary>Nachgebauter <see cref="IChatClient"/>: merkt sich Nachrichten und Optionen und liefert skriptierte Updates.</summary>
    private sealed class FakeChatClient(Func<IEnumerable<ChatMessage>, IEnumerable<ChatResponseUpdate>> script) : IChatClient
    {
        public List<ChatMessage> Messages { get; private set; } = [];

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Der Client streamt immer.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Messages = messages.ToList();
            Options = options;
            foreach (var update in script(Messages))
            {
                await Task.Yield();
                yield return update;
            }
        }

        public object? GetService(System.Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
