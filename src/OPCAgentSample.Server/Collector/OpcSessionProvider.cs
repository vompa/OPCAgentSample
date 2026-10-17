using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ISession = Opc.Ua.Client.ISession;
using OPCAgentSample.Domain.Opc;

namespace OPCAgentSample.Server.Collector;

/// <summary>Eine aufgebaute OPC-Sitzung. <see cref="Lost"/> wird ausgelöst, sobald die Sitzung nicht mehr nutzbar ist.</summary>
public sealed class OpcConnection(ISession session, ushort namespaceIndex, CancellationToken lost)
{
    public ISession Session { get; } = session;

    /// <summary>Index des Anlagen-Namespaces (<see cref="NodeIdScheme.NamespaceUri"/>) auf diesem Server.</summary>
    public ushort NamespaceIndex { get; } = namespaceIndex;

    public CancellationToken Lost { get; } = lost;

    public NodeId Node(string identifier) => new(identifier, NamespaceIndex);
}

/// <summary>
/// Hält genau eine OPC-Sitzung zum Simulator (oder einer echten Anlage) offen. Fällt sie aus (KeepAlive schlägt fehl, Server
/// weg), baut der Dienst sie mit exponentiellem Backoff neu auf; Abonnenten und Abrufe holen sich die jeweils aktuelle
/// <see cref="OpcConnection"/>. Beim Aufbau werden Simulationszeit und Beginn der Historie gelesen und die Uhr gesetzt.
/// </summary>
public sealed partial class OpcSessionProvider(
    IOptions<OpcOptions> options,
    OpcSimulationClock clock,
    TimeProvider timeProvider,
    ILogger<OpcSessionProvider> logger) : BackgroundService
{
    /// <summary>Telemetrie-Kontext des OPC-Stacks (ohne Ausgabe); auch für Subscriptions.</summary>
    internal static ITelemetryContext Telemetry { get; } = DefaultTelemetry.Create(_ => { });

    private readonly object _sync = new();
    private TaskCompletionSource<OpcConnection> _connected = NewSignal();
    private OpcConnection? _current;
    private DateTimeOffset? _historyStartUtc;

    /// <summary>Aktuelle Verbindung oder <see langword="null"/>, solange keine besteht.</summary>
    public OpcConnection? Current
    {
        get
        {
            lock (_sync) return _current;
        }
    }

    /// <summary>Beginn der Historie laut Server (<c>Werk.Simulation.HistorieBeginn</c>); bleibt nach dem ersten Lesen erhalten.</summary>
    public DateTimeOffset? HistoryStartUtc
    {
        get
        {
            lock (_sync) return _historyStartUtc;
        }
    }

    /// <summary>Wartet auf die erste bzw. nächste Verbindung (höchstens <paramref name="timeout"/>, sonst <see cref="TimeoutException"/>).</summary>
    public async Task<OpcConnection> WaitForConnectionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<OpcConnection> pending;
        lock (_sync)
        {
            if (_current is { } current) return current;
            pending = _connected.Task;
        }

        try
        {
            return await pending.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Keine OPC-Verbindung zu {options.Value.Endpoint} innerhalb von {timeout.TotalSeconds:F0} s.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(() => failures = 0, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                var delay = Backoff(failures);
                LogConnectionFailed(options.Value.Endpoint, delay.TotalSeconds, ex);
                await Task.Delay(delay, timeProvider, stoppingToken);
                continue;
            }

            // Die Sitzung war aufgebaut und ist ausgefallen: schnell, aber nicht ohne Pause neu versuchen.
            failures = 1;
            await Task.Delay(Backoff(failures), timeProvider, stoppingToken);
        }
    }

    /// <summary>Wartezeit nach dem <paramref name="failures"/>-ten Fehlschlag: verdoppelt sich bis zur Obergrenze, mit etwas Streuung.</summary>
    internal TimeSpan Backoff(int failures)
    {
        var settings = options.Value;
        var seconds = settings.ReconnectDelay.TotalSeconds * Math.Pow(2, Math.Clamp(failures, 1, 30) - 1);
        var jitter = 1 + ((Random.Shared.NextDouble() * 0.4) - 0.2);
        return TimeSpan.FromSeconds(Math.Min(seconds * jitter, settings.ReconnectMaxDelay.TotalSeconds));
    }

    private async Task RunConnectionAsync(Action connected, CancellationToken stoppingToken)
    {
        var session = await CreateSessionAsync(stoppingToken);
        using var lostSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        session.KeepAliveInterval = (int)options.Value.KeepAliveInterval.TotalMilliseconds;
        session.KeepAlive += (_, e) =>
        {
            if (ServiceResult.IsBad(e.Status)) CancelQuietly(lostSource);
        };

        try
        {
            var connection = await InitializeAsync(session, lostSource.Token);
            connected();
            Publish(connection);
            LogConnected(options.Value.Endpoint, options.Value.UseSecurity);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, lostSource.Token);
            }
            catch (OperationCanceledException)
            {
                // Sitzung ausgefallen oder Dienst beendet
            }
        }
        finally
        {
            Retire(lostSource);
            await CloseAsync(session);
        }

        if (!stoppingToken.IsCancellationRequested) LogConnectionLost(options.Value.Endpoint);
    }

    private async Task<OpcConnection> InitializeAsync(ISession session, CancellationToken cancellationToken)
    {
        var index = session.NamespaceUris.GetIndex(NodeIdScheme.NamespaceUri);
        if (index != NodeIdScheme.ExpectedNamespaceIndex)
        {
            throw new InvalidOperationException(
                $"Der Namespace '{NodeIdScheme.NamespaceUri}' hat auf dem Server den Index {index}, erwartet wird {NodeIdScheme.ExpectedNamespaceIndex}.");
        }

        var connection = new OpcConnection(session, (ushort)index, cancellationToken);
        var nodes = new[] { StationVariables.SimulationTime, StationVariables.SimulationSpeed, StationVariables.HistoryStart }
            .Select(variable => new ReadValueId
            {
                NodeId = connection.Node(NodeIdScheme.SimulationVariable(variable)),
                AttributeId = Attributes.Value,
            })
            .ToArray();
        var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, [.. nodes], cancellationToken);
        var values = response.Results;
        for (var i = 0; i < nodes.Length; i++)
        {
            if (StatusCode.IsBad(values[i].StatusCode))
                throw new InvalidOperationException($"{nodes[i].NodeId.Identifier}: {values[i].StatusCode}");
        }

        lock (_sync) _historyStartUtc = OpcValues.ToUtc(values[2].Value);
        clock.Update(OpcValues.ToUtc(values[0].Value), Convert.ToDouble(values[1].Value, System.Globalization.CultureInfo.InvariantCulture));
        return connection;
    }

    private void Publish(OpcConnection connection)
    {
        TaskCompletionSource<OpcConnection> signal;
        lock (_sync)
        {
            _current = connection;
            signal = _connected;
        }

        signal.TrySetResult(connection);
    }

    private void Retire(CancellationTokenSource lostSource)
    {
        lock (_sync)
        {
            _current = null;
            if (_connected.Task.IsCompleted) _connected = NewSignal();
        }

        clock.Freeze();
        lostSource.Cancel();
    }

    // Das KeepAlive-Ereignis kann nach dem Abbau der Sitzung noch eintreffen.
    private static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Sitzung ist bereits abgebaut
        }
    }

    private static TaskCompletionSource<OpcConnection> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<ISession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var configuration = await CreateConfigurationAsync(settings, cancellationToken);
        var application = new ApplicationInstance(configuration, Telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(false, null, cancellationToken);

        var endpoint = await CoreClientUtils.SelectEndpointAsync(configuration, settings.Endpoint, settings.UseSecurity, Telemetry, cancellationToken);
        var configured = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));
        return await new DefaultSessionFactory(Telemetry).CreateAsync(
            configuration, configured, false, settings.ApplicationName, (uint)settings.OperationTimeout.TotalMilliseconds * 2,
            new UserIdentity(), null, cancellationToken);
    }

    private async Task<ApplicationConfiguration> CreateConfigurationAsync(OpcOptions settings, CancellationToken cancellationToken)
    {
        var pki = settings.PkiPath;
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = settings.ApplicationName,
            ApplicationType = ApplicationType.Client,
            ApplicationUri = $"urn:localhost:{settings.ApplicationName}",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN={settings.ApplicationName}",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
                AutoAcceptUntrustedCertificates = settings.AutoAcceptUntrustedCertificates,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = (int)settings.OperationTimeout.TotalMilliseconds,
                MaxMessageSize = 16 * 1024 * 1024,
                MaxArrayLength = 1_000_000,
                MaxStringLength = 8 * 1024 * 1024,
            },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = (int)settings.OperationTimeout.TotalMilliseconds * 2 },
        };

        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken);

        // Sobald ein CertificateValidation-Handler registriert ist, entscheidet er allein: AutoAcceptUntrustedCertificates
        // allein genügt dann nicht und muss hier umgesetzt werden (Hinweis aus OPCSample/OpcSessionFactory).
        configuration.CertificateValidator.CertificateValidation += (_, e) =>
        {
            if (e.Accept) return;
            if (settings.AutoAcceptUntrustedCertificates && e.Error.StatusCode == Opc.Ua.StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = true;
                LogCertificateAccepted(e.Certificate.Subject, e.Certificate.Thumbprint);
                return;
            }

            LogCertificateRejected(e.Certificate.Subject, e.Certificate.Thumbprint, e.Error.StatusCode.ToString());
        };
        return configuration;
    }

    private static async Task CloseAsync(ISession session)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await session.CloseAsync(timeout.Token);
        }
        catch (Exception)
        {
            // Der Server ist womöglich schon weg; die Sitzung wird in jedem Fall verworfen.
        }

        session.Dispose();
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "OPC-Sitzung zu {Endpoint} aufgebaut (Sicherheit: {UseSecurity}).")]
    private partial void LogConnected(string endpoint, bool useSecurity);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "OPC-Sitzung zu {Endpoint} ist ausgefallen; neuer Versuch.")]
    private partial void LogConnectionLost(string endpoint);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "OPC-Verbindung zu {Endpoint} fehlgeschlagen; neuer Versuch in {Seconds:F1} s.")]
    private partial void LogConnectionFailed(string endpoint, double seconds, Exception exception);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Nicht vertrauenswürdiges OPC-Serverzertifikat automatisch akzeptiert ({Subject}, {Thumbprint}). Nur für Entwicklung und Tests geeignet.")]
    private partial void LogCertificateAccepted(string subject, string thumbprint);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "OPC-Serverzertifikat abgelehnt ({Subject}, {Thumbprint}): {Reason}. Nach Prüfung in <PkiPath>/trusted/certs ablegen.")]
    private partial void LogCertificateRejected(string subject, string thumbprint, string reason);
}
