using System.Security.Claims;
using OPCAgentSample.Server.Security;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server;

[Collection(InfrastructureFixture.Name)]
public sealed class AuditLogTests(InfrastructureFixture infrastructure)
{
    private static ClaimsPrincipal User(string name, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], "test"));

    [Fact]
    public async Task Record_stores_client_role_channel_action_and_outcome()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 7, 42, 0, TimeSpan.Zero));
        await using var services = await infrastructure.CreateServicesAsync(time);

        await services.Get<AuditLog>().RecordAsync(
            AuditChannels.Mcp, User("martin.huber", "shift_lead"), "get_hall_overview", "hallId=A", AuditOutcomes.Ok);

        var entry = Assert.Single(await AuditStream.ReadAsync(services.Get<IConnectionMultiplexer>()));
        Assert.Equal(time.GetUtcNow(), entry.TimestampUtc);
        Assert.Equal(("martin.huber", "shift_lead", "mcp"), (entry.Client, entry.Role, entry.Channel));
        Assert.Equal(("get_hall_overview", "hallId=A", "ok"), (entry.Action, entry.Arguments, entry.Outcome));
        Assert.Null(entry.Error);
    }

    [Fact]
    public async Task Long_arguments_and_errors_are_shortened_to_300_characters()
    {
        await using var services = await infrastructure.CreateServicesAsync();

        await services.Get<AuditLog>().RecordAsync(
            AuditChannels.Rest, null, "x", new string('a', 500), AuditOutcomes.Error, new string('e', 400));

        var entry = Assert.Single(await AuditStream.ReadAsync(services.Get<IConnectionMultiplexer>()));
        Assert.Equal(AuditLog.MaxTextLength + 3, entry.Arguments!.Length);
        Assert.EndsWith("...", entry.Arguments, StringComparison.Ordinal);
        Assert.Equal(AuditLog.MaxTextLength + 3, entry.Error!.Length);
        Assert.Equal(("unbekannt", "unbekannt"), (entry.Client, entry.Role));
    }

    [Fact]
    public async Task Unreachable_redis_does_not_break_the_caller()
    {
        await using var services = TestServices.Create(InfrastructureFixture.UnreachableRedis, null, null);

        var exception = await Record.ExceptionAsync(() =>
            services.Get<AuditLog>().RecordAsync(AuditChannels.Mcp, null, "x", null, AuditOutcomes.Ok));

        Assert.Null(exception);
    }
}
