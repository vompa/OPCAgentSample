using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Configuration;
using OPCAgentSample.Domain;
using OPCAgentSample.Domain.Opc;

namespace OPCAgentSample.Simulator;

/// <summary>
/// Startet den Simulator im Prozess (Konsolen-App und Tests): OPC-UA-Server der Halle plus Aktualisierungsschleife,
/// die die Live-Werte zur Simulationsuhr erneuert. <see cref="DisposeAsync"/> beendet beides sauber.
/// </summary>
public sealed class SimulatorHost : IAsyncDisposable
{
    private const string ApplicationName = "OPCAgentSample.Simulator";
    private static readonly ITelemetryContext s_telemetry = DefaultTelemetry.Create(_ => { });

    private readonly ApplicationInstance _application;
    private readonly PlantServer _server;
    private readonly SimulatorRuntime _runtime;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _updateLoop = Task.CompletedTask;
    private int _disposed;

    private SimulatorHost(ApplicationInstance application, PlantServer server, SimulatorRuntime runtime, ILogger logger)
    {
        _application = application;
        _server = server;
        _runtime = runtime;
        _logger = logger;
    }

    public string EndpointUrl => _runtime.Options.EndpointUrl;

    /// <summary>Simulationsuhr; nur lokal steuerbar, nicht über OPC.</summary>
    public SimulationClock Clock => _runtime.Clock;

    public static async Task<SimulatorHost> StartAsync(SimulatorOptions options, ILogger? logger = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        logger ??= NullLogger.Instance;

        var runtime = SimulatorRuntime.Create(options);
        var configuration = await CreateConfigurationAsync(options, ct);
        var application = new ApplicationInstance(configuration, s_telemetry);
        await EnsureCertificateAsync(application, options, logger, ct);

        var server = new PlantServer(runtime);
        await application.StartAsync(server);

        var host = new SimulatorHost(application, server, runtime, logger);
        try
        {
            host.VerifyNamespaceIndex();
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        // Uhr starten und alle Knoten sofort einmal füllen: ein Client, der direkt nach dem Start verbindet, liest sonst
        // bis zum ersten Takt Zeit und Geschwindigkeit 0 (die Uhr eines Collectors bliebe dann stehen)
        runtime.StartClock();
        host.RefreshOnce();
        host._updateLoop = Task.Run(host.RunUpdateLoopAsync, CancellationToken.None);
        logger.Started(host.EndpointUrl, runtime.Clock.UtcNow, runtime.Clock.Speed, options.EnableSecurity ? "None + Basic256Sha256" : "None");
        return host;
    }

    /// <summary>
    /// Prüft das eigene Zertifikat und legt bei Bedarf ein neues an. Passt ein vorhandenes nicht mehr zur Konfiguration
    /// (z. B. nach geänderter Bind-Adresse), wird es beiseitegelegt (nicht gelöscht) und einmal neu erzeugt.
    /// </summary>
    private static async Task EnsureCertificateAsync(
        ApplicationInstance application, SimulatorOptions options, ILogger logger, CancellationToken ct)
    {
        try
        {
            await application.CheckApplicationInstanceCertificatesAsync(true, null, ct);
        }
        catch (ServiceResultException ex) when (ex.StatusCode == StatusCodes.BadConfigurationError)
        {
            var own = Path.Combine(options.PkiPath, "own");
            var aside = $"{own}.invalid-{DateTime.UtcNow:yyyyMMddHHmmss}";
            Directory.Move(own, aside);
            logger.CertificateReplaced(aside);
            await application.CheckApplicationInstanceCertificatesAsync(true, null, ct);
        }
    }

    private static async Task<ApplicationConfiguration> CreateConfigurationAsync(SimulatorOptions options, CancellationToken ct)
    {
        var pki = options.PkiPath;
        var policies = new ServerSecurityPolicyCollection
        {
            new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.None, SecurityPolicyUri = SecurityPolicies.None },
        };
        if (options.EnableSecurity)
        {
            policies.Add(new ServerSecurityPolicy
            {
                SecurityMode = MessageSecurityMode.SignAndEncrypt,
                SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
            });
        }

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = ApplicationName,
            ApplicationType = ApplicationType.Server,
            ApplicationUri = $"urn:localhost:{ApplicationName}",
            ProductUri = "urn:vompa:opc-agent-sample:simulator:product",
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory",
                    StorePath = Path.Combine(pki, "own"),
                    SubjectName = $"CN={ApplicationName}, O=vompa, DC=localhost",
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "issuer") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(pki, "rejected") },
                // Simulator für Demo und Tests: Client-Zertifikate werden ohne Rückfrage akzeptiert.
                AutoAcceptUntrustedCertificates = true,
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = { options.EndpointUrl },
                SecurityPolicies = policies,
                UserTokenPolicies = { new UserTokenPolicy(UserTokenType.Anonymous) },
                MinRequestThreadCount = 5,
                MaxRequestThreadCount = 100,
                MaxQueuedRequestCount = 200,
            },
        };

        await configuration.ValidateAsync(ApplicationType.Server, ct);
        configuration.CertificateValidator.CertificateValidation += AcceptUntrustedClientCertificate;
        return configuration;
    }

    private static void AcceptUntrustedClientCertificate(CertificateValidator sender, CertificateValidationEventArgs e)
    {
        if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted) e.Accept = true;
    }

    private void VerifyNamespaceIndex()
    {
        var index = _server.CurrentInstance.NamespaceUris.GetIndex(NodeIdScheme.NamespaceUri);
        if (index != NodeIdScheme.ExpectedNamespaceIndex || _server.NodeManager?.PlantNamespaceIndex != NodeIdScheme.ExpectedNamespaceIndex)
        {
            throw new InvalidOperationException(
                $"Der Namespace '{NodeIdScheme.NamespaceUri}' hat den Index {index}, erwartet wird {NodeIdScheme.ExpectedNamespaceIndex}.");
        }
    }

    /// <summary>Schreibt die Live-Werte zur aktuellen Simulationszeit in den Adressraum.</summary>
    private void RefreshOnce()
    {
        var now = _runtime.Clock.UtcNow;
        _runtime.Feed.Advance(now);
        var values = _runtime.Feed.Compute(now);
        _server.NodeManager?.Refresh(values, _runtime.Feed.State.DrainAlarmChanges());
    }

    private async Task RunUpdateLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_runtime.Options.UpdateIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                RefreshOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // normales Beenden
        }
        catch (Exception ex)
        {
            _logger.UpdateLoopFailed(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        await _cts.CancelAsync();
        await _updateLoop;
        await _application.StopAsync();
        _server.Dispose();
        _cts.Dispose();
        _logger.Stopped();
    }
}
