using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.DemoAccess;

/// <summary>CORS für die getrennt gehostete Oberfläche (<c>Cors:AllowedOrigins</c>) und das Abschalten von <c>wwwroot</c> (<c>Web:ServeUi</c>).</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class CrossOriginTests(InfrastructureFixture infrastructure)
{
    private const string UiOrigin = "https://opc.vompa.dev";

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(IDictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
            ["Redis:CommandTimeout"] = "00:00:01",
            ["Demo:Enabled"] = "true",
            ["Demo:AccessCodeSha256"] = ApiKeyHasher.Hash("code"),
            ["Demo:CookieKey"] = DemoAccessEndpointTests.CookieKey,
        };
        foreach (var (key, value) in extra) values[key] = value;
        await infrastructure.FlushRedisAsync();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).UseSetting("Opc:Enabled", "false")
                .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values)));
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/demo/login", UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return request;
    }

    [Fact]
    public async Task Preflight_from_the_configured_origin_allows_credentials()
    {
        using var factory = await CreateFactoryAsync(new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = UiOrigin + "/" });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight(UiOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(UiOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task Other_origins_get_no_cors_headers()
    {
        using var factory = await CreateFactoryAsync(new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = UiOrigin });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Preflight("https://evil.example"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Unauthorized_answers_carry_cors_headers_so_the_ui_can_read_the_status()
    {
        using var factory = await CreateFactoryAsync(new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = UiOrigin });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/me", UriKind.Relative));
        request.Headers.Add("Origin", UiOrigin);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(UiOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task Without_configured_origins_cors_is_off()
    {
        using var factory = await CreateFactoryAsync(new Dictionary<string, string?>());
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/health", UriKind.Relative));
        request.Headers.Add("Origin", UiOrigin);

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task The_ui_is_not_served_when_serve_ui_is_off()
    {
        using var factory = await CreateFactoryAsync(new Dictionary<string, string?> { ["Web:ServeUi"] = "false" });
        using var client = factory.CreateClient();

        using var root = await client.GetAsync(new Uri("/", UriKind.Relative));
        using var health = await client.GetAsync(new Uri("/api/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("healthy", (await health.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("opc.vompa.dev")]
    [InlineData("https://opc.vompa.dev/pfad")]
    [InlineData("ftp://opc.vompa.dev")]
    public void Invalid_origins_are_rejected_with_a_clear_message(string origin)
    {
        var options = new WebUiCorsOptions { AllowedOrigins = [origin] };

        var error = Assert.Throws<InvalidOperationException>(() => options.ParseOrigins());

        Assert.Contains("Cors:AllowedOrigins", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Origins_are_trimmed_and_normalized()
    {
        var options = new WebUiCorsOptions { AllowedOrigins = [" https://opc.vompa.dev/ ", "http://localhost:4173", ""] };

        Assert.Equal(["https://opc.vompa.dev", "http://localhost:4173"], options.ParseOrigins());
    }
}
