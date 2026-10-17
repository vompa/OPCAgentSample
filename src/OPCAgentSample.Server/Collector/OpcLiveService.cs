using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.Opc;
using OPCAgentSample.Server.Ingestion;
using G = OPCAgentSample.Domain.Opc.StationGroups;
using V = OPCAgentSample.Domain.Opc.StationVariables;

namespace OPCAgentSample.Server.Collector;

/// <summary>
/// Live-Pfad des Collectors über Subscriptions. Bei jeder (neuen) Verbindung holt er zuerst alles nach, was seit dem Wasserstand
/// fehlt (Lücke nach einem Ausfall, beim ersten Start die ganze Historie), und abonniert dann die Simulationszeit sowie die
/// Zustände und Takte der Stationen.
/// </summary>
/// <remarks>
/// Die Subscription liefert nur das Signal „es gibt Neues“ und hält die Simulationsuhr aktuell. Die Werte selbst nimmt derselbe
/// <see cref="HistoryIngestor"/> auf wie beim Nachladen: Ein Nachladen des jüngsten Fensters per HistoryRead ist exakt (jeder
/// Takt, jedes Ereignis genau einmal, mit denselben Zeitstempeln wie in der Historie), idempotent und braucht keinen zweiten Pfad
/// für Live-Werte, der sich von der Historie unterscheiden und auseinanderlaufen könnte. Gelesen wird höchstens alle
/// <see cref="OpcOptions.LiveRefreshInterval"/>; Zwischenänderungen werden zusammengefasst.
/// </remarks>
public sealed partial class OpcLiveService(
    OpcSessionProvider sessions,
    OpcSimulationClock clock,
    OpcHistorySource source,
    HistoryIngestor ingestor,
    IOptions<OpcOptions> options,
    ILogger<OpcLiveService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await sessions.WaitForConnectionAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, connection.Lost);
                await RunAsync(connection, linked.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // Verbindung verloren: auf die nächste warten
            }
            catch (Exception ex)
            {
                LogLiveFailed(ex);
                await Task.Delay(options.Value.LiveRefreshInterval, stoppingToken);
            }
        }
    }

    private async Task RunAsync(OpcConnection connection, CancellationToken cancellationToken)
    {
        await IngestAsync(cancellationToken);

        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        await SubscribeAsync(connection, changes.Writer, cancellationToken);
        while (true)
        {
            await changes.Reader.ReadAsync(cancellationToken);
            await IngestAsync(cancellationToken);
            await Task.Delay(options.Value.LiveRefreshInterval, cancellationToken);
        }
    }

    /// <summary>Nimmt alles bis zur Simulationsuhr auf; ein Fehler (z. B. kurzer Ausfall) wird geloggt, der nächste Anlass versucht es erneut.</summary>
    private async Task IngestAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ingestor.IngestAsync(source, clock.UtcNow, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogIngestFailed(ex);
        }
    }

    private async Task SubscribeAsync(OpcConnection connection, ChannelWriter<bool> changes, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var subscription = new Subscription(OpcSessionProvider.Telemetry, new SubscriptionOptions
        {
            PublishingInterval = settings.SamplingIntervalMs,
            PublishingEnabled = true,
        });

        var speed = clock.Speed;
        var timeNode = connection.Node(NodeIdScheme.SimulationVariable(V.SimulationTime));
        var speedNode = connection.Node(NodeIdScheme.SimulationVariable(V.SimulationSpeed));

        subscription.AddItem(NewItem(timeNode, settings, (_, e) =>
        {
            if (Value(e) is { Value: DateTime time }) clock.Update(OpcValues.ToUtc(time), speed);
        }));
        subscription.AddItem(NewItem(speedNode, settings, (_, e) =>
        {
            if (Value(e) is not { } value || StatusCode.IsBad(value.StatusCode)) return;
            speed = OpcValues.ToDouble(value.Value);
            clock.Update(clock.UtcNow, speed);
        }));

        void Signal(MonitoredItem item, MonitoredItemNotificationEventArgs e) => changes.TryWrite(true);
        foreach (var station in PlantLayout.HallAStations)
        {
            subscription.AddItem(NewItem(connection.Node(NodeIdScheme.Variable(station, G.State, V.OperatingState)), settings, Signal));
            if (station.Type == StationType.RotaryTable)
                subscription.AddItem(NewItem(connection.Node(NodeIdScheme.Variable(station, G.Process, V.CycleTime)), settings, Signal));
        }

        connection.Session.AddSubscription(subscription);
        await subscription.CreateAsync(cancellationToken);
        var items = subscription.MonitoredItems.Count();
        LogSubscribed(items);
    }

    private static MonitoredItem NewItem(NodeId nodeId, OpcOptions settings, MonitoredItemNotificationEventHandler handler)
    {
        var item = new MonitoredItem(OpcSessionProvider.Telemetry, new MonitoredItemOptions
        {
            StartNodeId = nodeId,
            AttributeId = Attributes.Value,
            MonitoringMode = MonitoringMode.Reporting,
            SamplingInterval = settings.SamplingIntervalMs,
            QueueSize = 1,
            DiscardOldest = true,
        });
        item.Notification += handler;
        return item;
    }

    private static DataValue? Value(MonitoredItemNotificationEventArgs e) => (e.NotificationValue as MonitoredItemNotification)?.Value;

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "OPC-Subscription mit {Items} Knoten eingerichtet.")]
    private partial void LogSubscribed(int items);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Aufnahme über OPC fehlgeschlagen; der nächste Anlass versucht es erneut.")]
    private partial void LogIngestFailed(Exception exception);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Error, Message = "Der OPC-Live-Pfad ist fehlgeschlagen; neuer Versuch.")]
    private partial void LogLiveFailed(Exception exception);
}
