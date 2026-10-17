using OPCAgentSample.Domain;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>Ein Abschnitt einer Reihe: Beginn, Ende und die zusammengeführte Statistik.</summary>
public sealed record StatBucket(DateTimeOffset StartUtc, DateTimeOffset EndUtc, ProcessStat Stat);

public sealed partial class RollupReader
{
    private static readonly TimeSpan s_quarterHour = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_minute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Statistik einer Reihe über ein Fenster, aus dem 15-Minuten-Raster (freie Fenster sind 15 Minuten genau: Fächer, die das Fenster
    /// berühren, zählen ganz). Tage, die älter sind als das Raster (30 Tage) und ganz im Fenster liegen, kommen aus der Tagesstatistik.
    /// </summary>
    public async Task<ProcessStat> ReadRangeAsync(
        string station, string variable, DateTimeOffset from, DateTimeOffset to, ShiftCalendar calendar)
    {
        var info = SeriesCatalog.For(station, variable);
        var sum = new ProcessStat(info.Bins);
        foreach (var slot in await ReadSlotsAsync(info, fine: false, from, to, calendar)) sum.Merge(slot.Stat);
        return sum;
    }

    /// <summary>Die Fächer des Fensters im feinsten passenden Raster: 1 Minute (nur Rundtische, wenn <paramref name="fine"/>), sonst 15 Minuten.</summary>
    public async Task<IReadOnlyList<StatBucket>> ReadSlotsAsync(
        SeriesInfo info, bool fine, DateTimeOffset from, DateTimeOffset to, ShiftCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(calendar);
        var useFine = fine && info.HasFine;
        var width = useFine ? s_minute : s_quarterHour;
        var resolution = useFine ? "1m" : "15m";
        var days = new List<(ProductionPeriod Day, int First, int Last)>();
        for (var day = calendar.ProductionDayOf(from); calendar.DayPeriod(day).StartUtc < to; day = day.AddDays(1))
        {
            var period = calendar.DayPeriod(day);
            var start = from > period.StartUtc ? from : period.StartUtc;
            var end = to < period.EndUtc ? to : period.EndUtc;
            if (end <= start) continue;

            days.Add((period, (int)((start - period.StartUtc).Ticks / width.Ticks), (int)(((end - period.StartUtc).Ticks - 1) / width.Ticks)));
        }

        var batch = Database.CreateBatch();
        var reads = days
            .Select(d => batch.HashGetAsync(
                RollupKeys.Stats(info.Station, info.Variable, resolution, d.Day.Key),
                [.. Enumerable.Range(d.First, d.Last - d.First + 1).Select(slot => (RedisValue)slot)]))
            .ToList();
        var exists = days.Select(d => batch.KeyExistsAsync(RollupKeys.Stats(info.Station, info.Variable, resolution, d.Day.Key))).ToList();
        batch.Execute();

        var result = new List<StatBucket>();
        for (var i = 0; i < days.Count; i++)
        {
            var (period, first, _) = days[i];
            var values = await reads[i];
            if (!await exists[i])
            {
                if (period.StartUtc >= from && period.EndUtc <= to && await ReadDayStatAsync(info, period) is { } dayStat)
                {
                    result.Add(new StatBucket(period.StartUtc, period.EndUtc, dayStat));
                }

                continue;
            }

            for (var k = 0; k < values.Length; k++)
            {
                if (values[k].IsNull) continue;

                var slotStart = period.StartUtc + (width * (first + k));
                result.Add(new StatBucket(slotStart, slotStart + width, ProcessStat.Decode((byte[])values[k]!, info.Bins, wide: false)));
            }
        }

        return result;
    }

    /// <summary>
    /// Verlauf einer Reihe in gleich langen Abschnitten (<paramref name="bucket"/>), ausgerichtet an <paramref name="origin"/>. Gelesen wird
    /// im 1-Minuten-Raster der Rundtische, wenn der Abschnitt aus ganzen Minuten besteht und das Fenster nicht über das Raster
    /// zurückreicht (zwei Tage), sonst im 15-Minuten-Raster (der Abschnitt wird auf ein Vielfaches von 15 Minuten aufgerundet).
    /// </summary>
    public async Task<IReadOnlyList<StatBucket>> ReadBucketsAsync(
        string station, string variable, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket, DateTimeOffset origin,
        DateTimeOffset latest, ShiftCalendar calendar)
    {
        var info = SeriesCatalog.For(station, variable);
        var fine = info.HasFine && bucket.Ticks % s_minute.Ticks == 0 && from >= latest - TimeSpan.FromDays(2);
        var width = bucket;
        if (!fine && width.Ticks % s_quarterHour.Ticks != 0)
        {
            width = TimeSpan.FromTicks(((width.Ticks / s_quarterHour.Ticks) + 1) * s_quarterHour.Ticks);
        }

        var groups = new SortedDictionary<long, ProcessStat>();
        foreach (var slot in await ReadSlotsAsync(info, fine, from, to, calendar))
        {
            var index = (long)Math.Floor((double)(slot.StartUtc - origin).Ticks / width.Ticks);
            if (!groups.TryGetValue(index, out var stat)) groups[index] = stat = new ProcessStat(info.Bins);
            stat.Merge(slot.Stat);
        }

        return [.. groups.Select(g =>
        {
            var start = origin + (width * g.Key);
            var end = start + width;
            return new StatBucket(start, end < to ? end : to, g.Value);
        })];
    }

    private async Task<ProcessStat?> ReadDayStatAsync(SeriesInfo info, ProductionPeriod day)
    {
        var year = PeriodKeys.Year(PeriodKeys.ParseDay(day.Key));
        var value = await Database.HashGetAsync(RollupKeys.Stats(info.Station, info.Variable, RollupLevel.Day.Code(), year), day.Key);
        return value.IsNull ? null : ProcessStat.Decode((byte[])value!, info.Bins, wide: true);
    }
}
