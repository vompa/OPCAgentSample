using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OPCAgentSample.Server.Security;

namespace OPCAgentSample.Server.Agent;

/// <summary>Ein Tool des MCP-Servers, wie es Claude angeboten wird. Name, Beschreibung und Schema stammen vom MCP-Server (eine Quelle).</summary>
public sealed record AgentTool(string Name, string Description, JsonElement InputSchema);

/// <param name="IsError">Das Tool hat einen Fehler gemeldet oder wurde verweigert; <see cref="Text"/> ist der Text für das Modell.</param>
public sealed record ToolOutcome(bool IsError, string Text);

/// <summary>Eine Verbindung für genau eine Frage. Alle Aufrufe laufen mit der Rolle des Fragenden.</summary>
public interface IAgentToolConnection : IAsyncDisposable
{
    Task<ToolOutcome> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken);
}

/// <summary>Zugang zu den Tools des eigenen MCP-Servers, gebunden an eine Rolle.</summary>
public interface IAgentToolbox
{
    /// <summary>Die Tools, nach Name sortiert und unverändert über alle Fragen (Voraussetzung für das Prompt-Caching).</summary>
    Task<IReadOnlyList<AgentTool>> ListToolsAsync(CancellationToken cancellationToken);

    Task<IAgentToolConnection> ConnectAsync(string roleId, CancellationToken cancellationToken);
}

/// <summary>Liefert den HTTP-Client und die Adresse, mit denen der Agent den eigenen <c>/mcp</c>-Endpunkt erreicht (Tests ersetzen ihn).</summary>
public interface IMcpLoopback
{
    HttpClient CreateClient();

    Uri Endpoint { get; }
}

/// <summary>
/// Schleife auf den eigenen Server: nie ein Ziel aus der Anfrage, sondern die konfigurierte Loopback-Adresse oder der Port,
/// auf dem die laufende Anfrage ankam.
/// </summary>
public sealed class HttpMcpLoopback(IHttpContextAccessor httpContext, IOptions<ClaudeOptions> options) : IMcpLoopback
{
    private static readonly SocketsHttpHandler s_handler = new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    public HttpClient CreateClient() => new(s_handler, disposeHandler: false);

    public Uri Endpoint
    {
        get
        {
            if (options.Value.McpUrl is { } configured) return new Uri(configured);

            var port = httpContext.HttpContext?.Connection.LocalPort
                ?? throw new InvalidOperationException("Ohne Anfrage ist die Adresse des eigenen Servers unbekannt (Claude:McpUrl setzen).");
            return new Uri($"http://127.0.0.1:{port}/mcp");
        }
    }
}

/// <summary>
/// Zufällige API-Keys des Agenten, je Rolle einer, nur im Speicher dieses Prozesses. Sie sind beim eigenen MCP-Server als
/// Clients <c>claude-agent (rolle)</c> eingetragen: Authentifizierung, Rollen-Scoping, Rate Limit und Audit-Log greifen
/// dadurch genauso wie bei externem Zugriff.
/// </summary>
public sealed class InternalAgentKeys
{
    private readonly Dictionary<string, string> _keys = McpRoles.All.ToDictionary(
        role => role, _ => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), StringComparer.Ordinal);

    public string KeyFor(string role) =>
        _keys.TryGetValue(role, out var key) ? key : throw new ArgumentException($"Unbekannte Rolle '{role}'.", nameof(role));

    public static string ClientName(string role) => $"claude-agent ({role})";

    public IEnumerable<ApiKeyEntry> Entries() =>
        _keys.Select(pair => new ApiKeyEntry { Name = ClientName(pair.Key), Role = pair.Key, KeySha256 = ApiKeyHasher.Hash(pair.Value) });
}

/// <summary>Trägt die Keys des Agenten in die MCP-Absicherung ein, aber nur, wenn Claude angebunden ist.</summary>
public sealed class AgentKeyConfigurator(IOptions<ClaudeOptions> claude, InternalAgentKeys keys) : IConfigureOptions<McpSecurityOptions>
{
    public void Configure(McpSecurityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!claude.Value.Enabled) return;

        foreach (var entry in keys.Entries()) options.ApiKeys.Add(entry);
    }
}

/// <summary>
/// Echter MCP-Client gegen den eigenen <c>/mcp</c>-Endpunkt. Die Tool-Definitionen kommen vom Server (kein Duplikat im Agenten),
/// und jeder Aufruf geht durch Authentifizierung, Rollen-Scoping und Audit-Filter wie bei einem externen Client.
/// </summary>
public sealed class McpAgentToolbox(IMcpLoopback loopback, InternalAgentKeys keys) : IAgentToolbox, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AgentTool>? _tools;

    public async Task<IReadOnlyList<AgentTool>> ListToolsAsync(CancellationToken cancellationToken)
    {
        if (_tools is not null) return _tools;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tools is not null) return _tools;

            // Die Liste ist für alle Rollen gleich; Rollen unterscheiden sich erst beim Aufruf (Scoping).
            await using var connection = (McpAgentConnection)await ConnectAsync(McpRoles.ShiftLead, cancellationToken);
            var listed = await connection.Client.ListToolsAsync(cancellationToken: cancellationToken);
            _tools = listed
                .Where(tool => tool.ProtocolTool.Annotations?.ReadOnlyHint == true)
                .OrderBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(tool => new AgentTool(tool.Name, tool.Description ?? string.Empty, tool.JsonSchema.Clone()))
                .ToList();
            return _tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    public async Task<IAgentToolConnection> ConnectAsync(string roleId, CancellationToken cancellationToken)
    {
        var http = loopback.CreateClient();
        http.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, keys.KeyFor(roleId));
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = loopback.Endpoint, TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new McpAgentConnection(client);
    }
}

internal sealed class McpAgentConnection(McpClient client) : IAgentToolConnection
{
    public McpClient Client { get; } = client;

    public async Task<ToolOutcome> CallAsync(
        string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var args = arguments.ToDictionary(a => a.Key, a => (object?)a.Value, StringComparer.Ordinal);
        try
        {
            var result = await Client.CallToolAsync(name, args, cancellationToken: cancellationToken);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            return new ToolOutcome(result.IsError == true, text);
        }
        catch (McpException ex)
        {
            // Fehlertexte des eigenen Servers sind absichtlich lesbar (Verweigerungen, ungültige Argumente): Das Modell soll daraus lernen.
            return new ToolOutcome(true, ex.Message);
        }
    }

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
