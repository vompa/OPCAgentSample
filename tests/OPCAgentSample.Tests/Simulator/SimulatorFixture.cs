using System.Net;
using System.Net.Sockets;
using OPCAgentSample.Domain;
using OPCAgentSample.Simulation.Generation;
using OPCAgentSample.Simulation.Scenario;
using OPCAgentSample.Simulator;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>
/// Startet den Simulator einmal je Testsammlung im Prozess: freier Port, eigenes PKI-Verzeichnis, Uhr auf der Demo-Zeit
/// angehalten (Geschwindigkeit 0), damit Live-Werte exakt mit dem <see cref="LiveStateProvider"/> verglichen werden können.
/// </summary>
public sealed class SimulatorFixture : IAsyncLifetime
{
    private readonly string _pkiRoot = Path.Combine(Path.GetTempPath(), $"opc-agent-sim-{Guid.NewGuid():N}");
    private SimulatorHost _host = null!;

    public string EndpointUrl => _host.EndpointUrl;

    public string ClientPki => Path.Combine(_pkiRoot, "client");

    public ScenarioDefinition Scenario { get; } = HallAScenario.Create();

    public HistoryGenerator Generator { get; private set; } = null!;

    public LiveStateProvider Provider { get; private set; } = null!;

    /// <summary>Stehende Simulationszeit der Tests (Do 15.10.2026 09:42 Ortszeit).</summary>
    public static DateTimeOffset Now => SimulationClock.DemoStart;

    public async Task InitializeAsync()
    {
        Generator = new HistoryGenerator(Scenario);
        Provider = new LiveStateProvider(Generator);
        _host = await SimulatorHost.StartAsync(CreateOptions(Path.Combine(_pkiRoot, "server"), speed: 0));
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        TryDeleteDirectory(_pkiRoot);
    }

    internal static SimulatorOptions CreateOptions(string pkiPath, double speed, string? start = null) => new()
    {
        Port = GetFreePort(),
        Speed = speed,
        Start = start,
        UpdateIntervalMs = 200,
        PkiPath = pkiPath,
    };

    internal static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Aufräumen ist best effort; das Verzeichnis liegt im Temp-Ordner.
        }
    }
}

[CollectionDefinition(Name)]
public sealed class SimulatorFixtureGroup : ICollectionFixture<SimulatorFixture>
{
    public const string Name = "Simulator";
}
