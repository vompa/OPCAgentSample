namespace OPCAgentSample.Rollup;

/// <summary>
/// Aufbewahrung (Spezifikation Abschnitt 15): Sie steht am Schlüssel (TTL) bzw. am Stream, nicht in einem Aufräumdienst.
/// Die Fristen laufen ab dem letzten Schreiben; abgeschlossene Perioden werden danach nicht mehr geschrieben.
/// </summary>
public static class Retention
{
    /// <summary>Zähler und Statistik je Schicht, Ereignisse, Rückverfolgung: ein Quartal.</summary>
    public static readonly TimeSpan Shift = TimeSpan.FromDays(100);

    /// <summary>Zähler und Statistik je Tag und Woche: zwei Jahre.</summary>
    public static readonly TimeSpan DayAndWeek = TimeSpan.FromDays(730);

    /// <summary>Zähler und Statistik je Monat, Quartal und Jahr: fünf Jahre.</summary>
    public static readonly TimeSpan Archive = TimeSpan.FromDays(1825);

    /// <summary>Prozessstatistik im 1-Minuten-Raster: zwei Produktionstage (mit einem Tag Reserve).</summary>
    public static readonly TimeSpan OneMinute = TimeSpan.FromDays(3);

    /// <summary>Prozessstatistik im 15-Minuten-Raster: 30 Tage (mit einem Tag Reserve).</summary>
    public static readonly TimeSpan FifteenMinutes = TimeSpan.FromDays(31);

    /// <summary>Ereignisse und Rückverfolgung: ein Quartal.</summary>
    public static readonly TimeSpan Events = TimeSpan.FromDays(100);

    /// <summary>Aufbewahrung der Zähler einer Ebene.</summary>
    public static TimeSpan For(RollupLevel level) => level switch
    {
        RollupLevel.Shift => Shift,
        RollupLevel.Day or RollupLevel.Week => DayAndWeek,
        _ => Archive,
    };

    /// <summary>Größe des Ringpuffers der Takte je Maschine.</summary>
    public const int RingBufferCycles = 1000;

    /// <summary>Länge der Minutenreihen (Füllstände, Temperaturen) je Variable: vier Stunden.</summary>
    public const int MinuteRingLength = 240;
}
