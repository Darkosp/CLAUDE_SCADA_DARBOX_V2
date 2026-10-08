using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace ScadaDarbox.Tools.OpcUaSimulator;

/// <summary>
/// A minimal OPC UA server exposing one simulated pump, so the driver can be exercised
/// end to end without hardware.
/// </summary>
/// <remarks>
/// Not part of the product: it lives under tools/ and nothing in src/ references it.
/// The same shape as the Modbus simulator — the point of both is that a driver is
/// verified against something that actually speaks the protocol.
/// </remarks>
public sealed class SimulatorServer : StandardServer
{
    private PumpNodeManager? _pumps;

    /// <summary>The address space, once the server has started.</summary>
    public PumpNodeManager Pumps =>
        _pumps ?? throw new InvalidOperationException("The server has not started yet.");

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        _pumps = new PumpNodeManager(server, configuration);
        return new MasterNodeManager(server, configuration, null, [_pumps]);
    }

    /// <summary>
    /// Starts a server on the given port and returns it, already accepting sessions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **It offers a secured endpoint by default, and that is ADR-0033 decision 5.** It used to offer
    /// `MessageSecurityMode.None` and nothing else, on the reasoning that matching the driver's own
    /// posture *"keeps the exercise honest rather than testing a configuration nobody runs"*. The
    /// reasoning was right and it pointed the wrong way: the driver's posture was the defect, so the
    /// fixture faithfully reproduced it, and **the unsecured path was the only one anything here ever
    /// exercised** — which is how `useSecurity: false` survived two phases and an ADR of its own.
    /// </para>
    /// <para>
    /// **`None` is still offered beside it**, because the opt-out has to be walkable too, and because a
    /// real plant full of old equipment looks exactly like that. Pass <paramref name="offerSecurity"/>
    /// as false for a server that speaks no security at all — the case decision 1 refuses.
    /// </para>
    /// <para>
    /// User identity stays anonymous: ADR-0033 decision 4 keeps a named user out until something asks
    /// for one, and the client certificate is what identifies the application.
    /// </para>
    /// </remarks>
    public static async Task<SimulatorServer> StartAsync(
        int port,
        CancellationToken cancellationToken,
        bool offerSecurity = true)
    {
        var telemetry = DefaultTelemetry.Create(_ => { });

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "ScadaDarbox Simulator",
            // Deliberately free of the port. The certificate is cached on disk and its
            // subject has to keep matching this URI, so folding a per-run port into it
            // invalidates the certificate on the next run with a different port.
            ApplicationUri = "urn:localhost:scadadarbox:simulator",
            ApplicationType = ApplicationType.Server,
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [$"opc.tcp://localhost:{port}/ScadaDarboxSimulator"],
                // Strongest first. `SelectEndpoint` picks by security level rather than by order, so
                // this is for a reader rather than for the stack.
                SecurityPolicies = offerSecurity
                    ?
                    [
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.SignAndEncrypt,
                            SecurityPolicyUri = SecurityPolicies.Basic256Sha256,
                        },
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None,
                        },
                    ]
                    :
                    [
                        new ServerSecurityPolicy
                        {
                            SecurityMode = MessageSecurityMode.None,
                            SecurityPolicyUri = SecurityPolicies.None,
                        },
                    ],
                UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                MaxSessionCount = 10,
                MinSessionTimeout = 10_000,
                MaxSessionTimeout = 3_600_000,
                DiagnosticsEnabled = false,
            },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "own"),
                    SubjectName = "CN=ScadaDarbox Simulator, O=Darbo",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "trusted"),
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "issuers"),
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "rejected"),
                },
                AutoAcceptUntrustedCertificates = true,
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            TraceConfiguration = new TraceConfiguration(),
        };

        await configuration.ValidateAsync(ApplicationType.Server, cancellationToken).ConfigureAwait(false);

        var application = new ApplicationInstance(configuration, telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(true, null, cancellationToken)
            .ConfigureAwait(false);

        var server = new SimulatorServer();
        await application.StartAsync(server).ConfigureAwait(false);

        return server;
    }
}
