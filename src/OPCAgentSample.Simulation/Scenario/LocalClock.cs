using System.Globalization;
using OPCAgentSample.Domain;

namespace OPCAgentSample.Simulation.Scenario;

/// <summary>Schreibhilfe für Zeitpunkte des Szenarios in Ortszeit Wien (Jahr 2026), Ergebnis in UTC.</summary>
public sealed class LocalClock(ShiftCalendar calendar)
{
    private const int Year = 2026;

    /// <summary>Zeitpunkt im Oktober 2026, z. B. <c>Oct(14, "22:00")</c>.</summary>
    public DateTimeOffset Oct(int day, string time) => At(10, day, time);

    public DateTimeOffset At(int month, int day, string time)
    {
        var clock = TimeOnly.ParseExact(time, "HH:mm", CultureInfo.InvariantCulture);
        return calendar.ToUtc(new DateTime(Year, month, day, clock.Hour, clock.Minute, 0, DateTimeKind.Unspecified));
    }
}
