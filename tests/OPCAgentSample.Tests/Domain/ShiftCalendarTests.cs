using OPCAgentSample.Domain;

namespace OPCAgentSample.Tests.Domain;

public sealed class ShiftCalendarTests
{
    private readonly ShiftCalendar _calendar = new();

    [Fact]
    public void Demo_start_is_thursday_0942_local_in_early_shift_of_KW42()
    {
        var now = SimulationClock.DemoStart;

        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 42, 0, TimeSpan.FromHours(2)), _calendar.ToLocal(now));
        Assert.Equal(Shift.Early, _calendar.ShiftOf(now));
        Assert.Equal("2026-10-15F", _calendar.ShiftPeriodOf(now).Key);
        Assert.Equal("2026-10-15", _calendar.DayPeriodOf(now).Key);
        Assert.Equal("2026-W42", _calendar.WeekPeriodOf(now).Key);
        Assert.Equal("2026-10-15T09", _calendar.HourOf(now).Key);
    }

    [Fact]
    public void Night_shift_belongs_to_the_production_day_of_its_start()
    {
        // Do 15.10. 03:00 Ortszeit = Nachtschicht von Mi 14.10.
        var instant = new DateTimeOffset(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateOnly(2026, 10, 14), _calendar.ProductionDayOf(instant));
        var night = _calendar.ShiftPeriodOf(instant);
        Assert.Equal("2026-10-14N", night.Key);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 20, 0, 0, TimeSpan.Zero), night.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 4, 0, 0, TimeSpan.Zero), night.EndUtc);
    }

    [Fact]
    public void Week_runs_from_monday_0600_and_production_week_has_15_shifts()
    {
        var week = _calendar.WeekPeriod(new DateOnly(2026, 10, 14));

        Assert.Equal(new DateTimeOffset(2026, 10, 12, 4, 0, 0, TimeSpan.Zero), week.StartUtc);
        Assert.Equal(15, _calendar.ProductionShiftsBetween(week.StartUtc, week.EndUtc).Count());
    }

    [Fact]
    public void Weekend_and_national_holiday_are_not_production_days()
    {
        Assert.False(_calendar.IsProductionDay(new DateOnly(2026, 10, 17)));
        Assert.False(_calendar.IsProductionDay(new DateOnly(2026, 10, 18)));
        Assert.False(_calendar.IsProductionDay(new DateOnly(2026, 10, 26)));
        Assert.True(_calendar.IsProductionDay(new DateOnly(2026, 10, 27)));
    }

    [Fact]
    public void Shift_lasts_nine_hours_in_utc_when_clocks_go_back()
    {
        // 25.10.2026: 03:00 Sommerzeit wird 02:00 Winterzeit; die Nachtschicht vom Samstag ist 9 h lang
        var night = _calendar.ShiftPeriod(new DateOnly(2026, 10, 24), Shift.Night);

        Assert.Equal(TimeSpan.FromHours(9), night.Duration);
    }

    [Fact]
    public void Simulation_clock_can_be_set_and_accelerated()
    {
        var real = new ManualTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new SimulationClock(realTime: real);

        real.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(SimulationClock.DemoStart.AddSeconds(10), clock.UtcNow);

        clock.SetSpeed(60);
        real.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(SimulationClock.DemoStart.AddSeconds(70), clock.UtcNow);

        clock.Set(SimulationClock.DemoStart);
        Assert.Equal(SimulationClock.DemoStart, clock.UtcNow);
    }

    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
