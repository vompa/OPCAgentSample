namespace OPCAgentSample.Server.DemoAccess;

/// <summary>
/// Bremst das Raten des Zugangscodes: je Client-IP 5 Fehlversuche in 10 Minuten, insgesamt 40. Danach wird das Anmelden
/// vorübergehend abgewiesen (429 mit <c>Retry-After</c>). Der Zugangscode ist ein langes Zufallsgeheimnis, die Bremse
/// macht Probieren dennoch unattraktiv.
/// </summary>
public sealed class DemoLoginThrottle(TimeProvider clock)
{
    internal const int MaxFailuresPerClient = 5;
    internal const int MaxFailuresTotal = 40;
    private const int MaxTrackedClients = 10_000;
    private static readonly TimeSpan s_window = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _total = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _clients = new(StringComparer.Ordinal);

    /// <summary>Ist die Anmeldung für diesen Client gerade gesperrt? Dann steht in <paramref name="retryAfter"/> die Wartezeit.</summary>
    public bool IsBlocked(string client, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            Drop(_total, now);
            retryAfter = TimeSpan.Zero;

            if (_total.Count >= MaxFailuresTotal) retryAfter = _total.Peek() + s_window - now;

            if (_clients.TryGetValue(client, out var queue))
            {
                Drop(queue, now);
                if (queue.Count >= MaxFailuresPerClient)
                {
                    var wait = queue.Peek() + s_window - now;
                    if (wait > retryAfter) retryAfter = wait;
                }
            }

            return retryAfter > TimeSpan.Zero;
        }
    }

    public void RecordFailure(string client)
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_clients.Count > MaxTrackedClients) _clients.Clear();

            if (!_clients.TryGetValue(client, out var queue))
            {
                queue = new Queue<DateTimeOffset>();
                _clients[client] = queue;
            }

            queue.Enqueue(now);
            _total.Enqueue(now);
        }
    }

    private static void Drop(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && queue.Peek() + s_window <= now) _ = queue.Dequeue();
    }
}
