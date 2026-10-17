using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Observability;

/// <summary><c>GET /api/reports/summary</c>, die Kostenrechnung und der Start ohne erreichbaren OTLP-Endpunkt.</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class ReportsAndStartupTests(InfrastructureFixture infrastructure)
{
    private static readonly string[] s_outcomes = ["ok", "denied", "error"];

    private const string FirstQuestion = "Was muss ich von der Nachtschicht wissen?";

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
            ["Redis:CommandTimeout"] = "00:00:01",
            ["Demo:StreamDelay"] = "00:00:00",
            ["Opc:Enabled"] = "false",
            ["Reports:CacheDuration"] = "00:00:00",
        };
        foreach (var pair in extra ?? []) values[pair.Key] = pair.Value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values)));
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> GetSummaryAsync(HttpClient client)
    {
        using var response = await client.GetAsync(Url("/api/reports/summary"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Summary_without_any_question_is_complete_and_zero()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var summary = await GetSummaryAsync(client);

        Assert.Equal(0, summary.GetProperty("questions").GetProperty("total").GetInt32());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("period").GetProperty("firstQuestionAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("cost").GetProperty("cacheHitRate").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("latency").GetProperty("p95Ms").ValueKind);
        Assert.Equal(ReportsService.HistogramEdgesMs.Length + 1, summary.GetProperty("latencyHistogram").GetArrayLength());
        Assert.All(summary.GetProperty("latencyHistogram").EnumerateArray(), b => Assert.Equal(0, b.GetProperty("count").GetInt32()));
        Assert.Equal(ReportsService.SeriesHours, summary.GetProperty("timeSeries").GetProperty("questions").GetArrayLength());
        Assert.Empty(summary.GetProperty("tools").EnumerateArray());
    }

    [Fact]
    public async Task Summary_counts_questions_and_tool_calls_without_any_question_text()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.PostAsJsonAsync(Url("/api/ask"), new { role = "shift_lead", question = FirstQuestion });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await response.Content.ReadAsStringAsync();
        }

        using var raw = await client.GetAsync(Url("/api/reports/summary"));
        var text = await raw.Content.ReadAsStringAsync();
        var summary = JsonDocument.Parse(text).RootElement;

        Assert.DoesNotContain("Nachtschicht", text, StringComparison.Ordinal);
        Assert.Equal(2, summary.GetProperty("questions").GetProperty("total").GetInt32());
        Assert.Equal(2, summary.GetProperty("questions").GetProperty("last24Hours").GetInt32());
        Assert.Equal(0, summary.GetProperty("questions").GetProperty("answeredByClaude").GetInt32());
        Assert.Equal(2, summary.GetProperty("latency").GetProperty("measured").GetInt32());
        Assert.Equal(2, summary.GetProperty("latencyHistogram").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        Assert.Equal(2, summary.GetProperty("timeSeries").GetProperty("questions").EnumerateArray().Sum(v => v.GetInt32()));
        var tools = summary.GetProperty("tools").EnumerateArray().ToList();
        Assert.NotEmpty(tools);
        Assert.All(tools, row =>
        {
            Assert.Equal("shift_lead", row.GetProperty("role").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("tool").GetString()));
            Assert.Contains(row.GetProperty("outcome").GetString(), s_outcomes);
            Assert.Equal(0, row.GetProperty("calls").GetInt32() % 2);
        });
    }

    [Fact]
    public async Task Summary_reports_the_ingestion_state_of_the_server()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var ingestion = (await GetSummaryAsync(client)).GetProperty("ingestion");

        Assert.True(ingestion.GetProperty("liveConnections").GetInt32() >= 0);
        Assert.True(ingestion.GetProperty("ingestedRecordsSinceStart").GetInt64() >= 0);
        Assert.True(ingestion.GetProperty("quotaDenialsSinceStart").GetInt64() >= 0);
        Assert.True(ingestion.TryGetProperty("simulationClockUtc", out _));
    }

    [Fact]
    public void Cost_estimate_uses_the_configured_prices_per_million_tokens()
    {
        var tokens = new ReportTokens(Input: 1_000_000, Output: 2_000_000, CacheRead: 10_000_000, CacheWrite: 4_000_000);
        var prices = new ReportPrices { InputPerMillion = 0.10m, OutputPerMillion = 0.50m, CacheReadPerMillion = 0.01m, CacheWritePerMillion = 0.125m };

        Assert.Equal(0.10m + 1.00m + 0.10m + 0.50m, ReportsService.EstimateCostUsd(tokens, prices));
        Assert.Equal(0m, ReportsService.EstimateCostUsd(new ReportTokens(0, 0, 0, 0), prices));
    }

    [Fact]
    public void Cache_hit_rate_is_cache_read_over_cache_read_plus_input()
    {
        Assert.Equal(0.75, ReportsService.CacheHitRate(new ReportTokens(100, 5, 300, 20)));
        Assert.Null(ReportsService.CacheHitRate(new ReportTokens(0, 5, 0, 0)));
    }

    [Fact]
    public async Task Server_starts_and_answers_with_export_enabled_but_no_reachable_otlp_endpoint()
    {
        using var factory = await CreateFactoryAsync(new()
        {
            ["Observability:OtlpEnabled"] = "true",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
        });
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(Url("/api/health"));
        using var reports = await client.GetAsync(Url("/api/reports/summary"));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reports.StatusCode);
    }
}
