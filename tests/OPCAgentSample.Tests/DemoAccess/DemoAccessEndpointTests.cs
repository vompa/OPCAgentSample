using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Server.DemoAccess;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.DemoAccess;

/// <summary>Anmeldung, Sitzung und Zugangsschutz der Live-Demo gegen den vollständigen Server (Redis aus der Fixture).</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class DemoAccessEndpointTests(InfrastructureFixture infrastructure)
{
    internal const string AccessCode = "Test-Zugangscode-4711";
    internal const string CookieKey = "0123456789abcdef0123456789abcdef-test-key";

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        bool demo = true, IDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
            ["Redis:CommandTimeout"] = "00:00:01",
            ["Demo:StreamDelay"] = "00:00:00",
            ["Demo:Enabled"] = demo ? "true" : "false",
            ["Demo:AccessCodeSha256"] = ApiKeyHasher.Hash(AccessCode),
            ["Demo:CookieKey"] = CookieKey,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, string?>()) values[key] = value;

        await infrastructure.FlushRedisAsync();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).UseSetting("Opc:Enabled", "false")
                .ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(values)));
    }

    /// <summary>Client über „https“ (das Cookie ist nur dann Secure) ohne eigene Cookie-Verwaltung: Tests setzen die Köpfe selbst.</summary>
    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = false,
            AllowAutoRedirect = false,
        });

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string code, string? forwardedFor = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/demo/login")) { Content = JsonContent.Create(new { code }) };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return await client.SendAsync(request);
    }

    private static async Task<string> LoginForTokenAsync(HttpClient client)
    {
        using var response = await LoginAsync(client, AccessCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static HttpRequestMessage WithBearer(string path, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, Url(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage WithCookie(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
        request.Headers.Add("Cookie", $"{DemoSession.CookieName}={token}");
        return request;
    }

    private const string InviteKey = "Einladung-Anna-9f3k";

    private sealed class ListLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new ListLogger(this);

        public void Dispose()
        {
        }

        private sealed class ListLogger(ListLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Messages) owner.Messages.Add(formatter(state, exception));
            }
        }
    }

    private static Dictionary<string, string?> Invites(string value) =>
        new Dictionary<string, string?> { ["Demo:InviteCodes"] = value };

    [Fact]
    public async Task An_invite_key_logs_in_like_the_main_code_and_only_its_name_is_logged()
    {
        var logs = new ListLoggerProvider();
        using var factory = (await CreateFactoryAsync(extra: Invites($"anna={ApiKeyHasher.Hash(InviteKey)};ben={ApiKeyHasher.Hash("Einladung-Ben-1x2y")}")))
            .WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = CreateClient(factory);

        using var invited = await LoginAsync(client, InviteKey);
        using var main = await LoginAsync(client, AccessCode);

        Assert.Equal(HttpStatusCode.OK, invited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, main.StatusCode);
        Assert.Single(invited.Headers.GetValues("Set-Cookie"));
        var token = (await invited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        using var api = await client.SendAsync(WithBearer("/api/me?role=shift_lead", token));
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);

        string[] messages;
        lock (logs.Messages) messages = [.. logs.Messages];
        Assert.Contains(messages, m => m.Contains("Zugang „anna“", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Zugang „zugangscode“", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, m => m.Contains(InviteKey, StringComparison.Ordinal) || m.Contains(AccessCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_and_removed_invite_keys_get_401()
    {
        using var factory = await CreateFactoryAsync(extra: Invites($"ben={ApiKeyHasher.Hash("Einladung-Ben-1x2y")}"));
        using var client = CreateClient(factory);

        using var removed = await LoginAsync(client, InviteKey); // Annas Eintrag steht nicht mehr in der Liste
        using var other = await LoginAsync(client, "Einladung-Ben-1x2y");

        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        Assert.False(removed.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Theory]
    [InlineData("anna")]
    [InlineData("Anna=0123456789012345678901234567890123456789012345678901234567890123")]
    [InlineData("anna=zz")]
    [InlineData("anna=0123456789012345678901234567890123456789012345678901234567890123;anna=0123456789012345678901234567890123456789012345678901234567890123")]
    public async Task The_server_does_not_start_with_malformed_invite_codes(string value)
    {
        using var factory = await CreateFactoryAsync(extra: Invites(value));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Demo:InviteCodes", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_demo_nothing_changes_and_login_does_not_exist()
    {
        using var factory = await CreateFactoryAsync(demo: false);
        using var client = CreateClient(factory);

        using var me = await client.GetAsync(Url("/api/me?role=shift_lead"));
        using var session = await client.GetAsync(Url("/api/demo/session"));
        using var login = await LoginAsync(client, AccessCode);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var state = await session.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(state.GetProperty("enabled").GetBoolean());
        Assert.False(state.GetProperty("authenticated").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
    }

    [Theory]
    [InlineData("/api/me?role=shift_lead")]
    [InlineData("/api/overview?role=shift_lead")]
    [InlineData("/api/reports/summary")]
    [InlineData("/api/live?role=shift_lead")]
    [InlineData("/api/history/latest?role=shift_lead")]
    public async Task With_the_demo_on_the_api_answers_401_without_a_session(string path)
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(Url(path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(401, problem.GetProperty("status").GetInt32());
        Assert.Equal("Nicht angemeldet", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task With_the_demo_on_health_login_session_and_static_files_stay_open()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var health = await client.GetAsync(Url("/api/health"));
        using var session = await client.GetAsync(Url("/api/demo/session"));
        using var index = await client.GetAsync(Url("/index.html"));
        using var logout = await client.PostAsync(Url("/api/demo/logout"), content: null);

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var state = await session.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("enabled").GetBoolean());
        Assert.False(state.GetProperty("authenticated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("expiresAtUtc").ValueKind);
    }

    [Fact]
    public async Task The_mcp_endpoint_keeps_its_own_api_key_check_and_is_not_covered_by_the_session()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var response = await client.PostAsync(Url("/mcp"), new StringContent("{}"));

        Assert.DoesNotContain("Zugangscode", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_access_code_is_rejected_with_401_and_sets_no_cookie()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var response = await LoginAsync(client, "falsch");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Der Zugangscode ist nicht gültig.", problem.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("kein json")]
    [InlineData("{\"code\":\"\"}")]
    public async Task A_missing_or_malformed_code_is_rejected(string body)
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var response = await client.PostAsync(Url("/api/demo/login"), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_sets_a_strict_secure_http_only_cookie_and_returns_a_token_in_the_same_format()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var response = await LoginAsync(client, AccessCode);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        var expires = body.GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddHours(7.9), DateTimeOffset.UtcNow.AddHours(8.1));

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{DemoSession.CookieName}={token};", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.NotNull(DemoSession.Validate(CookieKey, token, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task The_session_hours_setting_controls_the_lifetime()
    {
        using var factory = await CreateFactoryAsync(extra: new Dictionary<string, string?> { ["Demo:SessionHours"] = "2" });
        using var client = CreateClient(factory);

        using var response = await LoginAsync(client, AccessCode);

        var expires = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("expiresAtUtc").GetDateTimeOffset();
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddHours(1.9), DateTimeOffset.UtcNow.AddHours(2.1));
        Assert.Contains("max-age=7200", string.Join(';', response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_and_bearer_token_both_open_the_api_and_the_session_endpoint_reports_the_state()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);
        var token = await LoginForTokenAsync(client);

        using var viaCookie = await client.SendAsync(WithCookie("/api/me?role=shift_lead", token));
        using var viaBearer = await client.SendAsync(WithBearer("/api/me?role=shift_lead", token));
        using var sessionState = await client.SendAsync(WithBearer("/api/demo/session", token));

        Assert.Equal(HttpStatusCode.OK, viaCookie.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaBearer.StatusCode);
        var state = await sessionState.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("authenticated").GetBoolean());
        Assert.True(state.GetProperty("expiresAtUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Forged_expired_and_foreign_tokens_get_401()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);
        var valid = await LoginForTokenAsync(client);
        var expired = DemoSession.Create(CookieKey, DateTimeOffset.UtcNow.AddMinutes(-1), out _);
        var foreign = DemoSession.Create(CookieKey + "-anders", DateTimeOffset.UtcNow.AddHours(1), out _);

        foreach (var bad in new[] { valid + "x", expired, foreign, "irgendwas" })
        {
            using var viaBearer = await client.SendAsync(WithBearer("/api/me?role=shift_lead", bad));
            using var viaCookie = await client.SendAsync(WithCookie("/api/me?role=shift_lead", bad));
            Assert.Equal(HttpStatusCode.Unauthorized, viaBearer.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, viaCookie.StatusCode);
        }
    }

    [Fact]
    public async Task Logout_deletes_the_cookie()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);

        using var response = await client.PostAsync(Url("/api/demo/logout"), content: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{DemoSession.CookieName}=;", cookie, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task After_five_failures_the_client_gets_429_with_retry_after_even_for_the_right_code()
    {
        using var factory = await CreateFactoryAsync(extra: new Dictionary<string, string?> { ["ForwardedHeaders:Enabled"] = "true" });
        using var client = CreateClient(factory);
        for (var i = 0; i < 5; i++)
        {
            using var failure = await LoginAsync(client, "falsch", forwardedFor: "198.51.100.7");
            Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
        }

        using var blocked = await LoginAsync(client, AccessCode, forwardedFor: "198.51.100.7");
        using var otherClient = await LoginAsync(client, AccessCode, forwardedFor: "198.51.100.8");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var retryAfter = int.Parse(Assert.Single(blocked.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 1, 600);
        var problem = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(retryAfter, problem.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(HttpStatusCode.OK, otherClient.StatusCode);
    }

    [Fact]
    public async Task Forwarded_headers_make_the_cookie_secure_behind_a_tls_proxy()
    {
        using var factory = await CreateFactoryAsync(extra: new Dictionary<string, string?> { ["ForwardedHeaders:Enabled"] = "true" });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/demo/login")) { Content = JsonContent.Create(new { code = AccessCode }) };
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.5");

        using var secure = await client.SendAsync(request);
        using var plain = await LoginAsync(client, AccessCode);

        Assert.Contains("secure", Assert.Single(secure.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", Assert.Single(plain.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_by_default()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/demo/login")) { Content = JsonContent.Create(new { code = AccessCode }) };
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        Assert.DoesNotContain("secure", Assert.Single(response.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Two_visitors_have_separate_history_even_in_the_same_role()
    {
        using var factory = await CreateFactoryAsync();
        using var client = CreateClient(factory);
        var first = await LoginForTokenAsync(client);
        var second = await LoginForTokenAsync(client);

        using var firstLatest = await client.SendAsync(WithBearer("/api/history/latest?role=shift_lead", first));
        using var secondLatest = await client.SendAsync(WithBearer("/api/history/latest?role=shift_lead", second));
        var firstId = (await firstLatest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetProperty("id").GetString();
        var secondId = (await secondLatest.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("session").GetProperty("id").GetString();
        using var foreign = await client.SendAsync(WithBearer($"/api/history/sessions/{firstId}?role=shift_lead", second));
        using var own = await client.SendAsync(WithBearer($"/api/history/sessions/{firstId}?role=shift_lead", first));
        using var otherRole = await client.SendAsync(WithBearer($"/api/history/sessions/{firstId}?role=hall_lead", first));

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherRole.StatusCode);
    }

    [Fact]
    public async Task The_live_connection_limit_counts_per_session()
    {
        using var factory = await CreateFactoryAsync(extra: new Dictionary<string, string?> { ["Live:MaxConnectionsPerOwner"] = "1" });
        using var client = CreateClient(factory);
        var first = await LoginForTokenAsync(client);
        var second = await LoginForTokenAsync(client);

        using var firstRequest = WithBearer("/api/live?role=shift_lead", first);
        using var firstStream = await client.SendAsync(firstRequest, HttpCompletionOption.ResponseHeadersRead);
        using var sameSessionAgain = await client.SendAsync(WithBearer("/api/live?role=shift_lead", first));
        using var otherSessionRequest = WithBearer("/api/live?role=shift_lead", second);
        using var otherSession = await client.SendAsync(otherSessionRequest, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, firstStream.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, sameSessionAgain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
    }

    [Theory]
    [InlineData("false", "", "")]
    [InlineData("true", "", "")]
    [InlineData("true", "kein-hash", "0123456789abcdef0123456789abcdef-test-key")]
    [InlineData("true", "x", "zu-kurz")]
    public async Task The_server_does_not_start_with_the_demo_on_and_missing_secrets(string enabled, string hash, string key)
    {
        if (enabled == "false")
        {
            // Ausgeschaltet darf alles fehlen: Der Server startet.
            using var off = await CreateFactoryAsync(demo: false, extra: new Dictionary<string, string?> { ["Demo:AccessCodeSha256"] = hash, ["Demo:CookieKey"] = key });
            using var offClient = CreateClient(off);
            using var ok = await offClient.GetAsync(Url("/api/health"));
            Assert.True(ok.IsSuccessStatusCode);
            return;
        }

        using var factory = await CreateFactoryAsync(extra: new Dictionary<string, string?> { ["Demo:AccessCodeSha256"] = hash, ["Demo:CookieKey"] = key });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Demo:", error.ToString(), StringComparison.Ordinal);
    }
}
