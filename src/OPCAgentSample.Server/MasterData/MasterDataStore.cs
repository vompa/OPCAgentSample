using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.MasterData;

/// <summary>
/// Stammdaten in Redis (<c>md:{typ}</c>, je ein Hash Id → JSON). Beim ersten Zugriff nach dem Start (oder nachdem Redis geleert wurde)
/// werden sie aus den Seed-Dateien geschrieben; danach hält der Server sie als <see cref="MasterDataSnapshot"/> im Prozess.
/// </summary>
public sealed class MasterDataStore(IConnectionMultiplexer redis) : IDisposable
{
    private static readonly string[] s_types =
    [
        "article", "mixture", "batch", "mould", "order", "cost", "maint:plan", "maint:hist", "role", "user", "rule", "holiday", "calendar",
    ];

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MasterDataSnapshot? _current;

    public void Dispose() => _gate.Dispose();

    /// <summary>Die Stammdaten; beim ersten Aufruf aus Redis gelesen, fehlen sie dort, aus dem Seed eingespielt.</summary>
    public async Task<MasterDataSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_current is { } known) return known;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_current is { } again) return again;

            var database = redis.GetDatabase();
            if (!await database.KeyExistsAsync(RedisKeys.MasterDataVersion)) await SeedAsync(database);
            return _current = await LoadAsync(database);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Schreibt die Stammdaten aus dem Seed neu nach Redis (Start, Neuaufbau nach geleertem Redis).</summary>
    public async Task ReseedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var database = redis.GetDatabase();
            await SeedAsync(database);
            _current = await LoadAsync(database);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stammdaten sind in Redis vorhanden (für die Gesundheitsprüfung).</summary>
    public async Task<bool> IsSeededAsync() => await redis.GetDatabase().KeyExistsAsync(RedisKeys.MasterDataVersion);

    private static async Task SeedAsync(IDatabase database)
    {
        var seed = MasterDataSeed.Build();
        var batch = database.CreateBatch();
        var tasks = new List<Task>
        {
            Save(batch, "article", seed.Articles.Values, a => a.Id),
            Save(batch, "mixture", seed.Mixtures.Values, m => m.Id),
            Save(batch, "batch", seed.MixtureBatches.Values, b => b.Id),
            Save(batch, "mould", seed.Moulds, m => m.Id),
            Save(batch, "order", seed.Orders, o => o.Id),
            Save(batch, "cost", seed.CostRates, r => r.Id),
            Save(batch, "maint:plan", seed.MaintenancePlans, p => p.Id),
            Save(batch, "maint:hist", seed.MaintenanceHistory, h => h.Id),
            Save(batch, "role", seed.Roles.Values, r => r.Id),
            Save(batch, "user", seed.Users, u => u.Id),
            Save(batch, "rule", seed.EscalationRules.Values, r => r.Id),
            Save(batch, "holiday", seed.Holidays, h => h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            Save(batch, "calendar", seed.CalendarDays, d => d.ProductionDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            batch.StringSetAsync(RedisKeys.MasterDataVersion, "1"),
        };
        batch.Execute();
        await Task.WhenAll(tasks);
    }

    private static Task Save<T>(IBatch batch, string type, IEnumerable<T> items, Func<T, string> id)
    {
        var key = RedisKeys.MasterData(type);
        _ = batch.KeyDeleteAsync(key);
        return batch.HashSetAsync(key, [.. items.Select(i => new HashEntry(id(i), JsonSerializer.Serialize(i, s_json)))]);
    }

    private static async Task<MasterDataSnapshot> LoadAsync(IDatabase database)
    {
        var batch = database.CreateBatch();
        var tasks = s_types.ToDictionary(type => type, type => batch.HashGetAllAsync(RedisKeys.MasterData(type)));
        batch.Execute();
        await Task.WhenAll(tasks.Values);

        List<T> Read<T>(string type) =>
            [.. tasks[type].Result.OrderBy(e => (string)e.Name!, StringComparer.Ordinal).Select(e => JsonSerializer.Deserialize<T>(e.Value.ToString(), s_json)!)];

        return new MasterDataSnapshot
        {
            Articles = Read<Article>("article").ToDictionary(a => a.Id),
            Mixtures = Read<Mixture>("mixture").ToDictionary(m => m.Id),
            MixtureBatches = Read<MixtureBatch>("batch").ToDictionary(b => b.Id),
            Moulds = Read<Mould>("mould"),
            Orders = Read<ProductionOrder>("order"),
            CostRates = Read<CostRate>("cost"),
            MaintenancePlans = Read<MaintenancePlan>("maint:plan"),
            MaintenanceHistory = Read<MaintenanceHistory>("maint:hist"),
            Roles = Read<AppRole>("role").ToDictionary(r => r.Id),
            Users = Read<AppUser>("user"),
            EscalationRules = Read<EscalationRule>("rule").ToDictionary(r => r.Id),
            Holidays = Read<Holiday>("holiday"),
            CalendarDays = Read<CalendarDay>("calendar"),
        };
    }
}
