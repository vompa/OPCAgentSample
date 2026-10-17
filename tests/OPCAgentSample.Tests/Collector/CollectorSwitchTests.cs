using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Server.Collector;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Tests.Server;
using OPCAgentSample.Tests.Simulator;

namespace OPCAgentSample.Tests.Collector;

/// <summary>
/// Der Schalter <c>Opc:Enabled</c> im echten Server-Host: aus = kein Collector und die Referenzuhr, an = Collector und
/// Simulationsuhr – und der Server startet auch dann, wenn der OPC-Server nicht erreichbar ist.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class CollectorSwitchTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task A_disabled_collector_leaves_the_reference_clock_and_registers_no_history_source()
    {
        await using var host = await StartHostAsync(enabled: false);

        Assert.Null(host.Factory.Services.GetService<OpcSessionProvider>());
        Assert.IsType<ReferenceDemoClock>(host.Factory.Services.GetRequiredService<IDemoClock>());
        Assert.Empty(host.Factory.Services.GetServices<IHistorySource>());
    }

    [Fact]
    public async Task An_enabled_collector_replaces_the_clock_and_the_server_starts_without_an_OPC_server()
    {
        await using var host = await StartHostAsync(enabled: true);

        Assert.NotNull(host.Factory.Services.GetService<OpcSessionProvider>());
        Assert.IsType<OpcSimulationClock>(host.Factory.Services.GetRequiredService<IDemoClock>());
        Assert.IsType<OpcHistorySource>(Assert.Single(host.Factory.Services.GetServices<IHistorySource>()));
        Assert.Null(host.Factory.Services.GetRequiredService<OpcSessionProvider>().Current);
    }

    private async Task<RunningHost> StartHostAsync(bool enabled)
    {
        await infrastructure.FlushRedisAsync();
        var pki = Path.Combine(Path.GetTempPath(), $"opc-agent-switch-{Guid.NewGuid():N}");

        // Opc:Enabled steuert die Registrierung beim Start und muss daher als Host-Einstellung (UseSetting) gesetzt werden.
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment(TestEnvironment.Name)
            .UseSetting("Opc:Enabled", enabled ? "true" : "false")
            .UseSetting("Opc:Endpoint", "opc.tcp://localhost:1")
            .UseSetting("Opc:PkiPath", pki)
            .UseSetting("ConnectionStrings:Redis", infrastructure.RedisConnectionString)
            .UseSetting("Redis:CommandTimeout", "00:00:01"));
        var client = factory.CreateClient();
        return new RunningHost(factory, client, pki);
    }

    private sealed class RunningHost(WebApplicationFactory<Program> factory, HttpClient client, string pki) : IAsyncDisposable
    {
        public WebApplicationFactory<Program> Factory => factory;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await factory.DisposeAsync();
            SimulatorFixture.TryDeleteDirectory(pki);
        }
    }
}
