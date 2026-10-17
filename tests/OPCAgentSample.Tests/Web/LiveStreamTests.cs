using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OPCAgentSample.Domain;
using OPCAgentSample.Server.Demo;
using OPCAgentSample.Server.Infrastructure.Redis;
using OPCAgentSample.Server.Live;
using OPCAgentSample.Tests.Server;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Web;

/// <summary>
/// Live-Strom <c>GET /api/live</c> (Server-Sent Events) gegen Redis aus der Fixture. Die Tests warten auf
/// sichtbare Ergebnisse mit großzügigen Zeitgrenzen und prüfen keine Laufzeiten.
/// </summary>
[Collection(InfrastructureFixture.Name)]
public sealed class LiveStreamTests(InfrastructureFixture infrastructure)
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    /// <param name="shareRedis">Teilt Redis mit einer bereits laufenden Instanz (zweite Instanz); sonst wird Redis neu befüllt bzw. geleert.</param>
    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(
        bool withHistory = false, bool shareRedis = false, IDictionary<string, string?>? settings = null)
    {
        if (!shareRedis)
        {
            if (withHistory) await infrastructure.RestoreIngestedRedisAsync();
            else await infrastructure.FlushRedisAsync();
        }

        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Redis"] = infrastructure.RedisConnectionString,
            ["Redis:CommandTimeout"] = "00:00:01",
            ["Demo:StreamDelay"] = "00:00:00",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) configuration[key] = value;

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(TestEnvironment.Name).UseSetting("Opc:Enabled", "false").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration)));
        return factory;
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Trägt eine Meldung nach dem Live-Pfad-Muster in den Stream des Produktionstags ein und meldet die Änderung.</summary>
    private static async Task<string> AddFeedEntryAsync(IServiceProvider services, int minutesAfterNow, string level, string text)
    {
        var clock = services.GetRequiredService<IDemoClock>();
        var key = RedisKeys.Events(PlantLayout.HallA, services.GetRequiredService<ShiftCalendar>().DayPeriodOf(clock.UtcNow));
        var id = string.Create(CultureInfo.InvariantCulture, $"{clock.UtcNow.AddMinutes(minutesAfterNow).ToUnixTimeMilliseconds()}-0");
        await services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().StreamAddAsync(key,
        [
            new NameValueEntry("time", "09:50"),
            new NameValueEntry("source", "RT-01 · Test"),
            new NameValueEntry("text", text),
            new NameValueEntry("level", level),
        ], messageId: id);
        await services.GetRequiredService<ILiveNotifier>().NotifyAsync(LiveChangeKind.Feed);
        return id;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        using var cancellation = new CancellationTokenSource(s_timeout);
        while (!condition())
        {
            if (cancellation.IsCancellationRequested) throw new TimeoutException($"Nicht eingetreten: {description}");

            await Task.Delay(25);
        }
    }

    [Fact]
    public async Task Stream_sends_retry_then_station_alarm_inbox_and_clock_as_documented()
    {
        var factory = await CreateFactoryAsync(withHistory: true);
        using var _f = factory;
        using var http = factory.CreateClient();

        await using var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("text/event-stream", live.ContentType);
        Assert.Contains("no-cache", live.CacheControl, StringComparison.Ordinal);
        await live.WaitForAsync(f => f.Event == "clock");
        var frames = live.Frames;
        Assert.Equal(3000, frames[0].Retry);
        Assert.Equal(["station", "alarm", "inbox", "clock"], frames.Where(f => f.Event is not null).Select(f => f.Event!).ToArray());
        Assert.All(frames.Where(f => f.Event is not null), f => Assert.Null(f.Id));

        var station = frames.Single(f => f.Event == "station").Json;
        var overview = await GetJsonAsync(http, "/api/overview?role=shift_lead");
        Assert.Equal("hall", station.GetProperty("scope").GetString());
        Assert.Equal(overview.GetProperty("groups").GetArrayLength(), station.GetProperty("groups").GetArrayLength());
        Assert.Equal(overview.GetProperty("counts").GetProperty("problem").GetInt32(), station.GetProperty("counts").GetProperty("problem").GetInt32());

        var inbox = frames.Single(f => f.Event == "inbox").Json;
        Assert.Equal(3, inbox.GetProperty("open").GetInt32());
        Assert.True(inbox.GetProperty("hasUrgent").GetBoolean());
        Assert.Equal("09:42", frames.Single(f => f.Event == "clock").Json.GetProperty("localTime").GetString());
        Assert.True(frames.Single(f => f.Event == "alarm").Json.TryGetProperty("count", out _));
    }

    [Fact]
    public async Task Heartbeat_comments_arrive_while_nothing_changes()
    {
        var factory = await CreateFactoryAsync(settings: new Dictionary<string, string?> { ["Live:HeartbeatInterval"] = "00:00:00.200" });
        using var _f = factory;
        using var http = factory.CreateClient();

        await using var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await live.WaitForAsync(f => f.Comment == "heartbeat");
        await live.WaitForAsync(f => f.Comment == "heartbeat", skip: 1);

        Assert.True(live.Frames.Count(f => f.Comment == "heartbeat") >= 2);
    }

    [Fact]
    public async Task The_stream_ends_with_an_idle_event_after_the_maximum_duration_and_frees_its_connection()
    {
        var factory = await CreateFactoryAsync(settings: new Dictionary<string, string?>
        {
            ["Live:MaxStreamDuration"] = "00:00:00.800",
            ["Live:ClockCheckInterval"] = "00:00:00.100",
            ["Live:HeartbeatInterval"] = "00:00:00.200",
        });
        using var _f = factory;
        using var http = factory.CreateClient();
        var hub = factory.Services.GetRequiredService<LiveHub>();

        await using var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        var idle = await live.WaitForAsync(f => f.Event == "idle");

        Assert.Equal("max-duration", idle.Json.GetProperty("reason").GetString());
        await WaitUntilAsync(() => hub.ConnectionCount == 0, "Verbindung freigegeben");
        var framesAtEnd = live.Frames.Count;
        await Task.Delay(500);
        Assert.Equal(framesAtEnd, live.Frames.Count);
        Assert.Equal("idle", live.Frames[^1].Event);
    }

    [Fact]
    public async Task Without_a_maximum_duration_the_stream_never_sends_idle()
    {
        var factory = await CreateFactoryAsync(settings: new Dictionary<string, string?> { ["Live:HeartbeatInterval"] = "00:00:00.100" });
        using var _f = factory;
        using var http = factory.CreateClient();

        await using var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await live.WaitForAsync(f => f.Comment == "heartbeat", skip: 2);

        Assert.DoesNotContain(live.Frames, f => f.Event == "idle");
    }

    [Fact]
    public async Task A_new_message_arrives_with_its_stream_id_as_event_id()
    {
        var factory = await CreateFactoryAsync();
        using var _f = factory;
        using var http = factory.CreateClient();
        await using var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await live.WaitForAsync(f => f.Event == "clock");

        var id = await AddFeedEntryAsync(factory.Services, 10, Rating.Warn, "Presskraft RT-01 zu niedrig");

        var frame = await live.WaitForAsync(f => f.Event == "feed");
        Assert.Equal(id, frame.Id);
        Assert.Equal("Presskraft RT-01 zu niedrig", frame.Json.GetProperty("text").GetString());
        Assert.Equal("warn", frame.Json.GetProperty("level").GetString());
        Assert.Equal("RT-01 · Test", frame.Json.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Resuming_with_Last_Event_ID_delivers_exactly_the_missing_messages()
    {
        var factory = await CreateFactoryAsync(
            withHistory: true, settings: new Dictionary<string, string?> { ["Live:MaxConnectionsPerOwner"] = "10" });
        using var _f = factory;
        using var http = factory.CreateClient();

        await using var everything = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead", lastEventId: "0-0");
        await everything.WaitForAsync(f => f.Event == "clock");
        var allIds = everything.Frames.Where(f => f.Event == "feed").Select(f => f.Id!).ToList();
        Assert.True(allIds.Count >= 4, "Die aufgenommene Historie liefert mehrere Meldungen.");
        Assert.True(allIds.Zip(allIds.Skip(1)).All(p => EventFeed.CompareIds(p.First, p.Second) < 0), "Ids steigen.");

        // Der Client hat die ersten drei gesehen und verbindet neu.
        await using var resumed = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead", lastEventId: allIds[2]);
        await resumed.WaitForAsync(f => f.Event == "clock");

        Assert.Equal(allIds.Skip(3), resumed.Frames.Where(f => f.Event == "feed").Select(f => f.Id));

        // Ohne Last-Event-ID gibt es keine Nachlieferung, nur den Stand.
        await using var fresh = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await fresh.WaitForAsync(f => f.Event == "clock");
        Assert.DoesNotContain(fresh.Frames, f => f.Event == "feed");

        // Der Parameter lastEventId ersetzt den Header, wenn der Client selbst neu verbindet (EventSource gibt den Header nicht frei).
        await using var viaQuery = await SseTestClient.ConnectAsync(http, $"/api/live?role=shift_lead&lastEventId={allIds[^2]}");
        await viaQuery.WaitForAsync(f => f.Event == "clock");
        Assert.Equal([allIds[^1]], viaQuery.Frames.Where(f => f.Event == "feed").Select(f => f.Id));
    }

    [Fact]
    public async Task Messages_are_filtered_by_role_on_the_server()
    {
        var factory = await CreateFactoryAsync();
        using var _f = factory;
        using var http = factory.CreateClient();
        await using var shiftLead = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await using var hallLead = await SseTestClient.ConnectAsync(http, "/api/live?role=hall_lead");
        await using var productionLead = await SseTestClient.ConnectAsync(http, "/api/live?role=production_lead");
        foreach (var client in new[] { shiftLead, hallLead, productionLead }) await client.WaitForAsync(f => f.Event == "clock");

        _ = await AddFeedEntryAsync(factory.Services, 10, Rating.Info, "Hinweis");
        _ = await AddFeedEntryAsync(factory.Services, 11, Rating.Warn, "Warnung");
        var critical = await AddFeedEntryAsync(factory.Services, 12, Rating.Crit, "Störung");

        // Die kritische Meldung kommt zuletzt: Wer sie hat, hat alles bekommen, was seine Rolle sehen darf.
        foreach (var client in new[] { shiftLead, hallLead, productionLead })
            _ = await client.WaitForAsync(f => f.Event == "feed" && f.Id == critical);

        Assert.Equal(["Hinweis", "Warnung", "Störung"], Texts(shiftLead));
        Assert.Equal(["Warnung", "Störung"], Texts(hallLead));
        Assert.Equal(["Störung"], Texts(productionLead));

        static IEnumerable<string?> Texts(SseTestClient client) =>
            client.Frames.Where(f => f.Event == "feed").Select(f => f.Json.GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_connection_limit_per_owner_rejects_with_429_and_frees_slots_on_disconnect()
    {
        var factory = await CreateFactoryAsync(settings: new Dictionary<string, string?> { ["Live:MaxConnectionsPerOwner"] = "2" });
        using var _f = factory;
        using var http = factory.CreateClient();
        var hub = factory.Services.GetRequiredService<LiveHub>();

        var first = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await using var second = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await using var rejected = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await using var otherOwner = await SseTestClient.ConnectAsync(http, "/api/live?role=hall_lead");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherOwner.StatusCode);
        Assert.Equal(3, hub.ConnectionCount);

        await first.DisposeAsync();
        await WaitUntilAsync(() => hub.ConnectionCount == 2, "Verbindung freigegeben");

        await using var again = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Closing_the_connection_unregisters_it()
    {
        var factory = await CreateFactoryAsync();
        using var _f = factory;
        using var http = factory.CreateClient();
        var hub = factory.Services.GetRequiredService<LiveHub>();

        var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await live.WaitForAsync(f => f.Event == "clock");
        Assert.Equal(1, hub.ConnectionCount);

        await live.DisposeAsync();

        await WaitUntilAsync(() => hub.ConnectionCount == 0, "Verbindung abgemeldet");
    }

    [Fact]
    public async Task Taking_an_inbox_item_updates_the_inbox_counter_of_the_open_stream()
    {
        var factory = await CreateFactoryAsync(withHistory: true);
        using var _f = factory;
        using var http = factory.CreateClient();
        await using var live = await SseTestClient.ConnectAsync(http, "/api/live?role=shift_lead");
        await live.WaitForAsync(f => f.Event == "clock");

        using var response = await http.PostAsync(new Uri("/api/inbox/backlog-RT-03/take?role=shift_lead", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var frame = await live.WaitForAsync(f => f.Event == "inbox", skip: 1);
        Assert.Equal(2, frame.Json.GetProperty("open").GetInt32());
        Assert.False(frame.Json.GetProperty("hasUrgent").GetBoolean());
    }

    [Fact]
    public async Task A_change_through_instance_A_reaches_the_client_of_instance_B()
    {
        var instanceA = await CreateFactoryAsync();
        using var _a = instanceA;
        var instanceB = await CreateFactoryAsync(shareRedis: true);
        using var _b = instanceB;
        using var httpA = instanceA.CreateClient();
        using var httpB = instanceB.CreateClient();
        Assert.NotSame(instanceA.Services.GetRequiredService<LiveHub>(), instanceB.Services.GetRequiredService<LiveHub>());

        await using var clientOfB = await SseTestClient.ConnectAsync(httpB, "/api/live?role=shift_lead");
        await clientOfB.WaitForAsync(f => f.Event == "clock");
        await WaitForSubscriptionAsync(instanceA.Services, expectedSubscribers: 2);

        var id = await AddFeedEntryAsync(instanceA.Services, 10, Rating.Crit, "Störung über Instanz A");

        var frame = await clientOfB.WaitForAsync(f => f.Event == "feed");
        Assert.Equal(id, frame.Id);
        Assert.Equal("Störung über Instanz A", frame.Json.GetProperty("text").GetString());
    }

    /// <summary>Beide Instanzen müssen den Pub/Sub-Kanal abonniert haben, bevor die Änderung veröffentlicht wird.</summary>
    private static async Task WaitForSubscriptionAsync(IServiceProvider services, int expectedSubscribers)
    {
        var redis = services.GetRequiredService<IConnectionMultiplexer>();
        var channel = LiveChannel.Name();
        await WaitUntilAsync(() =>
            redis.GetDatabase().Execute("PUBSUB", "NUMSUB", channel) is { } result
            && result.Resp2Type == ResultType.Array
            && (int)((RedisResult[])result!)[1] >= expectedSubscribers, "Pub/Sub-Abonnenten");
    }
}
