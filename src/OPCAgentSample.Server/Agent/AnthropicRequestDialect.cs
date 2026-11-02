using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace OPCAgentSample.Server.Agent;

/// <summary>
/// Anthropic-Dialekt des <see cref="ChatClientModelClient"/>. Blöcke, die der Adapter sonst verändern würde (Assistant-Text mit Leerraum am
/// Ende, Redacted Thinking, <c>is_error</c>, Cache-Marker), gehen als fertiger <see cref="ContentBlockParam"/> über <c>RawRepresentation</c>
/// unverändert hinaus. Modell, Token-Grenze und Effort setzt <see cref="CreateRawOptions"/> selbst: Mit <c>RawRepresentationFactory</c>
/// ignoriert der Adapter <see cref="ChatOptions.MaxOutputTokens"/>, und <see cref="ChatOptions.Reasoning"/> würde zusätzlich <c>thinking</c> senden.
/// </summary>
public sealed class AnthropicRequestDialect(IOptions<ClaudeOptions> options) : IModelRequestDialect
{
    private static readonly CacheControlEphemeral Ephemeral = new();

    private readonly ClaudeOptions _options = options.Value;

    public AIContent CachedSystemText(string text) =>
        new TextContent(text) { RawRepresentation = new TextBlockParam { Text = text, CacheControl = Ephemeral } };

    public AIContent AssistantText(string text) =>
        new TextContent(text) { RawRepresentation = new ContentBlockParam(new TextBlockParam { Text = text }) };

    public AIContent RedactedThinking(string data) =>
        new TextReasoningContent(string.Empty)
        {
            ProtectedData = data,
            RawRepresentation = new ContentBlockParam(new RedactedThinkingBlockParam { Data = data }),
        };

    public AIContent ToolResult(ToolResultItem item, bool cacheBreakpoint)
    {
        ArgumentNullException.ThrowIfNull(item);

        // cache_control nur setzen, wenn es gebraucht wird: ein Wert null würde als explizites null gesendet
        ToolResultBlockParam block = cacheBreakpoint
            ? new() { ToolUseID = item.ToolUseId, Content = item.Text, IsError = item.IsError, CacheControl = Ephemeral }
            : new() { ToolUseID = item.ToolUseId, Content = item.Text, IsError = item.IsError };
        return new FunctionResultContent(item.ToolUseId, item.Text) { RawRepresentation = new ContentBlockParam(block) };
    }

    public object CreateRawOptions(AgentModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            Messages = [],
            OutputConfig = new OutputConfig { Effort = ToEffort(ClaudeOptions.ParseEffort(_options.Effort) ?? AgentEffort.Medium) },
            Metadata = new Metadata { UserID = request.OwnerHash },
        };
    }

    public bool IsRedactedThinking(TextReasoningContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.RawRepresentation is RedactedThinkingBlock;
    }

    public string? ReadRawStopReason(ChatResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Raw() liefert den Wert der API (z. B. tool_use); ToString() enthielte die JSON-Anführungszeichen
        return update.RawRepresentation is RawMessageStreamEvent streamEvent && streamEvent.TryPickDelta(out RawMessageDeltaEvent? delta)
            ? delta.Delta.StopReason?.Raw()
            : null;
    }

    public ModelStop ToStop(string? rawStopReason) => rawStopReason switch
    {
        "end_turn" or "stop_sequence" => ModelStop.Done,
        "tool_use" => ModelStop.ToolCalls,
        "max_tokens" => ModelStop.MaxTokens,
        "refusal" => ModelStop.Refused,
        _ => ModelStop.Other,
    };

    private static Effort ToEffort(AgentEffort effort) => effort switch
    {
        AgentEffort.Low => Effort.Low,
        AgentEffort.High => Effort.High,
        AgentEffort.Max => Effort.Max,
        _ => Effort.Medium,
    };
}
