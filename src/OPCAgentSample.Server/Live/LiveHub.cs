using OPCAgentSample.Server.Observability;
using StackExchange.Redis;

namespace OPCAgentSample.Server.Live;

/// <summary>Eine offene Live-Verbindung: sammelt Änderungsmeldungen und weckt die Verbindungsschleife.</summary>
public sealed class LiveSubscription : IDisposable
{
    private readonly LiveHub _hub;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private int _pending;

    internal LiveSubscription(LiveHub hub, string ownerKey)
    {
        _hub = hub;
        OwnerKey = ownerKey;
    }

    public string OwnerKey { get; }

    internal void Raise(LiveChangeKind kinds, string? ownerKey)
    {
        // Der Postfach-Zähler gehört einem Besitzer; für andere ändert sich nichts.
        if (ownerKey is not null && ownerKey != OwnerKey) kinds &= ~LiveChangeKind.Inbox;
        if (kinds == LiveChangeKind.None) return;

        _ = Interlocked.Or(ref _pending, (int)kinds);
        try
        {
            _ = _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Die Schleife ist bereits geweckt; die Änderungen sammeln sich in _pending.
        }
    }

    /// <summary>Wartet auf Änderungen oder das Ende der Zeit; liefert die seit dem letzten Aufruf gesammelten Änderungen.</summary>
    public async Task<LiveChangeKind> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _ = await _signal.WaitAsync(timeout, cancellationToken);
        return (LiveChangeKind)Interlocked.Exchange(ref _pending, 0);
    }

    public void Dispose() => _hub.Remove(this);
}

/// <summary>
/// Verteilt Änderungsmeldungen (aus Redis-Pub/Sub) an die offenen Live-Verbindungen dieser Instanz und begrenzt die
/// Verbindungen je Besitzer.
/// </summary>
public sealed class LiveHub
{
    private readonly object _gate = new();
    private readonly List<LiveSubscription> _subscriptions = [];

    /// <summary>Anzahl offener Verbindungen dieser Instanz.</summary>
    public int ConnectionCount
    {
        get
        {
            lock (_gate) return _subscriptions.Count;
        }
    }

    /// <summary>Meldet eine Verbindung an; <see langword="null"/>, wenn der Besitzer sein Limit erreicht hat.</summary>
    public LiveSubscription? TryRegister(string ownerKey, int maxPerOwner)
    {
        lock (_gate)
        {
            if (_subscriptions.Count(s => s.OwnerKey == ownerKey) >= maxPerOwner) return null;

            var subscription = new LiveSubscription(this, ownerKey);
            _subscriptions.Add(subscription);
            AppTelemetry.SseOpened();
            return subscription;
        }
    }

    internal void Remove(LiveSubscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.Remove(subscription)) AppTelemetry.SseClosed();
        }
    }

    public void Dispatch(LiveChangeKind kinds, string? ownerKey)
    {
        LiveSubscription[] snapshot;
        lock (_gate) snapshot = [.. _subscriptions];

        foreach (var subscription in snapshot) subscription.Raise(kinds, ownerKey);
    }
}

/// <summary>
/// Abonniert den Pub/Sub-Kanal <c>live:A</c> einmal je Instanz. StackExchange.Redis stellt das Abonnement nach einem
/// Verbindungsabbruch wieder her; weil dabei Meldungen verloren gehen können, gleichen alle Verbindungen danach alles ab.
/// </summary>
public sealed partial class LiveSubscriberService(IConnectionMultiplexer connection, LiveHub hub, ILogger<LiveSubscriberService> logger)
    : BackgroundService
{
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = RedisChannel.Literal(LiveChannel.Name());
        void OnRestored(object? sender, ConnectionFailedEventArgs args) => hub.Dispatch(LiveChangeKind.All, null);
        connection.ConnectionRestored += OnRestored;
        try
        {
            await SubscribeWithRetryAsync(channel, stoppingToken);
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Herunterfahren.
        }
        finally
        {
            connection.ConnectionRestored -= OnRestored;
        }
    }

    private async Task SubscribeWithRetryAsync(RedisChannel channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await connection.GetSubscriber().SubscribeAsync(channel, (_, value) =>
                {
                    if (LiveChannel.TryParse(value, out var kinds, out var owner)) hub.Dispatch(kinds, owner);
                });
                return;
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                LogSubscribeFailed(ex.GetType().Name);
                await Task.Delay(s_retryDelay, cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live-Kanal nicht abonniert ({Error}); neuer Versuch.")]
    private partial void LogSubscribeFailed(string error);
}
