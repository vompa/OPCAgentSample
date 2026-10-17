using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using OPCAgentSample.Tests.Server;

namespace OPCAgentSample.Tests.Web;

/// <summary>HTTP-API der Weboberfläche gegen die Testcontainers-Fixture (Redis).</summary>
[Collection(InfrastructureFixture.Name)]
public sealed class WebApiTests(InfrastructureFixture infrastructure)
{
    private static readonly string[] s_escalatedLevels = ["warn", "crit"];

    private const string FavoriteQuestion = "Was muss ich von der Nachtschicht wissen?";

    /// <param name="withHistory">Redis mit der aufgenommenen Historie bis zur Demo-Uhr (Kopie der Vorlage) statt eines leeren.</param>
    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(bool withHistory = false)
    {
        if (withHistory) await infrastructure.RestoreIngestedRedisAsync();
        else await infrastructure.FlushRedisAsync();
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).UseSetting("Opc:Enabled", "false").ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
                    ["Redis:CommandTimeout"] = "00:00:01",
                    ["Demo:StreamDelay"] = "00:00:00",
                })));
    }

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(Url(path));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<(string Name, JsonElement Data)>> AskAsync(
        HttpClient client, string role, string question, Guid? sessionId = null)
    {
        using var response = await client.PostAsJsonAsync(Url("/api/ask"), new { role, question, sessionId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<(string, JsonElement)>();
        foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..];
            events.Add((name, JsonDocument.Parse(data).RootElement.Clone()));
        }

        return events;
    }

    [Fact]
    public async Task Me_returns_user_roles_clock_and_counters_of_the_role()
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();

        var me = await GetJsonAsync(client, "/api/me?role=hall_lead");

        Assert.Equal("Katharina Gruber", me.GetProperty("user").GetProperty("displayName").GetString());
        Assert.Equal(3, me.GetProperty("roles").GetArrayLength());
        Assert.Equal("2026-10-15T07:42:00+00:00", me.GetProperty("clock").GetProperty("utcNow").GetString());
        Assert.Equal("09:42", me.GetProperty("clock").GetProperty("localTime").GetString());
        Assert.True(me.GetProperty("status").GetProperty("opcConnected").GetBoolean());
        Assert.Equal(4, me.GetProperty("counters").GetProperty("inboxOpen").GetInt32());
    }

    [Fact]
    public async Task Unknown_role_is_rejected()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Url("/api/me?role=gast"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Overview_counts_stations_and_shows_halls_for_the_production_lead()
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();

        var hall = await GetJsonAsync(client, "/api/overview?role=shift_lead");
        var plant = await GetJsonAsync(client, "/api/overview?role=production_lead");

        Assert.Equal("hall", hall.GetProperty("scope").GetString());
        Assert.Equal(1, hall.GetProperty("counts").GetProperty("problem").GetInt32());
        Assert.Equal(5, hall.GetProperty("counts").GetProperty("warning").GetInt32());
        Assert.Equal(2, hall.GetProperty("counts").GetProperty("running").GetInt32());
        Assert.Equal(3, hall.GetProperty("groups").GetArrayLength());
        Assert.Equal("plant", plant.GetProperty("scope").GetString());
        Assert.Equal("Hallen", plant.GetProperty("groups")[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, plant.GetProperty("climate").ValueKind);

        var halls = plant.GetProperty("groups")[0].GetProperty("stations").EnumerateArray().ToList();
        Assert.Equal(["Halle A", "Halle B", "Halle C"], halls.Select(h => h.GetProperty("id").GetString()));
        Assert.All(halls, h => Assert.Equal("OEE KW 42", h.GetProperty("kpi").GetProperty("unit").GetString()));
        Assert.Equal("77 %", halls[0].GetProperty("kpi").GetProperty("value").GetString());
        Assert.Equal("81 %", halls[1].GetProperty("kpi").GetProperty("value").GetString());
        Assert.Equal("80 %", halls[2].GetProperty("kpi").GetProperty("value").GetString());
    }

    [Fact]
    public async Task Overview_marks_halls_without_figures_as_no_data_and_does_not_count_them_as_running()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var plant = await GetJsonAsync(client, "/api/overview?role=production_lead");

        var halls = plant.GetProperty("groups")[0].GetProperty("stations").EnumerateArray()
            .Where(h => h.GetProperty("status").GetString() == "nodata").ToList();
        Assert.NotEmpty(halls);
        Assert.All(halls, h => Assert.Equal("Keine Daten", h.GetProperty("statusWord").GetString()));
        var counts = plant.GetProperty("counts");
        Assert.Equal(halls.Count, counts.GetProperty("noData").GetInt32());
        Assert.Equal(0, counts.GetProperty("running").GetInt32());
    }

    [Theory]
    [InlineData("RT-01", "101 %", "warning")]
    [InlineData("RT-02", "97 %", "warning")]
    [InlineData("RT-03", "72 %", "problem")]
    [InlineData("RT-04", "89 %", "warning")]
    [InlineData("OF-01", "78 %", "running")]
    [InlineData("OF-02", "96 %", "warning")]
    [InlineData("VP-01", "98 %", "running")]
    [InlineData("VP-02", "64 %", "warning")]
    public async Task Overview_shows_the_start_state_of_the_specification_per_station(string station, string value, string status)
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();

        var hall = await GetJsonAsync(client, "/api/overview?role=shift_lead");

        var info = hall.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("stations").EnumerateArray())
            .Single(s => s.GetProperty("id").GetString() == station);
        Assert.Equal(value, info.GetProperty("kpi").GetProperty("value").GetString());
        Assert.Equal(status, info.GetProperty("status").GetString());
        Assert.Equal("23,4 °C", hall.GetProperty("climate").GetProperty("temperature").GetString());
        Assert.Equal("48 % rF", hall.GetProperty("climate").GetProperty("humidity").GetString());
        Assert.Equal("running", hall.GetProperty("climate").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Overview_texts_name_the_cause_of_each_station()
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();

        var hall = await GetJsonAsync(client, "/api/overview?role=shift_lead");

        var texts = hall.GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("stations").EnumerateArray())
            .ToDictionary(s => s.GetProperty("id").GetString()!, s => (Article: s.GetProperty("article").GetString(), Message: s.GetProperty("message").GetString()));
        Assert.Equal("Ø 125 · A60", texts["RT-01"].Article);
        Assert.StartsWith("Mischung A60 noch 38 min", texts["RT-01"].Message, StringComparison.Ordinal);
        Assert.Equal("Ø 125 · A60", texts["RT-02"].Article);
        Assert.Contains("Form 94 %", texts["RT-02"].Message, StringComparison.Ordinal);
        Assert.Equal(("Ø 125 · A46", "Rückstand nach Störung"), texts["RT-03"]);
        Assert.Equal(("Ø 180 · A30", "Taktzeit +9 %"), texts["RT-04"]);
        Assert.Equal(("Ø 125 · 25er-Karton", "1.240 Kartons seit 06:00"), texts["VP-01"]);
        Assert.Equal(("Ø 180 · 10er-Karton", "Wartet auf Ofencharge"), texts["VP-02"]);
        Assert.StartsWith("Charge ", texts["OF-01"].Article, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Favorites_start_with_five_defaults_and_accept_at_most_ten()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var initial = await GetJsonAsync(client, "/api/favorites?role=shift_lead");
        Assert.Equal(5, initial.GetProperty("items").GetArrayLength());
        Assert.Equal(10, initial.GetProperty("max").GetInt32());

        var ten = Enumerable.Range(1, 10).Select(i => new { shortTitle = $"Frage {i}", question = $"Wie geht es Station {i}?" });
        using var accepted = await client.PutAsJsonAsync(Url("/api/favorites?role=shift_lead"), new { items = ten });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var eleven = Enumerable.Range(1, 11).Select(i => new { shortTitle = $"Frage {i}", question = $"Wie geht es Station {i}?" });
        using var rejected = await client.PutAsJsonAsync(Url("/api/favorites?role=shift_lead"), new { items = eleven });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var stored = await GetJsonAsync(client, "/api/favorites?role=shift_lead");
        Assert.Equal(10, stored.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Favorites_reject_a_short_title_longer_than_22_characters()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            Url("/api/favorites?role=shift_lead"), new { items = new[] { new { shortTitle = new string('x', 23), question = "Frage?" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Favorites_reset_restores_the_defaults_and_marks_them()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();
        using var changed = await client.PutAsJsonAsync(
            Url("/api/favorites?role=hall_lead"), new { items = new[] { new { shortTitle = "Eigene", question = "Eigene Frage?" } } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        using var reset = await client.PostAsync(Url("/api/favorites/reset?role=hall_lead"), content: null);
        var restored = await reset.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var items = restored.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(5, items.Count);
        Assert.All(items, item => Assert.True(item.GetProperty("isDefault").GetBoolean()));
        Assert.Equal("Tagesbesprechung", items[0].GetProperty("shortTitle").GetString());
    }

    [Fact]
    public async Task Ask_streams_tool_calls_then_answer_then_done_and_saves_the_turn()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var events = await AskAsync(client, "shift_lead", FavoriteQuestion);

        var names = events.Select(e => e.Name).ToList();
        Assert.Equal(["tool_call", "tool_call", "tool_call", "tool_call", "tool_call", "answer", "done"], names);
        Assert.Equal("get_shift_summary", events[0].Data.GetProperty("tool").GetString());
        var card = events[5].Data;
        Assert.StartsWith("Nachtschicht mit 92 %", card.GetProperty("headline").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, events[6].Data.GetProperty("sequence").GetInt32());

        var latest = await GetJsonAsync(client, "/api/history/latest?role=shift_lead");
        var turns = latest.GetProperty("turns");
        Assert.Equal(1, turns.GetArrayLength());
        Assert.Equal(FavoriteQuestion, turns[0].GetProperty("question").GetString());
        Assert.Equal(5, turns[0].GetProperty("toolCalls").GetArrayLength());
        var recent = await GetJsonAsync(client, "/api/history/recent-questions?role=shift_lead");
        Assert.Equal(FavoriteQuestion, recent[0].GetString());
    }

    [Fact]
    public async Task Ask_matches_the_short_title_and_answers_unknown_questions_honestly()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var byTitle = await AskAsync(client, "shift_lead", "Hallenlage");
        var unknown = await AskAsync(client, "shift_lead", "Wie wird das Wetter morgen?");

        Assert.Contains(byTitle, e => e.Name == "answer" && e.Data.GetProperty("headline").GetString()!.StartsWith("Die Halle läuft", StringComparison.Ordinal));
        Assert.Equal(["answer", "done"], unknown.Select(e => e.Name));
        Assert.Equal(
            "Diese Frage beantwortet die Demo erst mit angebundenem Claude.", unknown[0].Data.GetProperty("headline").GetString());
    }

    [Fact]
    public async Task Ask_rejects_an_empty_question()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(Url("/api/ask"), new { role = "shift_lead", question = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task First_visit_shows_the_sample_history_and_new_session_starts_empty()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        var first = await GetJsonAsync(client, "/api/history/latest?role=production_lead");
        Assert.Equal(1, first.GetProperty("turns").GetArrayLength());
        Assert.Equal("Fass mir die Woche zusammen.", first.GetProperty("turns")[0].GetProperty("question").GetString());

        using var created = await client.PostAsync(Url("/api/history/sessions?role=production_lead"), content: null);
        var session = await created.Content.ReadFromJsonAsync<JsonElement>();
        await AskAsync(client, "production_lead", "Fass mir die Woche zusammen.");

        var sessions = await GetJsonAsync(client, "/api/history/sessions?role=production_lead");
        Assert.Equal(2, sessions.GetArrayLength());
        var id = session.GetProperty("id").GetString();
        var loaded = await GetJsonAsync(client, $"/api/history/sessions/{id}?role=production_lead");
        Assert.Equal(1, loaded.GetProperty("turns").GetArrayLength());
    }

    [Fact]
    public async Task Sessions_of_one_role_are_invisible_to_another()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();
        var events = await AskAsync(client, "shift_lead", FavoriteQuestion);
        var sessionId = events[^1].Data.GetProperty("sessionId").GetString();

        using var foreign = await client.GetAsync(Url($"/api/history/sessions/{sessionId}?role=hall_lead"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Ask_with_unknown_or_foreign_session_returns_session_not_found_and_saves_nothing()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();
        var events = await AskAsync(client, "shift_lead", FavoriteQuestion);
        var foreignId = events[^1].Data.GetProperty("sessionId").GetGuid();

        foreach (var (role, sessionId) in new[] { ("hall_lead", foreignId), ("shift_lead", Guid.NewGuid()) })
        {
            using var response = await client.PostAsJsonAsync(Url("/api/ask"), new { role, question = FavoriteQuestion, sessionId });
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("session-not-found", problem.GetProperty("code").GetString());
            Assert.Contains("Sitzung", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }

        var own = await GetJsonAsync(client, $"/api/history/sessions/{foreignId}?role=shift_lead");
        Assert.Equal(1, own.GetProperty("turns").GetArrayLength()); // in die fremde Sitzung wurde nichts geschrieben
    }

    [Theory]
    [InlineData("shift_lead", 7)]
    [InlineData("hall_lead", 4)]
    [InlineData("production_lead", 0)]
    public async Task Feed_is_filtered_by_role(string role, int expected)
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();

        var feed = await GetJsonAsync(client, $"/api/feed?role={role}");

        var items = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(expected, items.Count);
        if (role != "shift_lead")
            Assert.All(items, item => Assert.Contains(item.GetProperty("level").GetString(), s_escalatedLevels));
    }

    [Fact]
    public async Task Feed_lists_the_alarms_of_the_production_day_newest_first_with_station_and_kind()
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();

        var feed = await GetJsonAsync(client, "/api/feed?role=shift_lead");

        var items = feed.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(
            ["09:41", "09:38", "09:30", "09:20", "09:12", "09:05", "07:05"], items.Select(i => i.GetProperty("time").GetString()));
        Assert.Equal("RT-04 · Zustandswarnung", items[0].GetProperty("source").GetString());
        Assert.Equal("Presskraftaufbau 1,1 s (Soll 0,8 s)", items[0].GetProperty("text").GetString()!.Replace("Zustandswarnung ", string.Empty, StringComparison.Ordinal));
        Assert.Equal("RT-01 · Material", items[1].GetProperty("source").GetString());
        Assert.Equal("warn", items[1].GetProperty("level").GetString());
        Assert.Equal("OF-02 · Belegung", items[3].GetProperty("source").GetString());
        Assert.Equal("info", items[^1].GetProperty("level").GetString());
    }

    [Fact]
    public async Task Taking_an_inbox_item_records_name_and_time()
    {
        using var factory = await CreateFactoryAsync(withHistory: true);
        using var client = factory.CreateClient();
        var inbox = await GetJsonAsync(client, "/api/inbox?role=shift_lead");
        var id = inbox[0].GetProperty("id").GetString();

        using var response = await client.PostAsync(Url($"/api/inbox/{id}/take?role=shift_lead"), content: null);
        var taken = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Martin Huber", taken.GetProperty("takenBy").GetString());
        Assert.Equal("09:42", taken.GetProperty("takenAtText").GetString());
        var me = await GetJsonAsync(client, "/api/me?role=shift_lead");
        Assert.Equal(2, me.GetProperty("counters").GetProperty("inboxOpen").GetInt32());

        using var unknown = await client.PostAsync(Url("/api/inbox/gibt-es-nicht/take?role=shift_lead"), content: null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Root_serves_the_web_ui_with_a_content_security_policy()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Url("/"));
        var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));

        // Die Sicherheitsköpfe gelten unabhängig vom Frontend-Build.
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("googleapis", csp, StringComparison.Ordinal);

        // Das Ergebnis von „npm run build“ (src/OPCAgentSample.Web) liegt in wwwroot; die CI baut es vor den Tests.
        if (response.StatusCode == HttpStatusCode.NotFound)
            Assert.Fail("wwwroot/index.html fehlt: erst die Weboberfläche bauen (cd src/OPCAgentSample.Web && npm ci && npm run build).");

        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("KI-Leitstand", html, StringComparison.Ordinal);
        Assert.DoesNotContain(" onclick=", html, StringComparison.OrdinalIgnoreCase);
        // Strenge CSP: keine Inline-Skripte (jedes script-Element verweist auf eine Datei).
        Assert.DoesNotMatch(@"<script(?![^>]*\ssrc=)", html);
    }

    [Fact]
    public async Task Web_ui_ships_a_pwa_manifest_and_service_worker()
    {
        using var factory = await CreateFactoryAsync();
        using var client = factory.CreateClient();

        using var manifest = await client.GetAsync(Url("/manifest.webmanifest"));
        using var worker = await client.GetAsync(Url("/sw.js"));

        if (manifest.StatusCode == HttpStatusCode.NotFound)
            Assert.Fail("PWA-Dateien fehlen in wwwroot: erst die Weboberfläche bauen (npm run build in src/OPCAgentSample.Web).");

        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, worker.StatusCode);
        Assert.Contains("standalone", await manifest.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
