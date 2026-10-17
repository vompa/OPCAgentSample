namespace OPCAgentSample.Kpi;

/// <summary>Halboffenes Zeitfenster [Start, End) in UTC.</summary>
public readonly record struct TimeWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public bool IsEmpty => EndUtc <= StartUtc;

    public TimeSpan Duration => IsEmpty ? TimeSpan.Zero : EndUtc - StartUtc;

    public bool Contains(DateTimeOffset instant) => instant >= StartUtc && instant < EndUtc;

    public TimeWindow Intersect(DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        new(StartUtc > fromUtc ? StartUtc : fromUtc, EndUtc < toUtc ? EndUtc : toUtc);

    public TimeWindow Intersect(TimeWindow other) => Intersect(other.StartUtc, other.EndUtc);

    /// <summary>Überlappung mit einer Liste disjunkter Fenster.</summary>
    public TimeSpan OverlapWith(IEnumerable<TimeWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var sum = TimeSpan.Zero;
        foreach (var window in windows)
        {
            sum += Intersect(window).Duration;
        }

        return sum;
    }

    public static TimeSpan TotalDuration(IEnumerable<TimeWindow> windows) =>
        windows.Aggregate(TimeSpan.Zero, (sum, window) => sum + window.Duration);
}
