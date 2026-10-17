using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Collector;
using OPCAgentSample.Simulator;
using OPCAgentSample.Tests.Server;
using OPCAgentSample.Tests.Simulator;

namespace OPCAgentSample.Tests.Collector;

/// <summary>Ein Simulator im Prozess mit Temp-PKI, der sich anhalten und (auf demselben Port, mit demselben Zertifikat) neu starten lässt.</summary>
internal sealed class SimulatorInstance : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"opc-agent-collector-{Guid.NewGuid():N}");
    private readonly int _port = SimulatorFixture.GetFreePort();
    private SimulatorHost? _host;

    public string EndpointUrl => $"opc.tcp://localhost:{_port}";

    /// <summary>Ordner für Zertifikate des Collectors dieses Tests.</summary>
    public string ClientPki => Path.Combine(_root, "client");

    public SimulationClock Clock => _host?.Clock ?? throw new InvalidOperationException("Der Simulator läuft nicht.");

    public static async Task<SimulatorInstance> StartAsync(double speed, string? start = null)
    {
        var instance = new SimulatorInstance();
        await instance.StartHostAsync(speed, start);
        return instance;
    }

    public async Task StartHostAsync(double speed, string? start)
    {
        var options = SimulatorFixture.CreateOptions(Path.Combine(_root, "server"), speed, start);
        options.Port = _port;
        _host = await SimulatorHost.StartAsync(options);
    }

    public async Task StopHostAsync()
    {
        if (_host is null) return;
        await _host.DisposeAsync();
        _host = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopHostAsync();
        SimulatorFixture.TryDeleteDirectory(_root);
    }
}

/// <summary>Einstellungen und Hilfen für Tests des Collectors.</summary>
internal static class CollectorSettings
{
    /// <summary>Konfiguration <c>Opc:*</c> für Tests: schnelle Wiederverbindung, Zertifikate werden angenommen.</summary>
    public static IConfiguration Configuration(string endpoint, string pkiRoot, IDictionary<string, string?>? overrides = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Opc:Endpoint"] = endpoint,
            ["Opc:AutoAcceptUntrustedCertificates"] = "true",
            ["Opc:PkiPath"] = pkiRoot,
            ["Opc:KeepAliveInterval"] = "00:00:02",
            ["Opc:ReconnectDelay"] = "00:00:00.500",
            ["Opc:ReconnectMaxDelay"] = "00:00:03",
            ["Opc:LiveRefreshInterval"] = "00:00:01",
            ["Opc:SamplingIntervalMs"] = "200",
        };
        foreach (var pair in overrides ?? new Dictionary<string, string?>()) values[pair.Key] = pair.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    /// <summary>Wartet, bis die Bedingung gilt; Zeitgrenze großzügig (Last der Gesamtsuite), geprüft wird nur das sichtbare Ergebnis.</summary>
    public static Task WaitForAsync(Func<Task<bool>> condition, string what, int seconds = 180) =>
        WaitForAsync(condition, () => what, seconds);

    /// <param name="what">Beschreibung für die Fehlermeldung; wird erst bei Zeitüberschreitung ausgewertet (kann den Zustand zeigen).</param>
    public static async Task WaitForAsync(Func<Task<bool>> condition, Func<string> what, int seconds = 180)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(250);
        }

        Assert.Fail($"Zeitüberschreitung: {what()}");
    }
}

/// <summary>Startet Sitzung und Live-Pfad des Collectors (die Hosted Services der Test-Container laufen sonst nicht) und beendet sie wieder.</summary>
internal sealed class RunningCollector : IAsyncDisposable
{
    private readonly List<IHostedService> _services;

    private RunningCollector(List<IHostedService> services) => _services = services;

    public static async Task<RunningCollector> StartAsync(TestServices services, bool live = true)
    {
        var hosted = services.Provider.GetServices<IHostedService>().ToList();
        var started = new List<IHostedService> { hosted.OfType<OpcSessionProvider>().Single() };
        if (live) started.Add(hosted.OfType<OpcLiveService>().Single());
        foreach (var service in started) await service.StartAsync(CancellationToken.None);
        return new RunningCollector(started);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var service in Enumerable.Reverse(_services)) await service.StopAsync(CancellationToken.None);
    }
}
