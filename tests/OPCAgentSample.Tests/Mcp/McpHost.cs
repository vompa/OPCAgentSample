using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Mcp;

/// <summary>Rollen des MCP-Zugangs in den Tests mit je einem eigenen API-Key.</summary>
internal static class McpKeys
{
    public const string ShiftLead = "test-shift-lead-key";
    public const string HallLead = "test-hall-lead-key";
    public const string ProductionLead = "test-production-lead-key";

    public static string For(string role) => role switch
    {
        McpRoles.ShiftLead => ShiftLead,
        McpRoles.HallLead => HallLead,
        _ => ProductionLead,
    };
}

/// <summary>
/// Die Anwendung mit der aufgenommenen Historie bis zur Demo-Uhr (Do 15.10.2026 09:42) und dem echten Endpunkt <c>/mcp</c>.
/// Wird einmal je Testlauf aufgebaut und von allen MCP-Tests geteilt; Tool-Aufrufe sind rein lesend.
/// </summary>
internal sealed class McpHost
{
    private static readonly object s_gate = new();
    private static Task<McpHost>? s_instance;

    private McpHost(WebApplicationFactory<Program> factory) => Factory = factory;

    public WebApplicationFactory<Program> Factory { get; }

    public static Task<McpHost> GetAsync(InfrastructureFixture infrastructure)
    {
        lock (s_gate)
        {
            return s_instance ??= CreateAsync(infrastructure);
        }
    }

    /// <summary>Eine eigene Anwendung, z. B. mit abweichendem Rate Limit; teilt sich die aufgenommene Historie.</summary>
    public static async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        InfrastructureFixture infrastructure, IDictionary<string, string?>? settings = null, bool withHistory = true)
    {
        if (withHistory) await infrastructure.RestoreIngestedRedisAsync();
        else await infrastructure.FlushRedisAsync();
        return Build(infrastructure, settings);
    }

    public HttpClient CreateHttpClient(string? role)
    {
        var client = Factory.CreateClient();
        if (role is not null) client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, McpKeys.For(role));
        return client;
    }

    public async Task<McpClient> ConnectAsync(string role)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            CreateHttpClient(role),
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    /// <summary>Ruft ein Tool auf und liefert das strukturierte Ergebnis; ein Fehlerergebnis wird zur Ausnahme mit dem Text für das Modell.</summary>
    public async Task<JsonElement> CallAsync(string role, string tool, object? arguments = null)
    {
        await using var client = await ConnectAsync(role);
        var result = await client.CallToolAsync(tool, ToDictionary(arguments));
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (result.IsError == true) throw new McpToolFailedException(tool, text);

        return result.StructuredContent is { } structured ? structured.Clone() : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static Dictionary<string, object?> ToDictionary(object? arguments)
    {
        if (arguments is null) return [];

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(arguments));
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    private static async Task<McpHost> CreateAsync(InfrastructureFixture infrastructure)
    {
        await infrastructure.RestoreIngestedRedisAsync();
        return new McpHost(Build(infrastructure, null));
    }

    private static WebApplicationFactory<Program> Build(InfrastructureFixture infrastructure, IDictionary<string, string?>? settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
            ["Redis:CommandTimeout"] = "00:00:01",
            ["Demo:StreamDelay"] = "00:00:00",
            ["Opc:Enabled"] = "false",
            ["McpSecurity:RateLimitPerMinute"] = "100000",
            ["McpSecurity:ApiKeys:0:Name"] = "test-shift-lead",
            ["McpSecurity:ApiKeys:0:Role"] = McpRoles.ShiftLead,
            ["McpSecurity:ApiKeys:0:KeySha256"] = ApiKeyHasher.Hash(McpKeys.ShiftLead),
            ["McpSecurity:ApiKeys:1:Name"] = "test-hall-lead",
            ["McpSecurity:ApiKeys:1:Role"] = McpRoles.HallLead,
            ["McpSecurity:ApiKeys:1:KeySha256"] = ApiKeyHasher.Hash(McpKeys.HallLead),
            ["McpSecurity:ApiKeys:2:Name"] = "test-production-lead",
            ["McpSecurity:ApiKeys:2:Role"] = McpRoles.ProductionLead,
            ["McpSecurity:ApiKeys:2:KeySha256"] = ApiKeyHasher.Hash(McpKeys.ProductionLead),
        };
        foreach (var pair in settings ?? new Dictionary<string, string?>()) values[pair.Key] = pair.Value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values)));
    }
}

/// <summary>Das Tool hat dem Modell einen Fehler gemeldet; <see cref="Exception.Message"/> ist der Text, den das Modell sieht.</summary>
internal sealed class McpToolFailedException(string tool, string message) : Exception($"{tool}: {message}")
{
    public string ToolName { get; } = tool;

    public string ModelText { get; } = message;
}
