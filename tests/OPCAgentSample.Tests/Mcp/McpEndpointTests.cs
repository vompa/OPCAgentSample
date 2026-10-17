using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using StackExchange.Redis;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Mcp;

/// <summary>
/// Der Endpunkt <c>/mcp</c> als Ganzes: Toolliste und Annotationen, Resources und Prompts, Anmeldung (401), Rollen-Scoping,
/// Audit jedes Aufrufs, Rate Limit (429 mit Retry-After), Begrenzung der Ergebnisgröße und Herkunftsangabe.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class McpEndpointTests(InfrastructureFixture infrastructure)
{
    private const string InitializeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""";

    private static readonly string[] s_toolNames =
    [
        "get_hall_overview", "get_machine_status", "get_active_alarms", "get_material_reach", "get_order_progress", "get_process_trend",
        "get_shift_summary", "get_oee", "get_downtime_pareto", "compare", "get_maintenance_due", "get_condition_warnings",
        "check_maintenance_conflicts", "get_reliability_kpis", "get_action_items", "get_overproduction", "trace_batch",
    ];

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative)) { Content = content };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request);
    }

    private static async Task<List<(string Client, string Role, string Action, string Outcome, string? Error)>> AuditAsync(McpHost host, string action)
    {
        var entries = await AuditStream.ReadAsync(host.Factory.Services.GetRequiredService<IConnectionMultiplexer>());
        return [.. entries.Where(e => e.Channel == AuditChannels.Mcp && e.Action == action).Select(e => (e.Client, e.Role, e.Action, e.Outcome, e.Error))];
    }

    [Fact]
    public async Task Tool_list_has_the_17_tools_all_read_only_idempotent_and_not_destructive()
    {
        var host = await McpHost.GetAsync(infrastructure);
        await using var client = await host.ConnectAsync(McpRoles.ShiftLead);

        var tools = await client.ListToolsAsync();

        Assert.Equal(s_toolNames.Order(StringComparer.Ordinal), tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint, $"{tool.Name}: ReadOnly");
            Assert.True(tool.ProtocolTool.Annotations?.IdempotentHint, $"{tool.Name}: Idempotent");
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint ?? false, $"{tool.Name}: nicht destruktiv");
            Assert.False(tool.ProtocolTool.Annotations?.OpenWorldHint ?? false, $"{tool.Name}: geschlossen");
            Assert.True(tool.Description.Length > 150, $"{tool.Name}: Beschreibung fürs Modell");
            Assert.Contains("Nächster Schritt", tool.Description, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(tools, t => t.Name.StartsWith("set_", StringComparison.Ordinal) || t.Name.Contains("write", StringComparison.Ordinal)
            || t.Name.Contains("start", StringComparison.Ordinal) || t.Name.Contains("stop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resources_and_prompts_are_listed_and_readable()
    {
        var host = await McpHost.GetAsync(infrastructure);
        await using var shift = await host.ConnectAsync(McpRoles.ShiftLead);
        await using var production = await host.ConnectAsync(McpRoles.ProductionLead);

        var resources = await shift.ListResourcesAsync();
        var templates = await shift.ListResourceTemplatesAsync();
        var prompts = await shift.ListPromptsAsync();

        Assert.Equal(
            ["plant://kpi-definitions", "plant://shift-calendar", "plant://structure"], resources.Select(r => r.Uri).Order(StringComparer.Ordinal));
        Assert.Equal("plant://machine/{id}/addressspace", Assert.Single(templates).UriTemplate);
        Assert.Equal(
            ["schichtuebergabe", "tagesbesprechung_halle", "weihnachtsstillstand", "wochenreport"], prompts.Select(p => p.Name).Order(StringComparer.Ordinal));

        var structure = await ReadTextAsync(shift, "plant://structure");
        var plantStructure = await ReadTextAsync(production, "plant://structure");
        Assert.Contains("RT-04", structure, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\":\"B\"", structure, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"B\"", plantStructure, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"C\"", plantStructure, StringComparison.Ordinal);

        var calendar = await ReadTextAsync(shift, "plant://shift-calendar");
        Assert.Contains("2026-10-15F", calendar, StringComparison.Ordinal);
        Assert.Contains("Nationalfeiertag", calendar, StringComparison.Ordinal);
        Assert.Contains("Betriebsurlaub", calendar, StringComparison.Ordinal);

        var definitions = await ReadTextAsync(shift, "plant://kpi-definitions");
        Assert.Contains("Verfügbarkeit × Leistung × Qualität", definitions, StringComparison.Ordinal);
        Assert.Contains("unter 90 % Warnung, unter 75 % Problem", definitions, StringComparison.Ordinal);

        var address = await ReadTextAsync(shift, "plant://machine/RT-04/addressspace");
        Assert.Contains("ns=2;s=HalleA.Pressen.RT-04.Prozess.PresskraftAufbauzeit", address, StringComparison.Ordinal);
        var oven = await ReadTextAsync(shift, "plant://machine/OF-02/addressspace");
        Assert.Contains("ns=2;s=HalleA.Oefen.OF-02.Planung.Zuordnungsregel", oven, StringComparison.Ordinal);

        var prompt = await shift.GetPromptAsync("schichtuebergabe", new Dictionary<string, object?> { ["schicht"] = "vorherige" });
        var text = string.Concat(prompt.Messages.Select(m => (m.Content as TextContentBlock)?.Text));
        Assert.Contains("get_shift_summary", text, StringComparison.Ordinal);
        Assert.Contains("get_material_reach", text, StringComparison.Ordinal);
        var christmas = await shift.GetPromptAsync("weihnachtsstillstand");
        Assert.Contains("von 2026-12-01, bis 2027-01-17", string.Concat(christmas.Messages.Select(m => (m.Content as TextContentBlock)?.Text)), StringComparison.Ordinal);
    }

    private static async Task<string> ReadTextAsync(ModelContextProtocol.Client.McpClient client, string uri)
    {
        var result = await client.ReadResourceAsync(uri);
        return string.Concat(result.Contents.OfType<TextResourceContents>().Select(c => c.Text));
    }

    [Fact]
    public async Task Without_a_valid_api_key_the_endpoint_answers_401_while_the_web_api_stays_open()
    {
        var host = await McpHost.GetAsync(infrastructure);
        using var anonymous = host.CreateHttpClient(null);
        using var wrong = host.Factory.CreateClient();
        wrong.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, "falscher-key");
        using var bearer = host.Factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", McpKeys.HallLead);

        using var withoutKey = await PostAsync(anonymous, InitializeRequest);
        using var withWrongKey = await PostAsync(wrong, InitializeRequest);
        using var withBearer = await PostAsync(bearer, InitializeRequest);
        using var health = await anonymous.GetAsync(new Uri("/api/health", UriKind.Relative));
        using var overview = await anonymous.GetAsync(new Uri("/api/overview?role=shift_lead", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withWrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withBearer.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
    }

    [Theory]
    [InlineData(McpRoles.ShiftLead, "get_oee", """{"scope":"Werk"}""")]
    [InlineData(McpRoles.ShiftLead, "get_hall_overview", """{"hallId":"B"}""")]
    [InlineData(McpRoles.ShiftLead, "get_order_progress", """{"scope":"Halle C"}""")]
    [InlineData(McpRoles.ShiftLead, "check_maintenance_conflicts", """{"hallId":"A","zeitraum":"heute"}""")]
    [InlineData(McpRoles.ShiftLead, "get_action_items", """{"role":"Produktionsleiter"}""")]
    [InlineData(McpRoles.HallLead, "get_oee", """{"scope":"Werk","zeitraum":"KW 42"}""")]
    [InlineData(McpRoles.HallLead, "get_downtime_pareto", """{"scope":"Halle B","zeitraum":"KW 42"}""")]
    [InlineData(McpRoles.HallLead, "get_action_items", """{"role":"Schichtleiter"}""")]
    public async Task Forbidden_scope_or_role_is_refused_with_a_message_for_the_model_and_audited_as_denied(string role, string tool, string arguments)
    {
        var host = await McpHost.GetAsync(infrastructure);
        var before = (await AuditAsync(host, tool)).Count;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments);

        var failure = await Assert.ThrowsAsync<McpToolFailedException>(() => host.CallAsync(role, tool, parsed));

        Assert.Contains("Keine Berechtigung", failure.ModelText, StringComparison.Ordinal);
        var audit = await AuditAsync(host, tool);
        Assert.Equal(before + 1, audit.Count);
        Assert.Equal("denied", audit[^1].Outcome);
        Assert.Equal(role, audit[^1].Role);
        Assert.StartsWith("test-", audit[^1].Client, StringComparison.Ordinal);
        Assert.Contains("Keine Berechtigung", audit[^1].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_lead_sees_the_plant_and_every_successful_call_is_audited_as_ok()
    {
        var host = await McpHost.GetAsync(infrastructure);
        var before = (await AuditAsync(host, "get_hall_overview")).Count;

        var overview = await host.CallAsync(McpRoles.ProductionLead, "get_hall_overview", new { hallId = "Werk" });

        var halls = overview.At("hallen");
        Assert.Equal(3, halls.GetArrayLength());
        Assert.Equal(77, halls.Item("halle", "A").Number("oee_prozent"));
        Assert.Equal(81, halls.Item("halle", "B").Number("oee_prozent"));
        var audit = await AuditAsync(host, "get_hall_overview");
        Assert.Equal(before + 1, audit.Count);
        Assert.Equal(("test-production-lead", "production_lead", "get_hall_overview", "ok"), (audit[^1].Client, audit[^1].Role, audit[^1].Action, audit[^1].Outcome));
    }

    [Fact]
    public async Task Invalid_arguments_return_an_explaining_error_and_are_audited_as_error_not_denied()
    {
        var host = await McpHost.GetAsync(infrastructure);

        var period = await Assert.ThrowsAsync<McpToolFailedException>(() => host.CallAsync(McpRoles.HallLead, "get_oee", new { zeitraum = "irgendwann" }));
        var scope = await Assert.ThrowsAsync<McpToolFailedException>(() => host.CallAsync(McpRoles.HallLead, "get_machine_status", new { machineId = "XY-99" }));

        Assert.Contains("Unbekannter Zeitraum", period.ModelText, StringComparison.Ordinal);
        Assert.Contains("aktuelle Schicht", period.ModelText, StringComparison.Ordinal);
        Assert.Contains("Unbekannter Bereich", scope.ModelText, StringComparison.Ordinal);
        var audit = await AuditAsync(host, "get_machine_status");
        Assert.Equal("error", audit[^1].Outcome);
    }

    [Fact]
    public async Task Message_filter_follows_the_role_all_from_warning_only_escalations()
    {
        var host = await McpHost.GetAsync(infrastructure);
        var arguments = new { scope = "Halle A", zeitraum = "seit 06:00", inklusiveQuittiert = true };

        var shift = await host.CallAsync(McpRoles.ShiftLead, "get_active_alarms", arguments);
        var hall = await host.CallAsync(McpRoles.HallLead, "get_active_alarms", arguments);

        var kindsShift = shift.At("alarme").Items().Select(a => a.Text("art")).ToList();
        var kindsHall = hall.At("alarme").Items().Select(a => a.Text("art")).ToList();
        Assert.Contains("Hinweis", kindsShift);
        Assert.DoesNotContain("Hinweis", kindsHall);
        Assert.Contains("Prozesswarnung", kindsHall);
        Assert.Equal("ab Warnung", hall.Text("meldungsfilter"));
        Assert.True(kindsHall.Count < kindsShift.Count);
    }

    [Fact]
    public async Task Results_carry_data_stand_source_and_are_limited_in_size()
    {
        using var factory = await McpHost.CreateFactoryAsync(infrastructure, new Dictionary<string, string?> { ["Mcp:MaxRows"] = "5" });
        var host = new McpTestClient(factory);

        var pareto = await host.CallAsync(McpRoles.HallLead, "get_downtime_pareto", new { scope = "Halle A", zeitraum = "KW 42" });

        Assert.Equal(5, pareto.At("eintraege").GetArrayLength());
        Assert.Contains(pareto.At("hinweise").Items(), h => h.GetString()!.Contains("gekürzt", StringComparison.Ordinal));
        Assert.Equal("2026-10-15T07:42:00Z", pareto.Text("source_until"));
        Assert.Equal("Do 15.10. 09:42", pareto.Text("datenstand"));
        Assert.False(string.IsNullOrWhiteSpace(pareto.Text("quelle")));
        Assert.Equal("get_downtime_pareto", pareto.Text("tool"));
    }

    [Fact]
    public async Task Source_label_names_Redis_as_the_only_store()
    {
        var host = await McpHost.GetAsync(infrastructure);

        var result = await host.CallAsync(McpRoles.HallLead, "get_oee", new { scope = "Halle A", zeitraum = "KW 42", gruppierung = "Station" });

        Assert.Contains("Redis", result.Text("quelle"), StringComparison.Ordinal);
        Assert.DoesNotContain("PostgreSQL", result.Text("quelle"), StringComparison.Ordinal);
        Assert.DoesNotContain("Rebuild", result.Text("quelle"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_answers_429_with_retry_after_after_the_configured_number_of_requests()
    {
        using var factory = await McpHost.CreateFactoryAsync(
            infrastructure, new Dictionary<string, string?> { ["McpSecurity:RateLimitPerMinute"] = "3" }, withHistory: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, McpKeys.ShiftLead);

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < 4; i++)
        {
            var response = await PostAsync(client, InitializeRequest);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = response;
            else response.Dispose();
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.NotNull(limited);
        using (limited)
        {
            Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
            Assert.InRange(int.Parse(values!.Single(), System.Globalization.CultureInfo.InvariantCulture), 1, 60);
            Assert.Contains("Zu viele Anfragen", await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_key_entry_without_a_valid_hash_stops_the_application_from_starting()
    {
        using var factory = await McpHost.CreateFactoryAsync(
            infrastructure,
            new Dictionary<string, string?>
            {
                ["McpSecurity:ApiKeys:0:KeySha256"] = string.Empty,
                ["McpSecurity:ApiKeys:0:Name"] = "x",
                ["McpSecurity:ApiKeys:0:Role"] = McpRoles.ShiftLead,
            },
            withHistory: false);

        // Ein Eintrag ohne gültigen Hash lässt die Anwendung nicht starten: Es gibt keinen ungeschützten Zugang.
        var failure = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(failure);
    }

    /// <summary>MCP-Client für eine eigene Anwendung (z. B. mit anderer Konfiguration).</summary>
    private sealed class McpTestClient(WebApplicationFactory<Program> factory)
    {
        public async Task<System.Text.Json.JsonElement> CallAsync(string role, string tool, object? arguments)
        {
            var http = factory.CreateClient();
            http.DefaultRequestHeaders.Add(ApiKeyDefaults.HeaderName, McpKeys.For(role));
            var transport = new ModelContextProtocol.Client.HttpClientTransport(
                new ModelContextProtocol.Client.HttpClientTransportOptions
                {
                    Endpoint = new Uri("http://localhost/mcp"),
                    TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                },
                http,
                ownsHttpClient: true);
            await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport);
            var result = await client.CallToolAsync(tool, McpHost.ToDictionary(arguments));
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            if (result.IsError == true) throw new McpToolFailedException(tool, text);

            return System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
        }
    }
}
