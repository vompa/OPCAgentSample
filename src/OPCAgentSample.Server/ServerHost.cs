using OPCAgentSample.Server.Agent;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Collector;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.DemoAccess;
using OPCAgentSample.Server.Health;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Server.Live;
using OPCAgentSample.Server.Mcp;
using OPCAgentSample.Server.Observability;

namespace OPCAgentSample.Server;

/// <summary>Aufbau des Servers; <c>Program.cs</c> und das Werkzeug zum Erzeugen der Momentaufnahme (<c>OPCAgentSample.Snapshot</c>) nutzen dasselbe.</summary>
public static class ServerHost
{
    /// <param name="options">Start-Optionen (Umgebung, Argumente).</param>
    /// <param name="configure">Ändert die Konfiguration, bevor die Dienste sie lesen (z. B. Verbindungen, Schalter).</param>
    public static WebApplication Build(WebApplicationOptions options, Action<ConfigurationManager>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options);
        configure?.Invoke(builder.Configuration);
        builder.AddObservability();

        builder.Services
            .AddForwardedHeadersOptions()
            .AddRedisStore(builder.Configuration)
            .AddChatHistory(builder.Configuration)
            .AddIngestion(builder.Configuration)
            .AddOpcCollector(builder.Configuration)
            .AddReferenceDemo(builder.Configuration)
            .AddMcpEndpoint(builder.Configuration)
            .AddClaudeAgent(builder.Configuration)
            .AddDemoAccess(builder.Configuration)
            .AddApi()
            .AddWebUiCors()
            .AddLiveStream(builder.Configuration);

        var app = builder.Build();

        app.UseForwardedHeadersIfEnabled();
        app.UseWebUi();
        app.UseWebUiCors();
        app.UseDemoAccess();
        app.UseMcpEndpoint();
        app.MapHealthEndpoint();
        app.MapApi();
        app.MapMcpEndpoint();
        return app;
    }
}
