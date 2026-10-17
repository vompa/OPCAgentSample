using StackExchange.Redis;

namespace OPCAgentSample.Rollup;

/// <summary>
/// Sammelt die Schreibbefehle eines Fensters in einer <c>MULTI</c>/<c>EXEC</c>-Transaktion. Ein Abbruch hinterlässt nie einen
/// halben Stand; ein erneutes Schreiben desselben Fensters überschreibt nur dieselben Werte.
/// </summary>
internal sealed class TxWriter(IDatabase database)
{
    private readonly ITransaction _transaction = database.CreateTransaction();
    private readonly List<Task> _tasks = [];

    public int Commands => _tasks.Count;

    public void HashSet(string key, HashEntry[] entries, TimeSpan? ttl = null)
    {
        if (entries.Length == 0) return;

        _tasks.Add(_transaction.HashSetAsync(key, entries));
        if (ttl is { } lifetime) _tasks.Add(_transaction.KeyExpireAsync(key, lifetime));
    }

    public void HashIncrement(string key, string field, long by) => _tasks.Add(_transaction.HashIncrementAsync(key, field, by));

    public void HashSetIfAbsent(string key, string field, RedisValue value, TimeSpan ttl)
    {
        _tasks.Add(_transaction.HashSetAsync(key, field, value, When.NotExists));
        _tasks.Add(_transaction.KeyExpireAsync(key, ttl));
    }

    public void SetIfAbsent(string key, RedisValue value) => _tasks.Add(_transaction.StringSetAsync(key, value, when: When.NotExists));

    public void HashDelete(string key, string field) => _tasks.Add(_transaction.HashDeleteAsync(key, field));

    public void Delete(string key) => _tasks.Add(_transaction.KeyDeleteAsync(key));

    public void Expire(string key, TimeSpan ttl) => _tasks.Add(_transaction.KeyExpireAsync(key, ttl));

    public void SortedSetReplace(string key, SortedSetEntry[] entries, TimeSpan ttl)
    {
        _tasks.Add(_transaction.KeyDeleteAsync(key));
        if (entries.Length == 0) return;

        _tasks.Add(_transaction.SortedSetAddAsync(key, entries));
        _tasks.Add(_transaction.KeyExpireAsync(key, ttl));
    }

    public void SortedSetAdd(string key, string member, double score) => _tasks.Add(_transaction.SortedSetAddAsync(key, member, score));

    public void SetAdd(string key, string member) => _tasks.Add(_transaction.SetAddAsync(key, member));

    public void SetAdd(string key, RedisValue[] members) => _tasks.Add(_transaction.SetAddAsync(key, members));

    public void StreamAdd(string key, string id, NameValueEntry[] fields, int? maxLength = null) =>
        _tasks.Add(_transaction.StreamAddAsync(key, fields, id, maxLength, useApproximateMaxLength: false));

    public void StreamDelete(string key, string id) => _tasks.Add(_transaction.StreamDeleteAsync(key, [id]));


    /// <summary>Führt die Transaktion aus; schlägt ein Befehl fehl, wird das hier gemeldet.</summary>
    public async Task ExecuteAsync()
    {
        if (!await _transaction.ExecuteAsync()) throw new InvalidOperationException("Die Redis-Transaktion wurde nicht ausgeführt.");

        await Task.WhenAll(_tasks);
    }
}
