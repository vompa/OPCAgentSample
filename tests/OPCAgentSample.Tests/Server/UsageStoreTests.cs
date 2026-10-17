using OPCAgentSample.Server.Features.ChatHistory;

namespace OPCAgentSample.Tests.Server;

/// <summary>Histogramm der Antwortzeiten (Seite „KI-Nutzung“): Einordnung in die Behälter und geschätzte Perzentile.</summary>
public sealed class UsageStoreTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(249.9, 0)]
    [InlineData(250, 1)]
    [InlineData(999, 2)]
    [InlineData(1000, 3)]
    [InlineData(29_999, 9)]
    [InlineData(30_000, 10)]
    [InlineData(500_000, 10)]
    public void A_duration_falls_into_the_bin_that_starts_at_its_lower_edge(double milliseconds, int expectedBin) =>
        Assert.Equal(expectedBin, UsageStore.BinOf(milliseconds));

    [Fact]
    public void Percentiles_are_interpolated_inside_the_bin_that_holds_them()
    {
        var bins = new long[UsageStore.Bins];
        bins[3] = 100; // 1000 bis 2000 ms

        Assert.Equal(1500, UsageStore.Percentile(bins, 0.5));
        Assert.Equal(1950, UsageStore.Percentile(bins, 0.95)!.Value, 6);
    }

    [Fact]
    public void Percentile_of_a_mixed_histogram_picks_the_bin_by_cumulative_count()
    {
        var bins = new long[UsageStore.Bins];
        bins[1] = 50; // 250 bis 500 ms
        bins[4] = 50; // 2000 bis 3000 ms

        Assert.InRange(UsageStore.Percentile(bins, 0.25)!.Value, 250, 500);
        Assert.InRange(UsageStore.Percentile(bins, 0.75)!.Value, 2000, 3000);
    }

    [Fact]
    public void Percentile_without_measurements_is_unknown() =>
        Assert.Null(UsageStore.Percentile(new long[UsageStore.Bins], 0.5));

    [Fact]
    public void The_open_last_bin_counts_twice_as_wide()
    {
        var bins = new long[UsageStore.Bins];
        bins[^1] = 10; // ab 30 000 ms

        Assert.Equal(45_000, UsageStore.Percentile(bins, 0.5));
    }
}
