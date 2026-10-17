using System.Globalization;
using OPCAgentSample.Server.Infrastructure.Redis;
using OPCAgentSample.Server.Security;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server;

/// <summary>Liest das Audit-Log (Stream <c>audit</c>) in Redis für die Prüfungen der Tests.</summary>
internal static class AuditStream
{
    public static async Task<IReadOnlyList<AuditEntry>> ReadAsync(IConnectionMultiplexer redis)
    {
        var rows = await redis.GetDatabase().StreamRangeAsync(RedisKeys.Audit);
        return [.. rows.Select(row =>
        {
            string Field(string name) => row.Values.First(v => v.Name == name).Value.ToString();
            string? Optional(string name) => Field(name) is { Length: > 0 } value ? value : null;
            return new AuditEntry(
                new DateTimeOffset(long.Parse(Field("ts"), CultureInfo.InvariantCulture), TimeSpan.Zero), Field("client"), Field("role"),
                Field("channel"), Field("action"), Optional("args"), Field("outcome"), Optional("error"));
        })];
    }
}
