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

/// <param name="StopReason"><c>end_turn</c>, <c>tool_use</c>, <c>max_tokens</c>, <c>refusal</c> …</param>
public sealed record ModelReply(string? StopReason, IReadOnlyList<ModelBlock> Blocks, ModelUsage Usage);

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

/// <summary>Schnittstelle um den SDK-Aufruf: Die Tests ersetzen sie durch einen nachgebauten Client mit skriptierten Antworten.</summary>
public interface IClaudeModelClient
{
    /// <param name="onTextDelta">
    /// Wird für jedes Stück Antworttext (nur Text, nicht das Denken) gemeldet, sobald es eintrifft, noch vor dem Ende des Aufrufs.
    /// Der Rückgabewert enthält danach dieselbe vollständige Antwort wie bei einem Aufruf ohne Streaming.
    /// </param>
    Task<ModelReply> CreateAsync(MessageCreateParams request, Action<string>? onTextDelta, CancellationToken cancellationToken);
}

/// <summary>
/// Der echte Aufruf über das Anthropic-C#-SDK, streamend: Die Text-Deltas gehen sofort an den Aufrufer (Überschrift der Karte vorab),
/// die Antwort selbst wird aus den Ereignissen wieder vollständig zusammengesetzt und erst dann geprüft. Zwischen den Runden streamt
/// der Server die Tool-Aufrufe. Das SDK wiederholt 429 und 5xx selbst.
/// </summary>
public sealed class AnthropicModelClient : IClaudeModelClient, IDisposable
{
    private readonly Lazy<AnthropicClient> _client;

    public AnthropicModelClient(IOptions<ClaudeOptions> options)
    {
        var apiKey = options.Value.ApiKey;
        _client = new Lazy<AnthropicClient>(() => new AnthropicClient { ApiKey = apiKey });
    }

    public async Task<ModelReply> CreateAsync(MessageCreateParams request, Action<string>? onTextDelta, CancellationToken cancellationToken)
    {
        try
        {
            var events = _client.Value.Messages.CreateStreaming(request, cancellationToken);
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
        return new ModelReply(
            // Raw() liefert den Wert der API (z. B. tool_use); ToString() enthielte die JSON-Anführungszeichen
            message.StopReason?.Raw(),
            blocks,
            new ModelUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0));
    }
}
