using OPCAgentSample.Server.Observability;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Mcp;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Observability;

/// <summary>Jeder MCP-Tool-Aufruf zählt mit Tool, Rolle und Ergebnis und erzeugt einen Span ohne Argumente.</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class McpTelemetryTests(InfrastructureFixture infrastructure)
{
    [Fact]
    public async Task Successful_call_counts_ok_with_tool_and_role_records_duration_and_creates_a_span()
    {
        using var metrics = new MetricCollector();
        using var spans = new SpanCollector();
        var host = await McpHost.GetAsync(infrastructure);

        await host.CallAsync(McpRoles.ProductionLead, "get_hall_overview");

        bool Match(Measured m) => m.Tag("tool") == "get_hall_overview" && m.Tag("role") == McpRoles.ProductionLead && m.Tag("result") == AppTelemetry.Results.Ok;
        Assert.True(metrics.Count(AppTelemetry.McpToolCalls, Match) >= 1);
        Assert.True(metrics.Count(AppTelemetry.McpToolDuration, m => m.Tag("tool") == "get_hall_overview") >= 1);

        var span = spans.Items.First(a => a.OperationName == AppTelemetry.Spans.McpTool && Equals(a.GetTagItem("mcp.tool.name"), "get_hall_overview")
                                         && Equals(a.GetTagItem("mcp.role"), McpRoles.ProductionLead));
        Assert.Equal(AppTelemetry.Results.Ok, span.GetTagItem("mcp.result"));
    }

    [Fact]
    public async Task Refused_scope_counts_denied_and_invalid_arguments_count_error()
    {
        using var metrics = new MetricCollector();
        var host = await McpHost.GetAsync(infrastructure);

        await Assert.ThrowsAsync<McpToolFailedException>(() =>
            host.CallAsync(McpRoles.HallLead, "get_downtime_pareto", new { scope = "Halle B", zeitraum = "KW 42" }));
        await Assert.ThrowsAsync<McpToolFailedException>(() =>
            host.CallAsync(McpRoles.HallLead, "get_oee", new { zeitraum = "irgendwann" }));

        Assert.True(metrics.Count(AppTelemetry.McpToolCalls,
            m => m.Tag("tool") == "get_downtime_pareto" && m.Tag("role") == McpRoles.HallLead && m.Tag("result") == AppTelemetry.Results.Denied) >= 1);
        Assert.True(metrics.Count(AppTelemetry.McpToolCalls,
            m => m.Tag("tool") == "get_oee" && m.Tag("role") == McpRoles.HallLead && m.Tag("result") == AppTelemetry.Results.Error) >= 1);
    }

    [Fact]
    public async Task Spans_never_carry_tool_arguments()
    {
        using var spans = new SpanCollector();
        var host = await McpHost.GetAsync(infrastructure);
        var marker = $"marker-{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<McpToolFailedException>(() => host.CallAsync(McpRoles.HallLead, "get_oee", new { zeitraum = marker }));

        Assert.DoesNotContain(spans.Items.SelectMany(a => a.TagObjects), t => t.Value?.ToString()?.Contains(marker, StringComparison.Ordinal) == true);
    }
}
