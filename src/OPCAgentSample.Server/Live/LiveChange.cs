using System.Text.Json;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Live;

/// <summary>Was sich geändert hat; der Live-Strom berechnet daraus die Ereignisse <c>station</c>, <c>alarm</c>, <c>feed</c>, <c>inbox</c>.</summary>
[Flags]
public enum LiveChangeKind
{
    None = 0,
    Station = 1,
    Alarm = 2,
    Feed = 4,
    Inbox = 8,
    All = Station | Alarm | Feed | Inbox,
}

/// <summary>Meldet Änderungen an alle Server-Instanzen (Redis-Pub/Sub); der Live-Pfad ruft es nach erfolgreichem Schreiben auf.</summary>
public interface ILiveNotifier
{
    /// <param name="kinds">Geänderte Bereiche.</param>
    /// <param name="ownerKey">Nur der Postfach-Zähler dieses Besitzers; <see langword="null"/> = alle.</param>
    /// <param name="cancellationToken">Abbruch.</param>
    Task NotifyAsync(LiveChangeKind kinds, string? ownerKey = null, CancellationToken cancellationToken = default);
}

/// <summary>Ohne Live-Strom (z. B. reine Aufnahme-Tests): meldet nichts.</summary>
public sealed class NullLiveNotifier : ILiveNotifier
{
    public static NullLiveNotifier Instance { get; } = new();

    public Task NotifyAsync(LiveChangeKind kinds, string? ownerKey = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Kanal und Nachrichtenformat des Pub/Sub: <c>live:A</c>, Nutzlast <c>{"kinds":"Station, Feed","owner":null}</c>.</summary>
public static class LiveChannel
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public static string Name(string hallId = PlantLayout.HallA) => $"live:{hallId}";

    public static string Serialize(LiveChangeKind kinds, string? ownerKey) =>
        JsonSerializer.Serialize(new Message(kinds.ToString(), ownerKey), s_json);

    /// <summary>Liest eine Nachricht; unlesbare Nachrichten ergeben <see langword="false"/> (der Strom bleibt davon unberührt).</summary>
    public static bool TryParse(string? text, out LiveChangeKind kinds, out string? ownerKey)
    {
        kinds = LiveChangeKind.None;
        ownerKey = null;
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            var message = JsonSerializer.Deserialize<Message>(text, s_json);
            if (message is null || !Enum.TryParse(message.Kinds, ignoreCase: true, out kinds)) return false;

            ownerKey = message.Owner;
            return kinds != LiveChangeKind.None;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Message(string Kinds, string? Owner);
}

/// <summary>
/// Veröffentlicht über Redis-Pub/Sub, damit jede Server-Instanz ihre Clients bedient. Ist Redis nicht erreichbar, bedient die
/// Instanz wenigstens ihre eigenen Verbindungen (Redis bleibt verwerfbar, der Strom fällt nicht aus).
/// </summary>
public sealed class RedisLiveNotifier(RedisAccess redis, LiveHub hub) : ILiveNotifier
{
    public async Task NotifyAsync(LiveChangeKind kinds, string? ownerKey = null, CancellationToken cancellationToken = default)
    {
        if (kinds == LiveChangeKind.None) return;

        var message = LiveChannel.Serialize(kinds, ownerKey);
        var published = await redis.TryAsync(async database =>
        {
            _ = await database.PublishAsync(RedisChannel.Literal(LiveChannel.Name()), message);
            return true;
        }, false, "live publish");
        if (!published) hub.Dispatch(kinds, ownerKey);
    }
}
