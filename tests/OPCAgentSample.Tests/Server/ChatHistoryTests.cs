using System.Text.Json;
using OPCAgentSample.Server.Features.ChatHistory;
using OPCAgentSample.Server.Infrastructure.Redis;
using StackExchange.Redis;

namespace OPCAgentSample.Tests.Server;

[Collection(InfrastructureFixture.Name)]
public sealed class ChatHistoryTests(InfrastructureFixture infrastructure)
{
    private static readonly ChatOwner s_huber = ChatOwner.ForUser("martin.huber");
    private static readonly ChatOwner s_gruber = ChatOwner.ForUser("katharina.gruber");
    private static readonly DateTimeOffset s_start = new(2026, 10, 15, 7, 42, 0, TimeSpan.Zero);

    private static NewChatTurn Turn(string question, string card = """{"headline":"RT-03 steht","rows":[1,2]}""") => new(
        question,
        JsonDocument.Parse(card).RootElement.Clone(),
        JsonDocument.Parse("""[{"tool":"get_hall_overview","params":{"hallId":"A"},"ms":42}]""").RootElement.Clone(),
        s_start,
        InputTokens: 1200,
        OutputTokens: 340);

    private async Task<(TestServices Services, ChatHistoryService History, ManualTimeProvider Time)> CreateAsync()
    {
        var time = new ManualTimeProvider(s_start);
        var services = await infrastructure.CreateServicesAsync(time);
        return (services, services.Get<ChatHistoryService>(), time);
    }

    [Fact]
    public async Task Saved_turn_is_loaded_with_card_tool_calls_data_state_and_tokens()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");

        await history.AppendTurnAsync(s_huber, session.Id, Turn("Was muss ich von der Nachtschicht wissen?"));
        var package = await history.GetLatestSessionAsync(s_huber);

        Assert.NotNull(package);
        Assert.Equal(session.Id, package.Session.Id);
        Assert.Equal("shift_lead", package.Session.Role);
        Assert.Equal("Was muss ich von der Nachtschicht wissen?", package.Session.Title);
        var turn = Assert.Single(package.Turns);
        Assert.Equal(1, turn.Sequence);
        Assert.Equal("RT-03 steht", turn.AnswerCard.GetProperty("headline").GetString());
        Assert.Equal("get_hall_overview", turn.ToolCalls[0].GetProperty("tool").GetString());
        Assert.Equal(s_start, turn.DataAsOfUtc);
        Assert.Equal((1200, 340), (turn.InputTokens, turn.OutputTokens));
    }

    [Fact]
    public async Task Latest_session_is_kept_only_in_redis_and_gone_after_a_flush()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, session.Id, Turn("Hallenlage?"));
        var redis = services.Get<IConnectionMultiplexer>().GetDatabase();
        Assert.True(await redis.KeyExistsAsync(RedisKeys.ChatSession(session.Id)));
        Assert.True(await redis.KeyExistsAsync(RedisKeys.ChatTurns(session.Id)));

        var loaded = await history.GetLatestSessionAsync(s_huber);
        var again = await history.GetLatestSessionAsync(s_huber);

        Assert.NotNull(loaded);
        Assert.Equal(loaded.Session, again!.Session);
        Assert.Equal("Hallenlage?", loaded.Turns[0].Question);
        Assert.Equal("RT-03 steht", loaded.Turns[0].AnswerCard.GetProperty("headline").GetString());
        Assert.Equal(2, loaded.Turns[0].AnswerCard.GetProperty("rows").GetArrayLength());

        // Redis ist der einzige Speicher: Nach dem Leeren ist der Verlauf weg.
        await infrastructure.FlushRedisAsync();
        Assert.Null(await history.GetLatestSessionAsync(s_huber));
        Assert.Empty(await history.ListSessionsAsync(s_huber));
        Assert.Empty(await history.GetRecentQuestionsAsync(s_huber));
    }

    [Fact]
    public async Task Stored_keys_expire_after_the_retention_period()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, session.Id, Turn("Frage"));
        var redis = services.Get<IConnectionMultiplexer>().GetDatabase();

        foreach (var key in new[]
                 {
                     RedisKeys.ChatSession(session.Id), RedisKeys.ChatTurns(session.Id),
                     RedisKeys.ChatSessions(s_huber.Key), RedisKeys.History(s_huber.Key),
                 })
        {
            var ttl = await redis.KeyTimeToLiveAsync(key);
            Assert.NotNull(ttl);
            Assert.InRange(ttl.Value, TimeSpan.FromDays(30), TimeSpan.FromDays(32));
        }
    }

    [Fact]
    public async Task Appending_a_turn_updates_session_and_recent_questions()
    {
        var (services, history, time) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, session.Id, Turn("Erste Frage"));
        await history.GetLatestSessionAsync(s_huber);
        await history.GetRecentQuestionsAsync(s_huber);
        time.Advance(TimeSpan.FromSeconds(30));

        await history.AppendTurnAsync(s_huber, session.Id, Turn("Zweite Frage"));

        Assert.Equal(2, (await history.GetLatestSessionAsync(s_huber))!.Turns.Count);
        Assert.Equal(["Zweite Frage", "Erste Frage"], await history.GetRecentQuestionsAsync(s_huber));
    }

    [Fact]
    public async Task One_owner_never_sees_sessions_of_another_owner()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, session.Id, Turn("Geheime Frage von Huber"));
        await history.GetLatestSessionAsync(s_huber);
        await history.GetRecentQuestionsAsync(s_huber);

        Assert.Null(await history.GetLatestSessionAsync(s_gruber));
        Assert.Null(await history.GetSessionAsync(s_gruber, session.Id));
        Assert.Empty(await history.ListSessionsAsync(s_gruber));
        Assert.Empty(await history.GetRecentQuestionsAsync(s_gruber));
        await Assert.ThrowsAsync<ChatSessionNotFoundException>(() =>
            history.AppendTurnAsync(s_gruber, session.Id, Turn("Fremde Sitzung")));

        // Auch nach dem Fehlversuch bleibt Hubers Verlauf unverändert.
        Assert.Single((await history.GetLatestSessionAsync(s_huber))!.Turns);
    }

    [Fact]
    public async Task Access_codes_and_browser_tokens_are_separate_owners_and_never_stored_in_clear_text()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;
        var byCode = ChatOwner.ForAccessCode("DEMO-1234");
        var byToken = ChatOwner.ForBrowserToken("DEMO-1234");
        var session = await history.StartSessionAsync(byCode, "hall_lead");
        await history.AppendTurnAsync(byCode, session.Id, Turn("Wie läuft die Halle heute?"));

        Assert.NotEqual(byCode.Key, byToken.Key);
        Assert.DoesNotContain("DEMO-1234", byCode.Key, StringComparison.Ordinal);
        Assert.Null(await history.GetLatestSessionAsync(byToken));
        var stored = await services.Get<IConnectionMultiplexer>().GetDatabase().HashGetAsync(RedisKeys.ChatSession(session.Id), "owner");
        Assert.Equal(byCode.Key, stored);
        Assert.StartsWith("code:", stored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_last_50_distinct_questions_are_returned_newest_first()
    {
        var (services, history, time) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        for (var i = 1; i <= 60; i++)
        {
            await history.AppendTurnAsync(s_huber, session.Id, Turn($"Frage {i}"));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await history.AppendTurnAsync(s_huber, session.Id, Turn("Frage 30"));
        var questions = await history.GetRecentQuestionsAsync(s_huber);

        Assert.Equal(50, questions.Count);
        Assert.Equal("Frage 30", questions[0]);
        Assert.Equal("Frage 60", questions[1]);
        Assert.Equal(1, questions.Count(q => q == "Frage 30"));
        Assert.DoesNotContain("Frage 10", questions);
        Assert.Contains("Frage 12", questions);
    }

    [Fact]
    public async Task New_session_keeps_the_old_one_in_the_list_and_becomes_the_latest()
    {
        var (services, history, time) = await CreateAsync();
        await using var _ = services;
        var first = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, first.Id, Turn("Hallenlage?"));
        await history.AppendTurnAsync(s_huber, first.Id, Turn("Schichtziel?"));
        time.Advance(TimeSpan.FromMinutes(5));

        var second = await history.StartSessionAsync(s_huber, "shift_lead");
        var again = await history.StartSessionAsync(s_huber, "shift_lead");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(second.Id, again.Id);
        var latest = await history.GetLatestSessionAsync(s_huber);
        Assert.Equal(second.Id, latest!.Session.Id);
        Assert.Empty(latest.Turns);

        var list = await history.ListSessionsAsync(s_huber);
        var entry = Assert.Single(list);
        Assert.Equal(first.Id, entry.Id);
        Assert.Equal(2, entry.TurnCount);
        Assert.Equal("Hallenlage?", entry.Title);

        var old = await history.GetSessionAsync(s_huber, first.Id);
        Assert.Equal(["Hallenlage?", "Schichtziel?"], old!.Turns.Select(t => t.Question));
    }

    [Fact]
    public async Task Get_or_start_creates_a_session_on_first_visit()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;

        var first = await history.GetOrStartLatestSessionAsync(s_huber, "shift_lead");
        var second = await history.GetOrStartLatestSessionAsync(s_huber, "shift_lead");

        Assert.Empty(first.Turns);
        Assert.Equal(first.Session.Id, second.Session.Id);
    }

    [Fact]
    public async Task Purge_deletes_sessions_after_30_days_without_activity_including_their_turns()
    {
        var (services, history, time) = await CreateAsync();
        await using var _ = services;
        var old = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, old.Id, Turn("Alte Frage"));
        time.Advance(TimeSpan.FromDays(20));
        var recent = await history.StartSessionAsync(s_gruber, "hall_lead");
        await history.AppendTurnAsync(s_gruber, recent.Id, Turn("Junge Frage"));
        await history.GetLatestSessionAsync(s_huber);

        time.Advance(TimeSpan.FromDays(9));
        Assert.Equal(0, await history.PurgeExpiredAsync());

        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await history.PurgeExpiredAsync());

        Assert.Null(await history.GetLatestSessionAsync(s_huber));
        Assert.Empty(await history.GetRecentQuestionsAsync(s_huber));
        Assert.NotNull(await history.GetLatestSessionAsync(s_gruber));
        var redis = services.Get<IConnectionMultiplexer>().GetDatabase();
        Assert.False(await redis.KeyExistsAsync(RedisKeys.ChatSession(old.Id)));
        Assert.False(await redis.KeyExistsAsync(RedisKeys.ChatTurns(old.Id)));
        Assert.True(await redis.KeyExistsAsync(RedisKeys.ChatTurns(recent.Id)));
    }

    [Fact]
    public async Task Retention_is_configurable()
    {
        var time = new ManualTimeProvider(s_start);
        await using var services = await infrastructure.CreateServicesAsync(
            time, settings: new Dictionary<string, string?> { ["ChatHistory:RetentionDays"] = "7" });
        var history = services.Get<ChatHistoryService>();
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, session.Id, Turn("Frage"));

        time.Advance(TimeSpan.FromDays(8));

        Assert.Equal(1, await history.PurgeExpiredAsync());
    }

    [Fact]
    public async Task Invalid_answer_card_or_tool_calls_are_rejected()
    {
        var (services, history, _) = await CreateAsync();
        await using var _ = services;
        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        var card = JsonDocument.Parse("[1]").RootElement.Clone();
        var calls = JsonDocument.Parse("{}").RootElement.Clone();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            history.AppendTurnAsync(s_huber, session.Id, Turn("x") with { AnswerCard = card }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            history.AppendTurnAsync(s_huber, session.Id, Turn("x") with { ToolCalls = calls }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            history.AppendTurnAsync(s_huber, session.Id, Turn(" ")));
    }

    [Fact]
    public async Task A_session_starts_on_the_simulation_clock_while_activity_and_retention_follow_real_time()
    {
        var (services, history, time) = await CreateAsync();
        await using var _ = services;
        var simulation = (ManualDemoClock)services.Get<OPCAgentSample.Server.Demo.IDemoClock>();
        var simulatedStart = new DateTimeOffset(2026, 1, 5, 6, 0, 0, TimeSpan.Zero);
        simulation.Set(simulatedStart);

        var session = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, session.Id, Turn("Wie war die Nacht?"));
        var listed = Assert.Single(await history.ListSessionsAsync(s_huber));

        Assert.Equal(simulatedStart, session.StartedAtUtc);
        Assert.Equal(simulatedStart, listed.StartedAtUtc);
        Assert.Equal(s_start, listed.LastActivityUtc);

        // Die Aufbewahrung rechnet mit der Echtzeit: Eine Sitzung mit "alter" Simulationszeit wird nicht sofort gelöscht ...
        Assert.Equal(0, await history.PurgeExpiredAsync());
        time.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await history.PurgeExpiredAsync());

        // ... und nach der Frist trotzdem entfernt, auch wenn die Simulationszeit dann in der Zukunft liegt.
        simulation.Set(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, await history.PurgeExpiredAsync());
    }

    [Fact]
    public async Task The_newest_session_stays_first_even_if_the_simulator_restarted_with_an_earlier_start_time()
    {
        var (services, history, time) = await CreateAsync();
        await using var _ = services;
        var simulation = (ManualDemoClock)services.Get<OPCAgentSample.Server.Demo.IDemoClock>();
        simulation.Set(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
        var older = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, older.Id, Turn("Alte Frage"));
        time.Advance(TimeSpan.FromMinutes(5));

        simulation.Set(new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero));
        var newer = await history.StartSessionAsync(s_huber, "shift_lead");
        await history.AppendTurnAsync(s_huber, newer.Id, Turn("Neue Frage"));

        Assert.Equal(newer.Id, (await history.GetLatestSessionAsync(s_huber))!.Session.Id);
        Assert.Equal([newer.Id, older.Id], (await history.ListSessionsAsync(s_huber)).Select(s => s.Id));
    }

    [Fact]
    public void Owner_rejects_empty_and_malformed_identifiers()
    {
        Assert.Throws<ArgumentException>(() => ChatOwner.ForUser(""));
        Assert.Throws<ArgumentException>(() => ChatOwner.ForUser("a:b"));
        Assert.Throws<ArgumentException>(() => ChatOwner.ForAccessCode(" "));
    }
}
