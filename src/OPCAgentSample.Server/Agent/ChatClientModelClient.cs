using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace OPCAgentSample.Server.Agent;

/// <summary>
/// Alles, was der <see cref="ChatClientModelClient"/> über einen Anbieter wissen muss, ohne dessen Typen zu kennen: Cache-Marker,
/// unverändert zurückgehende Blöcke, Anbieter-Optionen und der Rohwert des Stopp-Grundes.
/// </summary>
public interface IModelRequestDialect
{
    /// <summary>Systemtext mit Cache-Breakpoint.</summary>
    AIContent CachedSystemText(string text);

    /// <summary>Antworttext des Modells, der unverändert (auch mit Leerraum am Ende) zurückgeht.</summary>
    AIContent AssistantText(string text);

    /// <summary>Redacted Thinking mit denselben Daten.</summary>
    AIContent RedactedThinking(string data);

    /// <summary>Ein Tool-Ergebnis; <paramref name="cacheBreakpoint"/> setzt den Breakpoint (letztes Ergebnis der letzten Runde).</summary>
    AIContent ToolResult(ToolResultItem item, bool cacheBreakpoint);

    /// <summary>Die anbietereigenen Anfrageparameter für <see cref="ChatOptions.RawRepresentationFactory"/>.</summary>
    object CreateRawOptions(AgentModelRequest request);

    /// <summary>Ob ein Denkblock der Antwort ein Redacted Thinking war.</summary>
    bool IsRedactedThinking(TextReasoningContent content);

    /// <summary>Der Rohwert des Stopp-Grundes aus einem Stream-Update, sonst <see langword="null"/>.</summary>
    string? ReadRawStopReason(ChatResponseUpdate update);

    /// <summary>Ordnet den Rohwert (oder dessen Fehlen) einem <see cref="ModelStop"/> zu.</summary>
    ModelStop ToStop(string? rawStopReason);
}

/// <summary>Übersetzt Fehler eines Anbieters in <see cref="ModelCallException"/>.</summary>
public interface IModelErrorMapper
{
    /// <returns>Die übersetzte Ausnahme, oder <see langword="null"/>, wenn der Fehler nicht zum Anbieter gehört (er wird dann unverändert weitergereicht).</returns>
    ModelCallException? Map(Exception exception);
}

/// <summary>
/// Modellaufruf über einen <see cref="IChatClient"/>; kennt keinen Anbieter, nur Dialekt und Fehlerabbildung. Streamt: Text-Deltas gehen
/// sofort an den Aufrufer, die Antwort wird aus den Updates wieder vollständig zusammengesetzt.
/// </summary>
public sealed class ChatClientModelClient(IChatClient chatClient, IModelRequestDialect dialect, IModelErrorMapper errorMapper) : IAgentModelClient
{
    private const string CacheCreationCount = "CacheCreationInputTokens";

    private static readonly Lazy<JsonElement> CardSchema = new(() => JsonSerializer.SerializeToElement(AnswerCardFormat.Schema()));

    public async Task<ModelReply> CreateAsync(AgentModelRequest request, Action<string>? onTextDelta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var reply = new ReplyBuilder(dialect, onTextDelta);
            await foreach (var update in chatClient.GetStreamingResponseAsync(ToMessages(request), ToOptions(request), cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                reply.Add(update);
            }

            return reply.Build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var mapped = errorMapper.Map(ex);
            if (mapped is null) throw;
            throw mapped;
        }
    }

    private ChatOptions ToOptions(AgentModelRequest request) => new()
    {
        Tools = request.Tools.OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (AITool)AIFunctionFactory.CreateDeclaration(t.Name, t.Description, t.InputSchema)).ToList(),
        ToolMode = request.AllowTools ? ChatToolMode.Auto : ChatToolMode.None,
        AllowMultipleToolCalls = true,
        ResponseFormat = ChatResponseFormat.ForJsonSchema(CardSchema.Value),
        RawRepresentationFactory = _ => dialect.CreateRawOptions(request),
    };

    /// <summary>Systemprompt zuerst; alle Ergebnisse einer Runde stehen in einer einzigen User-Nachricht, der Marker nur am letzten Ergebnis der letzten Runde.</summary>
    private List<ChatMessage> ToMessages(AgentModelRequest request)
    {
        var lastToolResults = request.Turns.ToList().FindLastIndex(t => t is ToolResultsTurn);
        var messages = new List<ChatMessage> { new(ChatRole.System, [dialect.CachedSystemText(AgentPrompt.SystemPrompt)]) };
        for (var i = 0; i < request.Turns.Count; i++)
        {
            switch (request.Turns[i])
            {
                case UserQuestionTurn question:
                    messages.Add(new ChatMessage(ChatRole.User, question.Text));
                    break;
                case AssistantTurn assistant:
                    messages.Add(new ChatMessage(ChatRole.Assistant, assistant.Blocks.Select(ToContent).ToList()));
                    break;
                case ToolResultsTurn results:
                    var markLast = i == lastToolResults;
                    messages.Add(new ChatMessage(
                        ChatRole.User,
                        results.Results.Select((item, index) => dialect.ToolResult(item, markLast && index == results.Results.Count - 1)).ToList()));
                    break;
            }
        }

        return messages;
    }

    private AIContent ToContent(ModelBlock block) => block switch
    {
        ModelText text => dialect.AssistantText(text.Text),
        ModelThinking thinking => new TextReasoningContent(thinking.Thinking) { ProtectedData = thinking.Signature },
        ModelRedactedThinking redacted => dialect.RedactedThinking(redacted.Data),
        ModelToolUse use => new FunctionCallContent(use.Id, use.Name, ToArguments(use.Input)),
        _ => throw new ArgumentOutOfRangeException(nameof(block), block, "Unbekannter Block."),
    };

    private static Dictionary<string, object?> ToArguments(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object
            ? input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal)
            : [];

    /// <summary>Setzt die Updates in Reihenfolge zu <see cref="ModelBlock"/>s zusammen; benachbarte Text- bzw. Denkstücke werden ein Block.</summary>
    private sealed class ReplyBuilder(IModelRequestDialect dialect, Action<string>? onTextDelta)
    {
        private readonly List<ModelBlock> _blocks = [];
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private string _signature = string.Empty;
        private bool _hasThinking;
        private string? _rawStopReason;
        private UsageDetails? _usage;

        public void Add(ChatResponseUpdate update)
        {
            _rawStopReason = dialect.ReadRawStopReason(update) ?? _rawStopReason;
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent { Text.Length: > 0 } text:
                        FlushThinking();
                        _text.Append(text.Text);
                        onTextDelta?.Invoke(text.Text);
                        break;
                    case TextReasoningContent reasoning when dialect.IsRedactedThinking(reasoning):
                        Flush();
                        _blocks.Add(new ModelRedactedThinking(reasoning.ProtectedData ?? string.Empty));
                        break;
                    case TextReasoningContent reasoning:
                        FlushText();
                        _hasThinking = true;
                        _thinking.Append(reasoning.Text);
                        if (!string.IsNullOrEmpty(reasoning.ProtectedData)) _signature = reasoning.ProtectedData;
                        break;
                    case FunctionCallContent call:
                        Flush();
                        _blocks.Add(new ModelToolUse(call.CallId, call.Name, ToInput(call)));
                        break;
                    case UsageContent usage:
                        _usage = usage.Details;
                        break;
                }
            }
        }

        public ModelReply Build()
        {
            Flush();
            return new ModelReply(dialect.ToStop(_rawStopReason), _rawStopReason, _blocks, ToUsage(_usage));
        }

        private static JsonElement ToInput(FunctionCallContent call) =>
            call.Exception is not null
                ? JsonSerializer.SerializeToElement<object?>(null) // kein Objekt: die Engine antwortet mit ihrem Fehlertext für ungültiges JSON
                : JsonSerializer.SerializeToElement(call.Arguments ?? new Dictionary<string, object?>());

        /// <summary>Der Adapter meldet Input einschließlich Cache-Lesen und -Schreiben; hier zählt nur der ungecachte Input, ohne Doppelzählung.</summary>
        private static ModelUsage ToUsage(UsageDetails? usage)
        {
            if (usage is null) return new ModelUsage(0, 0, 0, 0);
            var cacheRead = usage.CachedInputTokenCount ?? 0;
            var cacheWrite = usage.AdditionalCounts is not null && usage.AdditionalCounts.TryGetValue(CacheCreationCount, out var written) ? written : 0;
            var input = Math.Max(0, (usage.InputTokenCount ?? 0) - cacheRead - cacheWrite);
            return new ModelUsage(input, usage.OutputTokenCount ?? 0, cacheRead, cacheWrite);
        }

        private void Flush()
        {
            FlushText();
            FlushThinking();
        }

        private void FlushText()
        {
            if (_text.Length == 0) return;
            _blocks.Add(new ModelText(_text.ToString()));
            _text.Clear();
        }

        private void FlushThinking()
        {
            if (!_hasThinking) return;
            _blocks.Add(new ModelThinking(_thinking.ToString(), _signature));
            _thinking.Clear();
            _signature = string.Empty;
            _hasThinking = false;
        }
    }
}
