using System.Text.Json;
using OPCAgentSample.Server.Infrastructure.Redis;
using OPCAgentSample.Server.MasterData;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Api;

/// <summary>
/// Favoriten eines Benutzers (<c>fav:{benutzer}</c>, eine JSON-Liste): lesen, ersetzen, Standard zurückholen. Solange der Benutzer
/// nichts geändert hat, fehlt der Schlüssel und es gelten die Standard-Favoriten der Rolle.
/// </summary>
public sealed class FavoritesService(IConnectionMultiplexer redis)
{
    public const int MaxQuestionLength = 500;

    private static readonly Lazy<FavoritesFile> s_defaults = new(() => SeedFiles.Read<FavoritesFile>("favorites.json"));

    public static IReadOnlyList<FavoriteDefault> DefaultsOf(string roleId) =>
        s_defaults.Value.ByRole.TryGetValue(roleId, out var list)
            ? [.. list.Select(f => new FavoriteDefault(f.ShortTitle, f.Question))]
            : [];

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    public async Task<FavoritesResponse> GetAsync(RoleContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = await redis.GetDatabase().StringGetAsync(RedisKeys.Favorites(context.User.Id));
        var rows = stored.HasValue
            ? JsonSerializer.Deserialize<List<Favorite>>(stored.ToString(), s_json)!
            : [.. DefaultsOf(context.Role.Id).Select(d => new Favorite { ShortTitle = d.ShortTitle, Question = d.Question, IsDefault = true })];
        return Build(context.Role.Id, rows.Select(f => new FavoriteView(f.ShortTitle, f.Question, f.IsDefault)));
    }

    /// <summary>Prüft die Eingabe und liefert die bereinigte Liste oder eine Fehlermeldung.</summary>
    public static (IReadOnlyList<FavoriteDefault>? Items, string? Error) Validate(FavoritesUpdate? update)
    {
        if (update?.Items is not { } items) return (null, "Die Liste 'items' fehlt.");
        if (items.Count > Favorite.MaxPerUser) return (null, $"Höchstens {Favorite.MaxPerUser} Favoriten sind erlaubt.");

        var cleaned = new List<FavoriteDefault>(items.Count);
        foreach (var item in items)
        {
            var shortTitle = item.ShortTitle?.Trim() ?? string.Empty;
            var question = item.Question?.Trim() ?? string.Empty;
            if (shortTitle.Length is 0 or > Favorite.MaxShortTitleLength)
                return (null, $"Der Kurztitel braucht 1 bis {Favorite.MaxShortTitleLength} Zeichen.");
            if (question.Length is 0 or > MaxQuestionLength)
                return (null, $"Die Frage braucht 1 bis {MaxQuestionLength} Zeichen.");
            cleaned.Add(new FavoriteDefault(shortTitle, question));
        }

        return (cleaned, null);
    }

    public async Task<FavoritesResponse> ReplaceAsync(
        RoleContext context, IReadOnlyList<FavoriteDefault> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var defaults = DefaultsOf(context.Role.Id);
        var rows = items.Select(item => new Favorite { ShortTitle = item.ShortTitle, Question = item.Question, IsDefault = defaults.Contains(item) }).ToList();
        await redis.GetDatabase().StringSetAsync(RedisKeys.Favorites(context.User.Id), JsonSerializer.Serialize(rows, s_json));
        return Build(context.Role.Id, rows.Select(f => new FavoriteView(f.ShortTitle, f.Question, f.IsDefault)));
    }

    public Task<FavoritesResponse> ResetAsync(RoleContext context, CancellationToken cancellationToken) =>
        ReplaceAsync(context, DefaultsOf(context.Role.Id), cancellationToken);

    private static FavoritesResponse Build(string roleId, IEnumerable<FavoriteView> items) =>
        new(Favorite.MaxPerUser, Favorite.MaxShortTitleLength, [.. items], DefaultsOf(roleId));
}
