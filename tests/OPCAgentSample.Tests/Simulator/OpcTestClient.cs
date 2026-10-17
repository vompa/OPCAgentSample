using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using OPCAgentSample.Simulator;
using OPCAgentSample.Domain.Opc;

namespace OPCAgentSample.Tests.Simulator;

/// <summary>Seiten eines vollständig abgerufenen HistoryRead und deren Anzahl.</summary>
internal sealed record HistoryResult(List<DataValue> Values, int Pages);

/// <summary>
/// Einfacher OPC-UA-Client für die Simulator-Tests (angelehnt an OpcSessionFactory aus OPCSample): eigener PKI-Ordner je
/// Client, Zertifikate des Servers werden angenommen, Hilfen für Read, Browse, HistoryRead und Abonnements.
/// </summary>
internal sealed class OpcTestClient : IAsyncDisposable
{
    private static readonly ITelemetryContext s_telemetry = DefaultTelemetry.Create(_ => { });

    private OpcTestClient(ISession session)
    {
        Session = session;
    }

    public ISession Session { get; }

    public ushort NamespaceIndex => (ushort)Session.NamespaceUris.GetIndex(NodeIdScheme.NamespaceUri);

    public NodeId Node(string identifier) => new(identifier, NamespaceIndex);

    public static async Task<OpcTestClient> ConnectAsync(string endpointUrl, string pkiRoot, bool useSecurity = false, CancellationToken ct = default)
    {
        const string applicationName = "SimulatorTestClient";
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = applicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:localhost:{applicationName}",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pkiRoot, "own"),
                    SubjectName = $"CN={applicationName}",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "rejected") },
                AutoAcceptUntrustedCertificates = true,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 60000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
        };
        await configuration.ValidateAsync(ApplicationType.Client, ct);

        // Sobald ein Handler registriert ist, entscheidet er allein; AutoAccept muss daher hier umgesetzt werden.
        configuration.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
        };

        var application = new ApplicationInstance(configuration, s_telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(false, null, ct);

        var endpoint = await CoreClientUtils.SelectEndpointAsync(configuration, endpointUrl, useSecurity, s_telemetry, ct);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
        var session = await new DefaultSessionFactory(s_telemetry).CreateAsync(
            configuration, configured, false, applicationName, 60000, new UserIdentity(), null, ct);
        return new OpcTestClient(session);
    }

    public async Task<DataValue> ReadAsync(string identifier, CancellationToken ct = default) =>
        await ReadNodeAsync(Node(identifier), Attributes.Value, ct);

    public async Task<DataValue> ReadNodeAsync(NodeId nodeId, uint attributeId, CancellationToken ct = default)
    {
        var response = await Session.ReadAsync(
            null, 0, TimestampsToReturn.Both, [new ReadValueId { NodeId = nodeId, AttributeId = attributeId }], ct);
        return response.Results[0];
    }

    public async Task<T> ReadValueAsync<T>(string identifier, CancellationToken ct = default)
    {
        var value = await ReadAsync(identifier, ct);
        Assert.True(StatusCode.IsGood(value.StatusCode), $"{identifier}: {value.StatusCode}");
        return (T)value.Value;
    }

    public async Task<List<ReferenceDescription>> BrowseAsync(
        NodeId nodeId, BrowseDirection direction = BrowseDirection.Forward, NodeId? referenceTypeId = null, CancellationToken ct = default)
    {
        var description = new BrowseDescription
        {
            NodeId = nodeId,
            BrowseDirection = direction,
            ReferenceTypeId = referenceTypeId ?? ReferenceTypeIds.HierarchicalReferences,
            IncludeSubtypes = true,
            NodeClassMask = 0,
            ResultMask = (uint)BrowseResultMask.All,
        };
        var response = await Session.BrowseAsync(null, null, 0, [description], ct);
        var references = new List<ReferenceDescription>(response.Results[0].References);
        var point = response.Results[0].ContinuationPoint;
        while (point is { Length: > 0 })
        {
            var next = await Session.BrowseNextAsync(null, false, [point], ct);
            references.AddRange(next.Results[0].References);
            point = next.Results[0].ContinuationPoint;
        }

        return references;
    }

    /// <summary>Kinder (Browse-Namen) eines Knotens.</summary>
    public async Task<List<string>> ChildNamesAsync(string identifier, CancellationToken ct = default) =>
        [.. (await BrowseAsync(Node(identifier), ct: ct)).Select(r => r.BrowseName.Name)];

    /// <summary>Liest ein Zeitfenster; folgt den ContinuationPoints (höchstens <paramref name="maxPages"/> Seiten) und zählt die Seiten.</summary>
    public async Task<HistoryResult> HistoryReadAsync(
        string identifier, DateTime startUtc, DateTime endUtc, uint numValuesPerNode = 0, int maxPages = int.MaxValue, CancellationToken ct = default)
    {
        var details = new ReadRawModifiedDetails
        {
            StartTime = startUtc,
            EndTime = endUtc,
            NumValuesPerNode = numValuesPerNode,
            IsReadModified = false,
            ReturnBounds = false,
        };
        var values = new List<DataValue>();
        var pages = 0;
        byte[]? point = null;
        do
        {
            var node = new HistoryReadValueId { NodeId = Node(identifier), ContinuationPoint = point };
            var response = await Session.HistoryReadAsync(null, new ExtensionObject(details), TimestampsToReturn.Both, false, [node], ct);
            var result = response.Results[0];
            Assert.True(StatusCode.IsGood(result.StatusCode), $"{identifier}: {result.StatusCode}");
            var data = (HistoryData)ExtensionObject.ToEncodeable(result.HistoryData);
            values.AddRange(data.DataValues);
            pages++;
            point = result.ContinuationPoint;
        }
        while (point is { Length: > 0 } && pages < maxPages);

        return new HistoryResult(values, pages);
    }

    /// <summary>Abonniert Alarm-Ereignisse am Server-Objekt (alle Stationen) und fordert die aktiven Alarme per ConditionRefresh an.</summary>
    public async Task<AlarmSubscription> SubscribeAlarmsAsync(CancellationToken ct = default)
    {
        var alarmEvents = new AlarmSubscription();
        var subscription = new Subscription(s_telemetry, new SubscriptionOptions { PublishingInterval = 200, PublishingEnabled = true });
        var item = new MonitoredItem(s_telemetry, new MonitoredItemOptions
        {
            StartNodeId = ObjectIds.Server,
            AttributeId = Attributes.EventNotifier,
            MonitoringMode = MonitoringMode.Reporting,
            SamplingInterval = 0,
            QueueSize = 10000,
            DiscardOldest = true,
            Filter = AlarmSubscription.CreateFilter(),
        });
        item.Notification += alarmEvents.OnNotification;
        subscription.AddItem(item);
        Session.AddSubscription(subscription);
        await subscription.CreateAsync(ct);
        alarmEvents.Attach(subscription, Session);
        return alarmEvents;
    }

    public async ValueTask DisposeAsync()
    {
        await Session.CloseAsync(CancellationToken.None);
        Session.Dispose();
    }
}
