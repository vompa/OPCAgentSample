using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace OPCAgentSample.Server.Agent;

/// <summary>Ein Inhaltsblock der Modellantwort, unabhängig vom SDK (Tests skripten diese Typen).</summary>
public abstract record ModelBlock;

public sealed record ModelText(string Text) : ModelBlock;

/// <summary>Denkblock: muss mit Signatur unverändert in die nächste Anfrage zurück.</summary>
public sealed record ModelThinking(string Thinking, string Signature) : ModelBlock;

public sealed record ModelRedactedThinking(string Data) : ModelBlock;

/// <summary><paramref name="Input"/> ist immer ein JSON-Objekt.</summary>
public sealed record ModelToolUse(string Id, string Name, JsonElement Input) : ModelBlock;

public sealed record ModelUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens);

/// <summary>Anbieterneutraler Stopp-Grund einer Modellantwort.</summary>
public enum ModelStop
{
    /// <summary>Die Antwort ist vollständig (<c>end_turn</c>, <c>stop_sequence</c>).</summary>
    Done,

    /// <summary>Das Modell will Tools aufrufen.</summary>
    ToolCalls,

    /// <summary>Die Antwort wurde durch die Längenbegrenzung abgeschnitten.</summary>
    MaxTokens,

    /// <summary>Das Modell lehnt die Antwort ab.</summary>
    Refused,

    /// <summary>Alles andere, auch ein fehlender Grund.</summary>
    Other,
}

/// <param name="Stop">Der Stopp-Grund als Enum; die Engine wertet nur ihn aus.</param>
/// <param name="RawStopReason">Der Wert des Anbieters (<c>end_turn</c>, <c>pause_turn</c> …) für Logs und Span-Tags; <see langword="null"/>, wenn es keinen gab.</param>
public sealed record ModelReply(ModelStop Stop, string? RawStopReason, IReadOnlyList<ModelBlock> Blocks, ModelUsage Usage);

public enum ModelFailureKind
{
    RateLimited,
    ServerError,
    Misconfigured,
    Connection,
    Timeout,
    Other,
}

/// <summary>Fehler eines Modellaufrufs, gruppiert nach der Art; enthält nie Schlüssel oder Antwortinhalte.</summary>
public sealed class ModelCallException(ModelFailureKind kind, string description, Exception? inner = null)
    : Exception(description, inner)
{
    public ModelFailureKind Kind { get; } = kind;
}

/// <summary>Bisheriger Verlauf einer Frage in SDK-unabhängiger Form. Die Anfrage entsteht daraus jedes Mal neu (siehe <see cref="AnthropicModelClient"/>).</summary>
public abstract record AgentTurn;

public sealed record UserQuestionTurn(string Text) : AgentTurn;

public sealed record AssistantTurn(IReadOnlyList<ModelBlock> Blocks) : AgentTurn;

/// <summary>Alle Ergebnisse einer Runde stehen in einer einzigen User-Nachricht, in der Reihenfolge der Tool-Aufrufe.</summary>
public sealed record ToolResultsTurn(IReadOnlyList<ToolResultItem> Results) : AgentTurn;

public sealed record ToolResultItem(string ToolUseId, string Text, bool IsError);

/// <summary>Alles, was ein Modellaufruf braucht, ohne Bezug zu einem Anbieter: Tools, Verlauf, Besitzer-Hash und ob Tools erlaubt sind.</summary>
public sealed record AgentModelRequest(IReadOnlyList<AgentTool> Tools, IReadOnlyList<AgentTurn> Turns, string OwnerHash, bool AllowTools);

/// <summary>Schnittstelle um den Modellaufruf: Die Tests ersetzen sie durch einen nachgebauten Client mit skriptierten Antworten.</summary>
public interface IAgentModelClient
{
    /// <param name="onTextDelta">
    /// Wird für jedes Stück Antworttext (nur Text, nicht das Denken) gemeldet, sobald es eintrifft, noch vor dem Ende des Aufrufs.
    /// Der Rückgabewert enthält danach dieselbe vollständige Antwort wie bei einem Aufruf ohne Streaming.
    /// </param>
    Task<ModelReply> CreateAsync(AgentModelRequest request, Action<string>? onTextDelta, CancellationToken cancellationToken);
}

/// <summary>
/// Der echte Aufruf über das Anthropic-C#-SDK, streamend: Die Text-Deltas gehen sofort an den Aufrufer (Überschrift der Karte vorab),
/// die Antwort selbst wird aus den Ereignissen wieder vollständig zusammengesetzt und erst dann geprüft. Zwischen den Runden streamt
/// der Server die Tool-Aufrufe. Das SDK wiederholt 429 und 5xx selbst.
/// </summary>
public sealed class AnthropicModelClient : IAgentModelClient, IDisposable
{
    private readonly ClaudeOptions _options;
    private readonly Lazy<AnthropicClient> _client;

    public AnthropicModelClient(IOptions<ClaudeOptions> options)
    {
        _options = options.Value;
        var apiKey = _options.ApiKey;
        _client = new Lazy<AnthropicClient>(() => new AnthropicClient { ApiKey = apiKey });
    }

    public async Task<ModelReply> CreateAsync(AgentModelRequest request, Action<string>? onTextDelta, CancellationToken cancellationToken)
    {
        try
        {
            var events = _client.Value.Messages.CreateStreaming(Build(_options, request), cancellationToken);
            return ToReply(await AggregateAsync(events, onTextDelta, cancellationToken));
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new ModelCallException(ModelFailureKind.RateLimited, "Ratenlimit von Anthropic erreicht.", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new ModelCallException(ModelFailureKind.ServerError, "Anthropic meldet einen Serverfehler oder ist überlastet.", ex);
        }
        catch (Exception ex) when (ex is AnthropicUnauthorizedException or AnthropicForbiddenException or AnthropicNotFoundException
                                       or AnthropicBadRequestException or AnthropicUnprocessableEntityException)
        {
            throw new ModelCallException(ModelFailureKind.Misconfigured, $"Anthropic lehnt die Anfrage ab ({ex.GetType().Name}).", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new ModelCallException(ModelFailureKind.Connection, "Keine Verbindung zu Anthropic.", ex);
        }
        catch (AnthropicException ex)
        {
            throw new ModelCallException(ModelFailureKind.Other, $"Unerwarteter Fehler beim Aufruf von Anthropic ({ex.GetType().Name}).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelCallException(ModelFailureKind.Connection, "Keine Verbindung zu Anthropic.", ex);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }

    /// <summary>
    /// Setzt die Ereignisse des Streams mit dem Aggregator des SDK zur vollständigen <see cref="Message"/> zusammen (Denkblöcke mit
    /// Signatur, Tool-Aufrufe mit vollständigem Input-JSON, Nutzung, Stopp-Grund) und meldet nebenbei jedes Text-Delta.
    /// </summary>
    internal static async Task<Message> AggregateAsync(
        IAsyncEnumerable<RawMessageStreamEvent> events, Action<string>? onTextDelta, CancellationToken cancellationToken)
    {
        var aggregator = new MessageContentAggregator();
        await foreach (var streamEvent in events.CollectAsync(aggregator).WithCancellation(cancellationToken))
        {
            if (onTextDelta is not null
                && streamEvent.TryPickContentBlockDelta(out RawContentBlockDeltaEvent? blockDelta)
                && blockDelta.Delta.TryPickText(out TextDelta? text))
            {
                onTextDelta(text.Text);
            }
        }

        return aggregator.Message();
    }

    private static ModelStop ToStop(string? rawStopReason) => rawStopReason switch
    {
        "end_turn" or "stop_sequence" => ModelStop.Done,
        "tool_use" => ModelStop.ToolCalls,
        "max_tokens" => ModelStop.MaxTokens,
        "refusal" => ModelStop.Refused,
        _ => ModelStop.Other,
    };

    internal static ModelReply ToReply(Message message)
    {
        var blocks = new List<ModelBlock>();
        foreach (var block in message.Content)
        {
            if (block.TryPickText(out TextBlock? text)) blocks.Add(new ModelText(text.Text));
            else if (block.TryPickThinking(out ThinkingBlock? thinking)) blocks.Add(new ModelThinking(thinking.Thinking, thinking.Signature));
            else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted)) blocks.Add(new ModelRedactedThinking(redacted.Data));
            else if (block.TryPickToolUse(out ToolUseBlock? use)) blocks.Add(new ModelToolUse(use.ID, use.Name, JsonSerializer.SerializeToElement(use.Input)));
        }

        var usage = message.Usage;
        // Raw() liefert den Wert der API (z. B. tool_use); ToString() enthielte die JSON-Anführungszeichen
        var rawStop = message.StopReason?.Raw();
        return new ModelReply(
            ToStop(rawStop),
            rawStop,
            blocks,
            new ModelUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0));
    }

    private static readonly CacheControlEphemeral Ephemeral = new();

    /// <summary>
    /// Baut die Anfrage an Claude. Reihenfolge nach Stabilität (Prompt-Caching ist ein Präfix-Vergleich): zuerst die Tools (nach Name
    /// sortiert), dann der feste Systemprompt, dahinter der erste Breakpoint; Rolle, Demo-Uhr und Frage stehen erst in der User-Nachricht.
    /// In der Tool-Schleife markiert ein zweiter Breakpoint das letzte Tool-Ergebnis, damit jede Runde den Verlauf davor aus dem Cache liest.
    /// </summary>
    internal static MessageCreateParams Build(ClaudeOptions options, AgentModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        return new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = options.MaxTokens,
            Tools = request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => new ToolUnion(ToTool(t))).ToList(),
            System = new List<TextBlockParam> { new() { Text = AgentPrompt.SystemPrompt, CacheControl = Ephemeral } },
            Messages = ToMessages(request.Turns),
            ToolChoice = request.AllowTools ? new ToolChoiceAuto() : new ToolChoiceNone(),
            OutputConfig = new OutputConfig
            {
                Effort = ToEffort(ClaudeOptions.ParseEffort(options.Effort) ?? AgentEffort.Medium),
                Format = new JsonOutputFormat { Schema = AnswerCardFormat.Schema().ToDictionary(p => p.Key, p => p.Value) },
            },
            Metadata = new Metadata { UserID = request.OwnerHash },
        };
    }

    internal static Tool ToTool(AgentTool tool)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var required = new List<string>();
        if (tool.InputSchema.ValueKind == JsonValueKind.Object)
        {
            if (tool.InputSchema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in props.EnumerateObject()) properties[property.Name] = property.Value.Clone();
            }

            if (tool.InputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                required.AddRange(req.EnumerateArray().Select(r => r.GetString()).OfType<string>());
        }

        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static Effort ToEffort(AgentEffort effort) => effort switch
    {
        AgentEffort.Low => Effort.Low,
        AgentEffort.High => Effort.High,
        AgentEffort.Max => Effort.Max,
        _ => Effort.Medium,
    };

    private static List<MessageParam> ToMessages(IReadOnlyList<AgentTurn> turns)
    {
        var lastToolResults = turns.ToList().FindLastIndex(t => t is ToolResultsTurn);
        var messages = new List<MessageParam>();
        for (var i = 0; i < turns.Count; i++)
        {
            switch (turns[i])
            {
                case UserQuestionTurn question:
                    messages.Add(new MessageParam { Role = Role.User, Content = question.Text });
                    break;
                case AssistantTurn assistant:
                    messages.Add(new MessageParam { Role = Role.Assistant, Content = assistant.Blocks.Select(ToBlock).ToList() });
                    break;
                case ToolResultsTurn results:
                    messages.Add(new MessageParam { Role = Role.User, Content = ToResultBlocks(results, markLast: i == lastToolResults) });
                    break;
            }
        }

        return messages;
    }

    private static List<ContentBlockParam> ToResultBlocks(ToolResultsTurn turn, bool markLast)
    {
        var blocks = new List<ContentBlockParam>();
        for (var i = 0; i < turn.Results.Count; i++)
        {
            var item = turn.Results[i];
            var mark = markLast && i == turn.Results.Count - 1;
            ToolResultBlockParam block = mark
                ? new() { ToolUseID = item.ToolUseId, Content = item.Text, IsError = item.IsError, CacheControl = Ephemeral }
                : new() { ToolUseID = item.ToolUseId, Content = item.Text, IsError = item.IsError };
            blocks.Add(block);
        }

        return blocks;
    }

    private static ContentBlockParam ToBlock(ModelBlock block) => block switch
    {
        ModelText text => new TextBlockParam { Text = text.Text },
        ModelThinking thinking => new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature },
        ModelRedactedThinking redacted => new RedactedThinkingBlockParam { Data = redacted.Data },
        ModelToolUse use => new ToolUseBlockParam { ID = use.Id, Name = use.Name, Input = ToInput(use.Input) },
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Unbekannter Block."),
    };

    private static Dictionary<string, JsonElement> ToInput(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
            : [];
}
