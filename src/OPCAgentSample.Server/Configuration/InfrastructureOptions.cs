using System.ComponentModel.DataAnnotations;

namespace OPCAgentSample.Server.Configuration;

/// <summary>Verbindung zu Redis, dem einzigen Speicher (Abschnitt ConnectionStrings).</summary>
public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required(AllowEmptyStrings = false)]
    public string Redis { get; set; } = string.Empty;
}

/// <summary>Redis als einziger Speicher (Spezifikation Abschnitt 15).</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>Zeitlimit je Redis-Befehl; danach gilt der Zugriff als fehlgeschlagen.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:00:30")]
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Wie oft der Start Redis erneut versucht, falls es noch hochfährt (z. B. docker compose, Container-App).</summary>
    [Range(1, 120)]
    public int StartupAttempts { get; set; } = 30;

    [Range(typeof(TimeSpan), "00:00:00", "00:01:00")]
    public TimeSpan StartupRetryDelay { get; set; } = TimeSpan.FromSeconds(2);
}

/// <summary>Verlauf der Fragen (Spezifikation Abschnitt 16).</summary>
public sealed class ChatHistoryOptions
{
    public const string SectionName = "ChatHistory";

    /// <summary>Aufbewahrung in Tagen seit der letzten Aktivität einer Sitzung.</summary>
    [Range(1, 3650)]
    public int RetentionDays { get; set; } = 30;

    /// <summary>Anzahl der letzten Fragen für Pfeil hoch/runter und die Liste „Zuletzt gefragt“.</summary>
    [Range(1, 200)]
    public int RecentQuestionLimit { get; set; } = 50;

    /// <summary>Abstand des Aufräum-Laufs.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(6);
}
