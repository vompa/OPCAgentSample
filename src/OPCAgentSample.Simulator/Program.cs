using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OPCAgentSample.Simulator.Observability;

namespace OPCAgentSample.Simulator;

/// <summary>Einstiegspunkt des Simulators (eigene Klasse statt Top-Level-Anweisungen, damit kein Typ <c>Program</c> in Testprojekten kollidiert).</summary>
internal static class SimulatorEntryPoint
{
    public static async Task Main(string[] args)
    {
        // appsettings.json liegt neben der Anwendung, unabhängig vom Arbeitsverzeichnis.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Die Simulationsuhr wird nur lokal gesteuert (Kommandozeile oder Konfiguration), nie über OPC.
        builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
        {
            ["--bind"] = $"{SimulatorOptions.SectionName}:BindAddress",
            ["--port"] = $"{SimulatorOptions.SectionName}:Port",
            ["--speed"] = $"{SimulatorOptions.SectionName}:Speed",
            ["--start"] = $"{SimulatorOptions.SectionName}:Start",
            ["--interval"] = $"{SimulatorOptions.SectionName}:UpdateIntervalMs",
            ["--seed"] = $"{SimulatorOptions.SectionName}:Seed",
            ["--pki"] = $"{SimulatorOptions.SectionName}:PkiPath",
            ["--security"] = $"{SimulatorOptions.SectionName}:EnableSecurity",
        });

        builder.AddObservability();
        builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.SectionName));
        builder.Services.AddHostedService<SimulatorService>();

        await builder.Build().RunAsync();
    }
}

/// <summary>Betreibt den Simulator, solange der Host läuft (Strg+C beendet sauber).</summary>
internal sealed class SimulatorService(IOptions<SimulatorOptions> options, ILogger<SimulatorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SimulatorHost simulator;
        try
        {
            simulator = await SimulatorHost.StartAsync(options.Value, logger, stoppingToken);
        }
        catch (Exception) when (!stoppingToken.IsCancellationRequested)
        {
            // Ein gescheiterter Start soll als Fehler erkennbar sein (sonst endet der Host mit 0)
            Environment.ExitCode = 1;
            throw;
        }

        await using var running = simulator;
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Strg+C: der Simulator wird beim Verlassen des using-Blocks beendet.
        }
    }
}
