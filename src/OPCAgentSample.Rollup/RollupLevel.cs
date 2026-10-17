namespace OPCAgentSample.Rollup;

/// <summary>Ebenen der Verdichtung: Eine Periode ist die Summe ihrer Kinder (Schicht → Tag → Woche/Monat → Quartal → Jahr).</summary>
public enum RollupLevel
{
    Shift,
    Day,
    Week,
    Month,
    Quarter,
    Year,
}

public static class RollupLevels
{
    /// <summary>Die Ebenen oberhalb des Tags, die alle aus den Tagen summiert werden.</summary>
    public static IReadOnlyList<RollupLevel> AboveDay { get; } = [RollupLevel.Week, RollupLevel.Month, RollupLevel.Quarter, RollupLevel.Year];

    /// <summary>Kurzname der Ebene im Schlüssel (<c>s</c>, <c>d</c>, <c>w</c>, <c>m</c>, <c>q</c>, <c>y</c>).</summary>
    public static string Code(this RollupLevel level) => level switch
    {
        RollupLevel.Shift => "s",
        RollupLevel.Day => "d",
        RollupLevel.Week => "w",
        RollupLevel.Month => "m",
        RollupLevel.Quarter => "q",
        RollupLevel.Year => "y",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}
