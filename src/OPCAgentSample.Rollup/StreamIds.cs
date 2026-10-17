using System.Globalization;
using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Vergibt aufsteigende Stream-Ids aus Millisekunden und einer Folgenummer. Eine Id muss größer sein als alle vorherigen; die Id
/// eines Eintrags ist sein Zeitpunkt (bei Ereignissen die Endemeldung). Der Stand wird beim ersten Zugriff aus dem Stream gelesen.
/// </summary>
internal sealed class StreamIds
{
    private long _lastMilliseconds = -1;
    private long _sequence;

    public static async Task<StreamIds> LoadAsync(IDatabase database, string key)
    {
        var ids = new StreamIds();
        var last = await database.StreamRangeAsync(key, count: 1, messageOrder: Order.Descending);
        if (last.Length > 0)
        {
            var parts = last[0].Id.ToString().Split('-');
            ids._lastMilliseconds = long.Parse(parts[0], CultureInfo.InvariantCulture);
            ids._sequence = long.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        return ids;
    }

    public string Next(DateTimeOffset at)
    {
        var milliseconds = Math.Max(at.ToUnixTimeMilliseconds(), _lastMilliseconds);
        _sequence = milliseconds == _lastMilliseconds ? _sequence + 1 : 0;
        _lastMilliseconds = milliseconds;
        return string.Create(CultureInfo.InvariantCulture, $"{milliseconds}-{_sequence}");
    }
}
