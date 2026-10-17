using System.Diagnostics;
using System.Security.Claims;

namespace OPCAgentSample.Server.Observability;

/// <summary>
/// Messung eines MCP-Tool-Aufrufs: Span <c>mcp.tool</c> und die Metriken <c>mcp.tool.calls</c> / <c>mcp.tool.duration</c>.
/// Erfasst werden nur Tool-Name, Rolle und Ergebnis, nie Argumente oder Ergebnistexte.
/// Wird das Ergebnis nicht gemeldet (z. B. Abbruch), zählt der Aufruf beim Verwerfen als <c>error</c>.
/// </summary>
public sealed class McpToolTelemetry : IDisposable
{
    private readonly Activity? _activity;
    private readonly string _tool;
    private readonly string _role;
    private readonly long _start = Stopwatch.GetTimestamp();
    private string? _result;

    private McpToolTelemetry(string tool, string role)
    {
        _tool = tool;
        _role = role;
        _activity = AppTelemetry.Source.StartActivity(AppTelemetry.Spans.McpTool, ActivityKind.Internal);
        _activity?.SetTag("mcp.tool.name", tool);
        _activity?.SetTag("mcp.role", role);
    }

    public static McpToolTelemetry Start(string tool, ClaimsPrincipal? user)
    {
        var role = user?.FindFirst(ClaimTypes.Role)?.Value;
        return new McpToolTelemetry(tool, string.IsNullOrEmpty(role) ? "unknown" : role);
    }

    /// <summary>Meldet das Ergebnis (<c>ok</c>, <c>denied</c>, <c>error</c>); der erste Aufruf gilt.</summary>
    public void Complete(string result) => _result ??= result;

    public void Dispose()
    {
        var result = _result ?? AppTelemetry.Results.Error;
        AppTelemetry.ToolCall(_tool, _role, result, Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
        _activity?.SetTag("mcp.result", result);
        if (result == AppTelemetry.Results.Error) _activity?.SetStatus(ActivityStatusCode.Error);
        _activity?.Dispose();
    }
}
