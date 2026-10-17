using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.History;
using OPCAgentSample.Kpi;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Live;

/// <summary>Stationszustände mit Herkunft (immer „Redis“; leer, solange noch nichts aufgenommen ist).</summary>
public sealed record StationSnapshotResult(StationSnapshot Snapshot, string SourceLabel);

/// <summary>
/// Zustand je Station in Redis (<c>st:{id}</c>, Hash). Der Live-Pfad schreibt ihn nach jeder Aufnahme neu; er ist aus den
/// verdichteten Daten jederzeit wieder zu bilden.
/// </summary>
public sealed class StationStateStore(IConnectionMultiplexer redis, StationStateBuilder builder)
{
    private const string DataField = "data";
    private static readonly TimeSpan s_ttl = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Rechnet alle Zustände zum Datenstand und schreibt sie nach Redis.</summary>
    public async Task<StationSnapshot> RefreshAsync(DateTimeOffset untilUtc, KpiBlock shiftBlock, CancellationToken cancellationToken)
    {
        var snapshot = await builder.BuildAsync(untilUtc, shiftBlock, cancellationToken);
        await WriteAsync(snapshot);
        return snapshot;
    }

    public async Task<StationSnapshotResult> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new StationSnapshotResult(await ReadAsync() ?? StationSnapshot.Empty, "Redis");
    }

    private async Task<StationSnapshot?> ReadAsync()
    {
        var ids = PlantLayout.HallAStations.Select(s => s.Id).ToList();
        var database = redis.GetDatabase();
        var values = await Task.WhenAll(ids.Append(MinuteVariables.HallAClimateSource).Select(id => database.HashGetAsync(RedisKeys.Station(id), DataField)));
        if (values.Length != ids.Count + 1 || values[..ids.Count].Any(v => v.IsNullOrEmpty)) return null;

        try
        {
            var stations = values[..ids.Count].Select(v => JsonSerializer.Deserialize<StationState>(v.ToString(), s_json)!).ToList();
            var climate = values[ids.Count].IsNullOrEmpty ? null : JsonSerializer.Deserialize<ClimateState>(values[ids.Count].ToString(), s_json);
            return new StationSnapshot(stations, climate, stations.Max(s => s.SourceUntilUtc));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteAsync(StationSnapshot snapshot)
    {
            var transaction = redis.GetDatabase().CreateTransaction();
            foreach (var station in snapshot.Stations)
            {
                var key = RedisKeys.Station(station.StationId);
                _ = transaction.KeyDeleteAsync(key);
                _ = transaction.HashSetAsync(key,
                [
                    new HashEntry(DataField, JsonSerializer.Serialize(station, s_json)),
                    new HashEntry("state", station.State.ToString()),
                    new HashEntry("since", station.SinceUtc.ToString("O", CultureInfo.InvariantCulture)),
                    new HashEntry("order", station.OrderId ?? string.Empty),
                    new HashEntry("light", station.Light.ToString()),
                    new HashEntry("message", station.Message),
                    new HashEntry("plan_ratio", station.PlanRatio?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty),
                    new HashEntry("reach_min", station.ReachMinutes?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty),
                    new HashEntry("source_until", station.SourceUntilUtc.ToString("O", CultureInfo.InvariantCulture)),
                ]);
                _ = transaction.KeyExpireAsync(key, s_ttl);
            }

            if (snapshot.Climate is { } climate)
            {
                var key = RedisKeys.Station(MinuteVariables.HallAClimateSource);
                _ = transaction.KeyDeleteAsync(key);
                _ = transaction.HashSetAsync(key,
                [
                    new HashEntry(DataField, JsonSerializer.Serialize(climate, s_json)),
                    new HashEntry("celsius", climate.Celsius.ToString("R", CultureInfo.InvariantCulture)),
                    new HashEntry("humidity", climate.Humidity.ToString("R", CultureInfo.InvariantCulture)),
                    new HashEntry("source_until", climate.SourceUntilUtc.ToString("O", CultureInfo.InvariantCulture)),
                ]);
                _ = transaction.KeyExpireAsync(key, s_ttl);
            }

            await transaction.ExecuteAsync();
    }
}
