using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Api;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.DemoAccess;
using OPCAgentSample.Server.MasterData;
using OPCAgentSample.Server.Ingestion;
using OPCAgentSample.Server.Security;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.DemoAccess;

/// <summary>Reine Logik des Demo-Zugangs: Sitzungs-Signatur, Drossel, Pfadregeln, Optionen, Besitzer und Zeitbasis-Prüfung.</summary>
public sealed class DemoAccessUnitTests
{
    private const string CookieKey = "0123456789abcdef0123456789abcdef-key";
    private static readonly DateTimeOffset s_now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    // --- Sitzung ---

    [Fact]
    public void Session_token_is_valid_until_it_expires()
    {
        var token = DemoSession.Create(CookieKey, s_now.AddHours(8), out var id);

        var session = DemoSession.Validate(CookieKey, token, s_now.AddHours(7));

        Assert.NotNull(session);
        Assert.Equal(id, session.Id);
        Assert.Equal(s_now.AddHours(8), session.ExpiresAtUtc);
        Assert.Null(DemoSession.Validate(CookieKey, token, s_now.AddHours(8)));
        Assert.Null(DemoSession.Validate(CookieKey, token, s_now.AddHours(9)));
    }

    [Fact]
    public void Session_token_with_wrong_key_changed_payload_or_garbage_is_rejected()
    {
        var token = DemoSession.Create(CookieKey, s_now.AddHours(8), out _);
        var parts = token.Split('.');
        var forgedExpiry = $"{parts[0]}.{long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 3600}.{parts[2]}";
        var otherSession = $"andere.{parts[1]}.{parts[2]}";

        Assert.Null(DemoSession.Validate(CookieKey + "x", token, s_now));
        Assert.Null(DemoSession.Validate(CookieKey, forgedExpiry, s_now));
        Assert.Null(DemoSession.Validate(CookieKey, otherSession, s_now));
        Assert.Null(DemoSession.Validate(CookieKey, null, s_now));
        Assert.Null(DemoSession.Validate(CookieKey, string.Empty, s_now));
        Assert.Null(DemoSession.Validate(CookieKey, "a.b", s_now));
        Assert.Null(DemoSession.Validate(CookieKey, "a.-1.c", s_now));
        Assert.Null(DemoSession.Validate(CookieKey, new string('x', 500), s_now));
        Assert.Null(DemoSession.Validate(string.Empty, token, s_now));
    }

    [Fact]
    public void Every_login_gets_a_new_session_id()
    {
        _ = DemoSession.Create(CookieKey, s_now.AddHours(1), out var first);
        _ = DemoSession.Create(CookieKey, s_now.AddHours(1), out var second);

        Assert.NotEqual(first, second);
    }

    // --- Drossel ---

    [Fact]
    public void Throttle_blocks_a_client_after_five_failures_for_ten_minutes()
    {
        var time = new ManualTimeProvider(s_now);
        var throttle = new DemoLoginThrottle(time);
        for (var i = 0; i < DemoLoginThrottle.MaxFailuresPerClient - 1; i++) throttle.RecordFailure("1.1.1.1");
        Assert.False(throttle.IsBlocked("1.1.1.1", out _));

        throttle.RecordFailure("1.1.1.1");
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.True(throttle.IsBlocked("1.1.1.1", out var wait));
        Assert.InRange(wait, TimeSpan.FromMinutes(5.9), TimeSpan.FromMinutes(6));
        Assert.False(throttle.IsBlocked("2.2.2.2", out _));

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.False(throttle.IsBlocked("1.1.1.1", out _));
    }

    [Fact]
    public void Throttle_blocks_everyone_after_forty_failures_in_total()
    {
        var time = new ManualTimeProvider(s_now);
        var throttle = new DemoLoginThrottle(time);
        for (var i = 0; i < DemoLoginThrottle.MaxFailuresTotal; i++) throttle.RecordFailure($"10.0.0.{i}");

        Assert.True(throttle.IsBlocked("203.0.113.9", out var wait));
        Assert.True(wait > TimeSpan.Zero);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.False(throttle.IsBlocked("203.0.113.9", out _));
    }

    // --- Pfadregeln der Middleware ---

    [Theory]
    [InlineData("/api/me", true)]
    [InlineData("/api/overview", true)]
    [InlineData("/api/ask", true)]
    [InlineData("/api/live", true)]
    [InlineData("/api/reports/summary", true)]
    [InlineData("/API/Me", true)]
    [InlineData("/api/healthz", true)]
    [InlineData("/api/health/x", true)]
    [InlineData("/api/health", false)]
    [InlineData("/api/health/", false)]
    [InlineData("/api/demo/login", false)]
    [InlineData("/api/demo/session", false)]
    [InlineData("/api/demo/logout", false)]
    [InlineData("/mcp", false)]
    [InlineData("/", false)]
    [InlineData("/index.html", false)]
    [InlineData("/assets/index-abc.js", false)]
    [InlineData("/sw.js", false)]
    [InlineData("/manifest.webmanifest", false)]
    public void Only_api_paths_require_a_session(string path, bool expected) =>
        Assert.Equal(expected, DemoAccessRules.RequiresSession(new PathString(path)));

    // --- Optionen ---

    [Fact]
    public void Options_are_not_checked_while_the_demo_is_off()
    {
        Assert.Null(new DemoOptions { Enabled = false, CookieKey = "kurz" }.Validate());
    }

    [Fact]
    public void Options_with_the_demo_on_need_hash_key_and_a_sane_lifetime()
    {
        var valid = new DemoOptions { Enabled = true, AccessCodeSha256 = ApiKeyHasher.Hash("code"), CookieKey = CookieKey };

        Assert.Null(valid.Validate());
        Assert.Contains("AccessCodeSha256", new DemoOptions { Enabled = true, CookieKey = CookieKey }.Validate(), StringComparison.Ordinal);
        Assert.Contains("AccessCodeSha256", new DemoOptions { Enabled = true, AccessCodeSha256 = "zz", CookieKey = CookieKey }.Validate(), StringComparison.Ordinal);
        Assert.Contains("CookieKey", new DemoOptions { Enabled = true, AccessCodeSha256 = ApiKeyHasher.Hash("c"), CookieKey = "zu-kurz" }.Validate(), StringComparison.Ordinal);
        Assert.Contains("SessionHours", new DemoOptions { Enabled = true, AccessCodeSha256 = ApiKeyHasher.Hash("c"), CookieKey = CookieKey, SessionHours = 0 }.Validate(), StringComparison.Ordinal);
        Assert.True(new DemoOptionsValidator().Validate(null, new DemoOptions { Enabled = true }).Failed);
        Assert.True(new DemoOptionsValidator().Validate(null, valid).Succeeded);
    }

    [Fact]
    public void Access_code_is_compared_by_its_sha256()
    {
        var options = new DemoOptions { Enabled = true, AccessCodeSha256 = ApiKeyHasher.Hash("Geheimer-Code-42"), CookieKey = CookieKey };

        Assert.True(DemoAccessEndpoints.CodeMatches(options, "Geheimer-Code-42"));
        Assert.False(DemoAccessEndpoints.CodeMatches(options, "geheimer-code-42"));
        Assert.False(DemoAccessEndpoints.CodeMatches(options, string.Empty));
        Assert.False(DemoAccessEndpoints.CodeMatches(new DemoOptions { AccessCodeSha256 = "kaputt" }, "Geheimer-Code-42"));
    }

    // --- Einladungs-Keys ---

    private static readonly string s_hashA = ApiKeyHasher.Hash("key-a");
    private static readonly string s_hashB = ApiKeyHasher.Hash("key-b");

    [Fact]
    public void Invite_codes_are_parsed_from_name_hash_pairs()
    {
        Assert.True(DemoInviteCodes.TryParse(null, out var none, out _));
        Assert.True(DemoInviteCodes.TryParse("  ", out var blank, out _));
        Assert.True(DemoInviteCodes.TryParse($" anna-1 = {s_hashA} ; ben={s_hashB};", out var codes, out var error));

        Assert.Empty(none);
        Assert.Empty(blank);
        Assert.Null(error);
        Assert.Equal(["anna-1", "ben"], codes.Select(code => code.Name));
        Assert.Equal("anna-1", DemoInviteCodes.Find(codes, "key-a"));
        Assert.Equal("ben", DemoInviteCodes.Find(codes, "key-b"));
        Assert.Null(DemoInviteCodes.Find(codes, "key-c"));
        Assert.Null(DemoInviteCodes.Find(codes, string.Empty));
    }

    [Theory]
    [InlineData("anna")]
    [InlineData("=HASH")]
    [InlineData("Anna=HASH")]
    [InlineData("an na=HASH")]
    [InlineData("anna=HASH;anna=HASH")]
    [InlineData("anna=zz")]
    [InlineData("anna=")]
    public void Malformed_invite_codes_are_rejected_with_the_setting_name(string setting)
    {
        var text = setting.Replace("HASH", s_hashA, StringComparison.Ordinal);

        Assert.False(DemoInviteCodes.TryParse(text, out var codes, out var error));
        Assert.Empty(codes);
        Assert.Contains("Demo:InviteCodes", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_name_may_have_at_most_40_characters()
    {
        Assert.True(DemoInviteCodes.TryParse($"{new string('a', 40)}={s_hashA}", out _, out _));
        Assert.False(DemoInviteCodes.TryParse($"{new string('a', 41)}={s_hashA}", out _, out _));
    }

    [Fact]
    public void Options_validate_invite_codes_only_with_the_demo_on_and_find_the_access()
    {
        var valid = new DemoOptions
        {
            Enabled = true,
            AccessCodeSha256 = ApiKeyHasher.Hash("haupt"),
            CookieKey = CookieKey,
            InviteCodes = $"anna={s_hashA}",
        };

        Assert.Null(valid.Validate());
        Assert.Equal("zugangscode", DemoAccessEndpoints.FindAccess(valid, "haupt"));
        Assert.Equal("anna", DemoAccessEndpoints.FindAccess(valid, "key-a"));
        Assert.Null(DemoAccessEndpoints.FindAccess(valid, "key-b"));
        Assert.Contains("Demo:InviteCodes", new DemoOptions { Enabled = true, AccessCodeSha256 = ApiKeyHasher.Hash("c"), CookieKey = CookieKey, InviteCodes = "kaputt" }.Validate(), StringComparison.Ordinal);
        Assert.Null(new DemoOptions { Enabled = false, InviteCodes = "kaputt" }.Validate());
        Assert.Empty(new DemoOptions { InviteCodes = "kaputt" }.ParseInviteCodes());
    }

    // --- Besitzer des Verlaufs ---

    private static (DemoSessionChatOwnerResolver Resolver, string Token) ResolverWithSession(out DemoSessionReader reader)
    {
        var options = Options.Create(new DemoOptions { Enabled = true, AccessCodeSha256 = ApiKeyHasher.Hash("c"), CookieKey = CookieKey });
        reader = new DemoSessionReader(options, new ManualTimeProvider(s_now));
        var token = DemoSession.Create(CookieKey, s_now.AddHours(8), out _);
        return (new DemoSessionChatOwnerResolver(reader, options), token);
    }

    private static DefaultHttpContext BearerRequest(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = $"Bearer {token}";
        return context;
    }

    private static AppUser User(string id) => new() { Id = id, DisplayName = id, RoleId = "shift_lead" };

    [Fact]
    public void Owner_is_a_browser_token_per_session_and_user()
    {
        var (resolver, token) = ResolverWithSession(out _);
        var otherToken = DemoSession.Create(CookieKey, s_now.AddHours(8), out _);

        var owner = resolver.Resolve(BearerRequest(token), User("martin.huber"));
        var sameAgain = resolver.Resolve(BearerRequest(token), User("martin.huber"));
        var otherRole = resolver.Resolve(BearerRequest(token), User("katharina.gruber"));
        var otherVisitor = resolver.Resolve(BearerRequest(otherToken), User("martin.huber"));

        Assert.StartsWith("browser:", owner.Key, StringComparison.Ordinal);
        Assert.Equal(owner.Key, sameAgain.Key);
        Assert.NotEqual(owner.Key, otherRole.Key);
        Assert.NotEqual(owner.Key, otherVisitor.Key);
        Assert.DoesNotContain(token, owner.Key, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_demo_the_owner_stays_the_local_user()
    {
        var options = Options.Create(new DemoOptions { Enabled = false });
        var resolver = new DemoSessionChatOwnerResolver(new DemoSessionReader(options, new ManualTimeProvider(s_now)), options);

        var owner = resolver.Resolve(new DefaultHttpContext(), User("martin.huber"));

        Assert.Equal("user:martin.huber", owner.Key);
    }

    [Fact]
    public void Owner_is_read_from_the_cookie_as_well()
    {
        var (resolver, token) = ResolverWithSession(out _);
        var viaCookie = new DefaultHttpContext();
        viaCookie.Request.Headers.Cookie = $"{DemoSession.CookieName}={token}";

        Assert.Equal(
            resolver.Resolve(BearerRequest(token), User("martin.huber")).Key,
            resolver.Resolve(viaCookie, User("martin.huber")).Key);
    }

    [Fact]
    public void Owner_without_a_valid_session_is_never_the_owner_of_a_real_session()
    {
        var (resolver, token) = ResolverWithSession(out _);

        var anonymous = resolver.Resolve(new DefaultHttpContext(), User("martin.huber"));
        var forged = resolver.Resolve(BearerRequest(token + "x"), User("martin.huber"));

        Assert.Equal(anonymous.Key, forged.Key);
        Assert.NotEqual(anonymous.Key, resolver.Resolve(BearerRequest(token), User("martin.huber")).Key);
    }

    // --- Zeitbasis: Simulationsuhr gegen Wasserstand ---

    [Theory]
    [InlineData(0, false)]
    [InlineData(-30, false)]
    [InlineData(-59, false)]
    [InlineData(-61, true)]
    [InlineData(-600, true)]
    [InlineData(3600, false)]
    public void Clock_behind_the_watermark_is_detected_beyond_the_tolerance(int clockOffsetSeconds, bool expected)
    {
        var watermark = SimulationClock.DemoStart;
        var clock = watermark.AddSeconds(clockOffsetSeconds);

        Assert.Equal(expected, DataWatermarkCheck.IsClockBehind(clock, watermark));
    }

    [Fact]
    public void Without_a_watermark_there_is_nothing_to_warn_about()
    {
        Assert.False(DataWatermarkCheck.IsClockBehind(SimulationClock.DemoStart, null));
    }

    [Fact]
    public void Warning_text_across_days_names_both_dates()
    {
        var text = DataWatermarkCheck.WarningText(SimulationClock.DemoStart.AddDays(-3), SimulationClock.DemoStart);

        Assert.Contains("12.10.2026", text, StringComparison.Ordinal);
        Assert.Contains("15.10.2026", text, StringComparison.Ordinal);
        Assert.Contains("Simulator", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Warning_text_on_the_same_day_is_short_and_names_only_the_times()
    {
        var text = DataWatermarkCheck.WarningText(SimulationClock.DemoStart, SimulationClock.DemoStart.AddMinutes(7));

        Assert.Contains("(09:49)", text, StringComparison.Ordinal);
        Assert.Contains("(09:42)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
        Assert.True(text.Length <= 120, text);
    }
}
