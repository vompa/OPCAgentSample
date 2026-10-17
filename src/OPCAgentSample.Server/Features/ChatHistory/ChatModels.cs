using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OPCAgentSample.Server.Features.ChatHistory;

public enum ChatOwnerKind
{
    User,
    AccessCode,
    BrowserToken,
}

/// <summary>
/// Besitzer eines Verlaufs: lokal der Benutzer, in der Live-Demo der Zugangscode oder das Browser-Token.
/// Zugangscodes und Tokens werden nie im Klartext gespeichert, sondern als gekürzter SHA-256-Wert.
/// Der Schlüssel (<c>art:kennung</c>) trennt Besucher strikt und ist zugleich Teil der Redis-Schlüssel.
/// </summary>
public sealed partial record ChatOwner
{
    private ChatOwner(ChatOwnerKind kind, string id)
    {
        Kind = kind;
        Id = id;
    }

    public ChatOwnerKind Kind { get; }

    public string Id { get; }

    public string Key => $"{Prefix(Kind)}:{Id}";

    public static ChatOwner ForUser(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return UserIdPattern().IsMatch(userId)
            ? new ChatOwner(ChatOwnerKind.User, userId)
            : throw new ArgumentException("Ungültige Benutzerkennung.", nameof(userId));
    }

    public static ChatOwner ForAccessCode(string accessCode) => new(ChatOwnerKind.AccessCode, Hash(accessCode));

    public static ChatOwner ForBrowserToken(string token) => new(ChatOwnerKind.BrowserToken, Hash(token));

    private static string Hash(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..32];
    }

    private static string Prefix(ChatOwnerKind kind) => kind switch
    {
        ChatOwnerKind.User => "user",
        ChatOwnerKind.AccessCode => "code",
        ChatOwnerKind.BrowserToken => "browser",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex UserIdPattern();
}

public sealed record ChatSessionSummary(
    Guid Id, string Role, DateTimeOffset StartedAtUtc, DateTimeOffset LastActivityUtc, string Title, int TurnCount);

/// <summary>Eine gespeicherte Frage mit Antwortkarte, Tool-Aufrufen und Datenstand.</summary>
public sealed record ChatTurnRecord(
    int Sequence,
    DateTimeOffset AskedAtUtc,
    string Question,
    JsonElement AnswerCard,
    JsonElement ToolCalls,
    DateTimeOffset DataAsOfUtc,
    int InputTokens,
    int OutputTokens);

/// <summary>Neue Frage samt Ergebnis; <see cref="AnswerCard"/> ist ein JSON-Objekt, <see cref="ToolCalls"/> ein JSON-Array.</summary>
public sealed record NewChatTurn(
    string Question,
    JsonElement AnswerCard,
    JsonElement ToolCalls,
    DateTimeOffset DataAsOfUtc,
    int InputTokens = 0,
    int OutputTokens = 0,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0,
    int DurationMs = 0,
    bool Repeat = false);

/// <summary>Sitzung mit allen Fragen: das fertige Paket, das die Oberfläche beim Öffnen lädt.</summary>
public sealed record ChatSessionPackage(ChatSessionSummary Session, IReadOnlyList<ChatTurnRecord> Turns);

/// <summary>Die Sitzung existiert nicht oder gehört einem anderen Besitzer (beides wird gleich behandelt).</summary>
public sealed class ChatSessionNotFoundException(Guid sessionId)
    : KeyNotFoundException($"Sitzung {sessionId} nicht gefunden.");
