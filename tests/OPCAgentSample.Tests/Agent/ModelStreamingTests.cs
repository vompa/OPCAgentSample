using System.Text.Json;
using Anthropic.Models.Messages;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>
/// Zusammenbau der Antwort aus echten SDK-Streaming-Ereignissen (aus JSON gelesen, wie sie die API sendet). Ein nachgebauter Client
/// würde SDK-Eigenheiten verdecken (z. B. den Stopp-Grund mit JSON-Anführungszeichen); hier läuft derselbe Weg wie im Betrieb.
/// </summary>
public sealed class ModelStreamingTests
{
    private static readonly string[] s_events =
    [
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":150,"output_tokens":1,"cache_read_input_tokens":11798,"cache_creation_input_tokens":20}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Erst prüfen"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc=="}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"{\"headline\":\"RT-03 "}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"steht.\",\"todo\":[]}"}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"get_hall_overview","input":{}}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":""}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"hallId\":"}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"A\"}"}}""",
        """{"type":"content_block_stop","index":2}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":42}}""",
        """{"type":"message_stop"}""",
    ];

    private static async IAsyncEnumerable<RawMessageStreamEvent> Stream(IEnumerable<string> events)
    {
        foreach (var json in events)
        {
            yield return JsonSerializer.Deserialize<RawMessageStreamEvent>(json)!;
            await Task.Yield();
        }
    }

    [Fact]
    public async Task Streamed_events_are_assembled_to_the_same_reply_as_a_whole_message()
    {
        var message = await AnthropicModelClient.AggregateAsync(Stream(s_events), null, CancellationToken.None);
        var reply = AnthropicModelClient.ToReply(message);

        Assert.Equal("tool_use", reply.StopReason); // ohne JSON-Anführungszeichen
        var thinking = Assert.IsType<ModelThinking>(reply.Blocks[0]);
        Assert.Equal("Erst prüfen", thinking.Thinking);
        Assert.Equal("sig-abc==", thinking.Signature);
        Assert.Equal("""{"headline":"RT-03 steht.","todo":[]}""", Assert.IsType<ModelText>(reply.Blocks[1]).Text);
        var use = Assert.IsType<ModelToolUse>(reply.Blocks[2]);
        Assert.Equal("toolu_1", use.Id);
        Assert.Equal("get_hall_overview", use.Name);
        Assert.Equal("A", use.Input.GetProperty("hallId").GetString());
        Assert.Equal(150, reply.Usage.InputTokens);
        Assert.Equal(42, reply.Usage.OutputTokens);
        Assert.Equal(11798, reply.Usage.CacheReadTokens);
        Assert.Equal(20, reply.Usage.CacheWriteTokens);
    }

    [Fact]
    public async Task Only_text_deltas_are_reported_not_thinking_or_tool_input()
    {
        var deltas = new List<string>();

        await AnthropicModelClient.AggregateAsync(Stream(s_events), deltas.Add, CancellationToken.None);

        Assert.Equal(["{\"headline\":\"RT-03 ", "steht.\",\"todo\":[]}"], deltas);
    }

    [Fact]
    public async Task Text_only_reply_with_end_turn_keeps_its_stop_reason()
    {
        string[] events =
        [
            s_events[0],
            s_events[5],
            s_events[6],
            s_events[8],
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":9}}""",
            s_events[15],
        ];

        var reply = AnthropicModelClient.ToReply(await AnthropicModelClient.AggregateAsync(Stream(events), null, CancellationToken.None));

        Assert.Equal("end_turn", reply.StopReason);
        Assert.Single(reply.Blocks);
    }
}
