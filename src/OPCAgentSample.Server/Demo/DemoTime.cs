using System.Globalization;

namespace OPCAgentSample.Server.Demo;

/// <summary>Ortszeit der Demo (Wien) für Anzeigetexte; gespeichert wird immer UTC.</summary>
public static class DemoTime
{
    private static readonly string[] Weekdays = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];
    private static readonly TimeZoneInfo Zone = FindZone();

    public static DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone);

    /// <summary>Uhrzeit als <c>09:42</c>.</summary>
    public static string TimeText(DateTimeOffset utc) => ToLocal(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Datum und Uhrzeit als <c>Do 15.10.2026, 09:42</c>.</summary>
    public static string DateTimeText(DateTimeOffset utc)
    {
        var local = ToLocal(utc);
        return $"{Weekdays[(int)local.DayOfWeek]} {local.ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture)}";
    }

    public static string Greeting(DateTimeOffset utc) => ToLocal(utc).Hour switch
    {
        < 11 => "Guten Morgen",
        < 18 => "Guten Tag",
        _ => "Guten Abend",
    };

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        }
    }
}
