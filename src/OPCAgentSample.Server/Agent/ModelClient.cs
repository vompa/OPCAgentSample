using System.Text.Json;

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

/// <summary>Bisheriger Verlauf einer Frage in SDK-unabhängiger Form. Die Anfrage entsteht daraus jedes Mal neu (siehe <see cref="ChatClientModelClient"/>).</summary>
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
