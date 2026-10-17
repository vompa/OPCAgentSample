using OPCAgentSample.Domain;
using OPCAgentSample.Server.Infrastructure.Redis;

namespace OPCAgentSample.Tests.Server;

public sealed class RedisKeysTests
{
    private static readonly ShiftCalendar s_calendar = new();
    private static readonly DateTimeOffset s_now = SimulationClock.DemoStart;
    private static readonly ProductionPeriod s_day = s_calendar.DayPeriod(new DateOnly(2026, 10, 14));
    private static readonly ProductionPeriod s_nightShift = s_calendar.ShiftPeriod(new DateOnly(2026, 10, 14), Shift.Night);

    [Fact]
    public void Keys_follow_the_layout_of_the_specification()
    {
        Assert.Equal("st:RT-01", RedisKeys.Station("RT-01"));
        Assert.Equal("ev:A:2026-10-14", RedisKeys.Events("A", s_day));
        Assert.Equal("alarm:A:active", RedisKeys.ActiveAlarms("A"));
        Assert.Equal("inbox:shift_lead", RedisKeys.Inbox("shift_lead"));
        Assert.Equal("hist:user:martin.huber", RedisKeys.History("user:martin.huber"));
        Assert.Equal("chat:s:user:martin.huber", RedisKeys.ChatSessions("user:martin.huber"));
        Assert.Equal("fav:martin.huber", RedisKeys.Favorites("martin.huber"));
        Assert.Equal("md:article", RedisKeys.MasterData("article"));
    }

    [Fact]
    public void Day_keys_reject_other_period_levels()
    {
        Assert.Throws<ArgumentException>(() => RedisKeys.Events("A", s_nightShift));
        Assert.Throws<ArgumentException>(() => RedisKeys.Events("A", s_calendar.WeekPeriodOf(s_now)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("RT:01")]
    [InlineData("RT 01")]
    [InlineData("*")]
    public void Key_parts_cannot_reach_other_keyspaces(string part)
    {
        Assert.Throws<ArgumentException>(() => RedisKeys.Station(part));
        Assert.Throws<ArgumentException>(() => RedisKeys.Favorites(part));
    }

    [Theory]
    [InlineData("martin.huber")]
    [InlineData("user:")]
    [InlineData("user:a:b")]
    [InlineData("USER:x")]
    public void Owner_keys_must_have_the_form_kind_colon_id(string owner)
    {
        Assert.Throws<ArgumentException>(() => RedisKeys.History(owner));
    }
}
