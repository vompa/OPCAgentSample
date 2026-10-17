using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server;

[Collection(InfrastructureFixture.Name)]
public sealed class HealthEndpointTests(InfrastructureFixture infrastructure)
{
    private static WebApplicationFactory<Program> CreateFactory(string redis, int startupAttempts = 30) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).UseSetting("Opc:Enabled", "false").ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = redis,
                    ["Redis:CommandTimeout"] = "00:00:01",
                    ["Redis:StartupAttempts"] = startupAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
                })));

    private static async Task<(HttpStatusCode Status, string Body)> GetHealthAsync(WebApplicationFactory<Program> factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/api/health", UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Healthy_when_redis_answers_and_master_data_are_present_without_any_details()
    {
        await infrastructure.FlushRedisAsync();
        using var factory = CreateFactory(infrastructure.RedisConnectionString);

        // Beim Start schreibt der Server die Stammdaten aus dem Seed nach Redis.
        var (status, body) = await GetHealthAsync(factory);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("""{"status":"healthy"}""", body);
    }

    [Fact]
    public async Task Unavailable_when_the_master_data_vanish_from_redis()
    {
        await infrastructure.FlushRedisAsync();
        using var factory = CreateFactory(infrastructure.RedisConnectionString);
        using var client = factory.CreateClient();
        await infrastructure.FlushRedisAsync();

        var (status, body) = await GetHealthAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("""{"status":"unhealthy"}""", body);
    }

    [Fact]
    public async Task Unavailable_without_details_while_redis_does_not_answer()
    {
        await infrastructure.FlushRedisAsync();
        using var factory = CreateFactory(infrastructure.RedisConnectionString);
        using var client = factory.CreateClient();
        var options = ConfigurationOptions.Parse(infrastructure.RedisConnectionString);
        options.AllowAdmin = true;
        await using var admin = await ConnectionMultiplexer.ConnectAsync(options);

        // Redis hält alle Befehle zwei Sekunden an; der Server wartet höchstens eine Sekunde (Redis:CommandTimeout).
        await admin.GetDatabase().ExecuteAsync("CLIENT", "PAUSE", "2000", "ALL");
        using var response = await client.GetAsync(new Uri("/api/health", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"status":"unhealthy"}""", body);
    }

    [Fact]
    public async Task Server_does_not_start_when_redis_is_unreachable()
    {
        using var factory = CreateFactory(InfrastructureFixture.UnreachableRedis, startupAttempts: 1);

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
    }

    [Fact]
    public async Task Server_does_not_start_with_missing_connection_string()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).UseSetting("Opc:Enabled", "false").ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Redis"] = "" })));

        var exception = await Record.ExceptionAsync(() => Task.Run(() => factory.CreateClient()));

        Assert.NotNull(exception);
    }
}
