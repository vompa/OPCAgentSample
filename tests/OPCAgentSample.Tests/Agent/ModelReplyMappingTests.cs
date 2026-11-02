using System.Text.Json;
using Anthropic.Models.Messages;
using OPCAgentSample.Server.Agent;

namespace OPCAgentSample.Tests.Agent;

/// <summary>
/// Abbildung einer echten SDK-Antwort auf das eigene Modell. Der nachgebaute Client der übrigen Tests umgeht diese Stelle;
/// im ersten Live-Test kam der Stopp-Grund hier mit JSON-Anführungszeichen an und die Tool-Runde wurde nicht erkannt.
/// </summary>
public sealed class ModelReplyMappingTests
{
    private const string ToolUseResponse = """
        {
          "id": "msg_test",
          "type": "message",
          "role": "assistant",
          "model": "claude-haiku-5-5",
          "content": [
            { "type": "text", "text": "Ich schaue nach." },
            { "type": "tool_use", "id": "toolu_1", "name": "get_hall_overview", "input": { "hallId": "A" } }
          ],
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": { "input_tokens": 150, "output_tokens": 40, "cache_read_input_tokens": 11798, "cache_creation_input_tokens": 0 }
        }
        """;

    [Fact]
    public void Stop_reason_is_the_plain_api_value_and_tool_use_blocks_are_mapped()
    {
        var reply = AnthropicModelClient.ToReply(Parse(ToolUseResponse));

        Assert.Equal("tool_use", reply.RawStopReason);
        Assert.Equal(ModelStop.ToolCalls, reply.Stop);
        var use = Assert.Single(reply.Blocks.OfType<ModelToolUse>());
        Assert.Equal("toolu_1", use.Id);
        Assert.Equal("get_hall_overview", use.Name);
        Assert.Equal("A", use.Input.GetProperty("hallId").GetString());
        Assert.Equal(11798, reply.Usage.CacheReadTokens);
    }

    [Theory]
    [InlineData("end_turn", ModelStop.Done)]
    [InlineData("stop_sequence", ModelStop.Done)]
    [InlineData("tool_use", ModelStop.ToolCalls)]
    [InlineData("max_tokens", ModelStop.MaxTokens)]
    [InlineData("refusal", ModelStop.Refused)]
    [InlineData("pause_turn", ModelStop.Other)]
    [InlineData("model_context_window_exceeded", ModelStop.Other)]
    public void Raw_stop_reason_maps_to_the_stop_enum_and_arrives_without_quotes(string stopReason, ModelStop expected)
    {
        var json = ToolUseResponse.Replace("\"stop_reason\": \"tool_use\"", $"\"stop_reason\": \"{stopReason}\"", StringComparison.Ordinal);

        var reply = AnthropicModelClient.ToReply(Parse(json));

        Assert.Equal(expected, reply.Stop);
        Assert.Equal(stopReason, reply.RawStopReason);
    }

    [Fact]
    public void A_missing_stop_reason_maps_to_other_with_a_null_raw_value()
    {
        var json = ToolUseResponse.Replace("\"stop_reason\": \"tool_use\"", "\"stop_reason\": null", StringComparison.Ordinal);

        var reply = AnthropicModelClient.ToReply(Parse(json));

        Assert.Equal(ModelStop.Other, reply.Stop);
        Assert.Null(reply.RawStopReason);
    }

    private static Message Parse(string json) =>
        Message.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!);
}
